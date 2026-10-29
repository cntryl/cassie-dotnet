using Cntryl.Cassie.Rest;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Cntryl.Cassie.Tests.Rest
{
    public sealed class CassieRestAuthenticationTests
    {
        [Fact]
        public async Task ShouldApplyAndClearOwnedCookieGivenLoginAndLogoutWhenRealHttpRequestsRun()
        {
            // Arrange
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            int step = 0;
            await using ScriptedHttpServer server = new(request =>
            {
                int current = Interlocked.Increment(ref step);
                Assert.DoesNotContain("Authorization", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
                if (current == 1)
                {
                    Assert.Equal("POST", request.Method);
                    Assert.Equal("/v1/auth/login", request.Target);
                    Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.OrdinalIgnoreCase);
                    JsonObject login = JsonNode.Parse(request.Body)!.AsObject();
                    Assert.Equal("root", login["username"]!.GetValue<string>());
                    Assert.Equal("test-password", login["password"]!.GetValue<string>());
                    Assert.False(request.Headers.ContainsKey("Cookie"));
                    return Task.FromResult(new ScriptedHttpReply(200, /*lang=json,strict*/ "{\"user\":\"root\",\"role\":\"root\"}", new Dictionary<string, string>
                    {
                        ["Set-Cookie"] = "cassie_session=test-session; Path=/; HttpOnly; SameSite=Strict"
                    }));
                }

                if (current == 2)
                {
                    Assert.Equal("GET", request.Method);
                    Assert.Equal("/v1/auth/session", request.Target);
                    Assert.Equal("cassie_session=test-session", request.Headers["Cookie"]);
                    return Task.FromResult(new ScriptedHttpReply(200, /*lang=json,strict*/ "{\"user\":\"root\",\"role\":\"root\"}"));
                }

                if (current == 3)
                {
                    Assert.Equal("POST", request.Method);
                    Assert.Equal("/v1/auth/logout", request.Target);
                    Assert.Equal("cassie_session=test-session", request.Headers["Cookie"]);
                    return Task.FromResult(new ScriptedHttpReply(200, /*lang=json,strict*/ "{\"logged_out\":true}", new Dictionary<string, string>
                    {
                        ["Set-Cookie"] = "cassie_session=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"
                    }));
                }

                Assert.Equal(4, current);
                Assert.Equal("/v1/auth/session", request.Target);
                Assert.False(request.Headers.ContainsKey("Cookie"));
                return Task.FromResult(new ScriptedHttpReply(401, /*lang=json,strict*/ "{\"error\":\"unauthorized\"}"));
            });
            using CassieRestClient client = new(server.BaseAddress);

            // Act
            CassieRestSession login = await client.LoginAsync("root", "test-password", deadline.Token);
            CassieRestSession session = await client.GetSessionAsync(deadline.Token);
            await client.LogoutAsync(deadline.Token);
            CassieRestException rejected = await Assert.ThrowsAsync<CassieRestException>(async () => _ = await client.GetSessionAsync(deadline.Token));

            // Assert
            Assert.Equal("root", login.User);
            Assert.Equal("root", session.Role);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal(4, server.Requests);
        }

        [Fact]
        public async Task ShouldKeepCookiesSeparateGivenTwoOwnedClientsWhenSessionsAreRequested()
        {
            // Arrange
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using ScriptedHttpServer server = new(request =>
            {
                if (request.Target == "/v1/auth/login")
                {
                    string user = JsonNode.Parse(request.Body)!["username"]!.GetValue<string>();
                    return Task.FromResult(new ScriptedHttpReply(200, $"{{\"user\":\"{user}\",\"role\":\"{user}\"}}", new Dictionary<string, string>
                    {
                        ["Set-Cookie"] = $"cassie_session={user}-session; Path=/; HttpOnly; SameSite=Strict"
                    }));
                }

                Assert.Equal("/v1/auth/session", request.Target);
                string cookie = request.Headers["Cookie"];
                string authenticated = cookie == "cassie_session=first-session" ? "first" : "second";
                Assert.Equal($"cassie_session={authenticated}-session", cookie);
                return Task.FromResult(new ScriptedHttpReply(200, $"{{\"user\":\"{authenticated}\",\"role\":\"{authenticated}\"}}"));
            });
            using CassieRestClient first = new(server.BaseAddress);
            using CassieRestClient second = new(server.BaseAddress);
            _ = await first.LoginAsync("first", "test-password", deadline.Token);
            _ = await second.LoginAsync("second", "test-password", deadline.Token);

            // Act
            CassieRestSession firstSession = await first.GetSessionAsync(deadline.Token);
            CassieRestSession secondSession = await second.GetSessionAsync(deadline.Token);

            // Assert
            Assert.Equal("first", firstSession.User);
            Assert.Equal("second", secondSession.User);
            Assert.Equal(4, server.Requests);
        }

        [Fact]
        public async Task ShouldLeaveCookieHandlingToBorrowedHandlerGivenSetCookieWhenFakeHandlerHasNoCookieJar()
        {
            // Arrange
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            int step = 0;
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                if (Interlocked.Increment(ref step) == 1)
                {
                    JsonObject login = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    Assert.Equal("root", login["username"]!.GetValue<string>());
                    HttpResponseMessage response = ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"user\":\"root\",\"role\":\"root\"}");
                    Assert.True(response.Headers.TryAddWithoutValidation("Set-Cookie", "cassie_session=opaque; Path=/; HttpOnly"));
                    return response;
                }

                Assert.False(request.Headers.Contains("Cookie"));
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized);
            });
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);
            _ = await client.LoginAsync("root", "test-password", deadline.Token);

            // Act
            CassieRestException error = await Assert.ThrowsAsync<CassieRestException>(async () => _ = await client.GetSessionAsync(deadline.Token));

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
            Assert.Equal(2, handler.Requests);
            Assert.False(http.DefaultRequestHeaders.Contains("Cookie"));
        }
    }
}
