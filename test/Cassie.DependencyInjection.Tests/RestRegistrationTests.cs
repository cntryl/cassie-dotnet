using Cntryl.Cassie.DependencyInjection.Tests.Infrastructure;
using Cntryl.Cassie.Rest;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests
{
    public sealed class RestRegistrationTests
    {
        [Fact]
        public void ShouldShareRestAndAdminWithinScopeGivenValidatedProviderWhenServicesAreResolved()
        {
            // Arrange
            ServiceCollection services = [];
            _ = services.AddCassieRestClient(new Uri("http://127.0.0.1:1/"));
            using ServiceProvider provider = BuildProvider(services);
            using IServiceScope first = provider.CreateScope();
            using IServiceScope second = provider.CreateScope();

            // Act
            CassieRestClient firstClient = first.ServiceProvider.GetRequiredService<CassieRestClient>();
            CassieRestClient secondClient = second.ServiceProvider.GetRequiredService<CassieRestClient>();
            Exception? rootClient = Record.Exception(provider.GetRequiredService<CassieRestClient>);
            Exception? rootAdmin = Record.Exception(provider.GetRequiredService<CassieAdminClient>);

            // Assert
            Assert.Same(firstClient, first.ServiceProvider.GetRequiredService<CassieRestClient>());
            Assert.Same(firstClient.Admin, first.ServiceProvider.GetRequiredService<CassieAdminClient>());
            Assert.Same(secondClient.Admin, second.ServiceProvider.GetRequiredService<CassieAdminClient>());
            Assert.NotSame(firstClient, secondClient);
            Assert.NotSame(firstClient.Admin, secondClient.Admin);
            _ = Assert.IsType<InvalidOperationException>(rootClient);
            _ = Assert.IsType<InvalidOperationException>(rootAdmin);
        }

        [Fact]
        public async Task ShouldIsolateCookiesAndDisposalGivenTwoScopesWhenSessionsAndAdminRequestsRun()
        {
            // Arrange
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await using ScriptedHttpServer server = new(RespondToSession);
            ServiceCollection services = [];
            _ = services.AddCassieRestClient(server.BaseAddress);
            using ServiceProvider provider = BuildProvider(services);
            using IServiceScope first = provider.CreateScope();
            using IServiceScope second = provider.CreateScope();
            CassieRestClient firstClient = first.ServiceProvider.GetRequiredService<CassieRestClient>();
            CassieRestClient secondClient = second.ServiceProvider.GetRequiredService<CassieRestClient>();
            CassieAdminClient firstAdmin = first.ServiceProvider.GetRequiredService<CassieAdminClient>();
            CassieAdminClient secondAdmin = second.ServiceProvider.GetRequiredService<CassieAdminClient>();
            Assert.Equal(0, server.Requests);
            _ = await firstClient.LoginAsync("first", "test-password", deadline.Token);
            _ = await secondClient.LoginAsync("second", "test-password", deadline.Token);

            // Act
            CassieRestSession firstSession = await firstClient.GetSessionAsync(deadline.Token);
            CassieRestSession secondSession = await secondClient.GetSessionAsync(deadline.Token);
            JsonObject firstMetrics = await firstAdmin.GetMetricsAsync(deadline.Token);
            JsonObject secondMetrics = await secondAdmin.GetMetricsAsync(deadline.Token);
            first.Dispose();
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => firstClient.GetSessionAsync(deadline.Token));
            CassieRestSession survivingSession = await secondClient.GetSessionAsync(deadline.Token);

            // Assert
            Assert.Equal("first", firstSession.User);
            Assert.Equal("second", secondSession.User);
            Assert.Equal("first", firstMetrics["user"]!.GetValue<string>());
            Assert.Equal("second", secondMetrics["user"]!.GetValue<string>());
            Assert.Equal("second", survivingSession.User);
            Assert.Same(firstClient.Admin, firstAdmin);
            Assert.Same(secondClient.Admin, secondAdmin);
            Assert.Equal(7, server.Requests);
        }

        [Fact]
        public void ShouldPreserveExistingAdminGivenExplicitRegistrationWhenRestServicesAreAdded()
        {
            // Arrange
            using CassieRestClient external = new(new Uri("http://127.0.0.1:1/external/"));
            ServiceCollection services = [];
            _ = services.AddSingleton(external.Admin);
            _ = services.AddCassieRestClient(new Uri("http://127.0.0.1:1/scoped/"));
            using ServiceProvider provider = BuildProvider(services);
            using IServiceScope scope = provider.CreateScope();

            // Act
            CassieAdminClient selectedAdmin = scope.ServiceProvider.GetRequiredService<CassieAdminClient>();
            CassieRestClient scopedClient = scope.ServiceProvider.GetRequiredService<CassieRestClient>();

            // Assert
            Assert.Same(external.Admin, selectedAdmin);
            Assert.NotSame(external, scopedClient);
            Assert.NotSame(scopedClient.Admin, selectedAdmin);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldHonorExistingSingletonRestOwnershipGivenTypedRegistrationWhenAdminAliasIsResolved(bool callerOwned)
        {
            // Arrange
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await using ScriptedHttpServer server = new(RespondToSession);
            using CassieRestClient external = new(server.BaseAddress);
            ServiceCollection services = [];
            _ = services.Add(callerOwned
                ? ServiceDescriptor.Singleton(external)
                : ServiceDescriptor.Singleton(_ => new CassieRestClient(server.BaseAddress)));
            _ = services.AddCassieRestClient(server.BaseAddress);
            using ServiceProvider provider = BuildProvider(services);
            CassieRestClient client = provider.GetRequiredService<CassieRestClient>();
            CassieAdminClient admin = provider.GetRequiredService<CassieAdminClient>();
            _ = await client.LoginAsync("singleton", "test-password", deadline.Token);

            // Act
            using (IServiceScope scope = provider.CreateScope())
            {
                Assert.Same(client, scope.ServiceProvider.GetRequiredService<CassieRestClient>());
                Assert.Same(admin, scope.ServiceProvider.GetRequiredService<CassieAdminClient>());
            }
            CassieRestSession afterScope = await client.GetSessionAsync(deadline.Token);
            await provider.DisposeAsync();

            // Assert
            Assert.Same(client.Admin, admin);
            Assert.Equal("singleton", afterScope.User);
            if (callerOwned)
            {
                Assert.Same(external, client);
                Assert.Equal("singleton", (await external.GetSessionAsync(deadline.Token)).User);
                Assert.Equal(3, server.Requests);
            }
            else
            {
                Assert.NotSame(external, client);
                _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetSessionAsync(deadline.Token));
                Assert.Equal(2, server.Requests);
            }
        }

        private static Task<ScriptedHttpReply> RespondToSession(ScriptedHttpRequest request)
        {
            Assert.DoesNotContain("Authorization", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            if (request.Target == "/v1/auth/login")
            {
                Assert.Equal("POST", request.Method);
                string user = JsonNode.Parse(request.Body)!["username"]!.GetValue<string>();
                JsonObject body = new() { ["user"] = user, ["role"] = user };
                return Task.FromResult(new ScriptedHttpReply(200, body.ToJsonString(), new Dictionary<string, string>
                {
                    ["Set-Cookie"] = $"cassie_session={user}-session; Path=/; HttpOnly; SameSite=Strict"
                }));
            }

            Assert.Equal("GET", request.Method);
            Assert.True(request.Target is "/v1/auth/session" or "/metrics");
            string cookie = request.Headers["Cookie"];
            Assert.StartsWith("cassie_session=", cookie, StringComparison.Ordinal);
            Assert.EndsWith("-session", cookie, StringComparison.Ordinal);
            string authenticated = cookie["cassie_session=".Length..^"-session".Length];
            JsonObject response = new() { ["user"] = authenticated, ["role"] = authenticated };
            return Task.FromResult(new ScriptedHttpReply(200, response.ToJsonString()));
        }

        private static ServiceProvider BuildProvider(ServiceCollection services)
        {
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
    }
}
