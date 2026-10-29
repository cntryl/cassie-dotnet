using Cntryl.Cassie.Rest;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    internal sealed class LiveRestSession : IAsyncDisposable
    {
        private readonly List<string> _collections = [];

        private LiveRestSession(CassieRestClient client)
        {
            Client = client;
        }

        public CassieRestClient Client { get; }

        public static async Task<LiveRestSession> OpenAsync()
        {
            string address = Require("CASSIE_DOTNET_TEST_REST_BASE_ADDRESS");
            string username = Require("CASSIE_DOTNET_TEST_REST_USERNAME");
            string password = Require("CASSIE_DOTNET_TEST_REST_PASSWORD");
            CassieRestClient client = new(new Uri(address));
            try
            {
                _ = await client.LoginAsync(username, password, TestContext.Current.CancellationToken);
                return new LiveRestSession(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task<string> CreateCollectionAsync(IReadOnlyList<CassieRestField> fields)
        {
            string name = LiveSession.UniqueName("rest_collection");
            _ = await Client.CreateCollectionAsync(new(name, fields), TestContext.Current.CancellationToken);
            _collections.Add(name);
            return name;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (string name in _collections)
                {
                    _ = await Client.Admin.ExecuteQueryAsync(new("postgres", $"DROP TABLE IF EXISTS {name}"), CancellationToken.None);
                }

                await Client.LogoutAsync(CancellationToken.None);
            }
            finally
            {
                Client.Dispose();
            }
        }

        private static string Require(string variable)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (LiveFeature.IsEnabled(LiveEndpoint.RequireLiveVariable))
            {
                Assert.Fail($"{variable} is required for live REST coverage.");
            }
            Assert.Skip($"Set {variable} to run live REST contracts.");
            return string.Empty;
        }
    }
}
