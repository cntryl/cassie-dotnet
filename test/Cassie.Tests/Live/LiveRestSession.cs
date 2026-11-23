using Cntryl.Cassie.Rest;
using Xunit;

namespace Cntryl.Cassie.Tests.Live;

internal sealed class LiveRestSession : IAsyncDisposable
{
    private readonly List<string> _collections = [];
    private readonly List<string> _databases = [];

    private LiveRestSession(CassieRestClient client)
    {
        Client = client;
    }

    public CassieRestClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var name in _collections)
            {
                _ = await Client.Admin.ExecuteQueryAsync(
                    new CassieAdminQueryRequest("postgres", $"DROP TABLE IF EXISTS {name}"), CancellationToken.None);
            }

            // A leftover database changes how the server handles later DDL on other sessions.
            foreach (var name in _databases)
            {
                _ = await Client.Admin.ExecuteQueryAsync(
                    new CassieAdminQueryRequest("postgres", $"DROP DATABASE {name}"), CancellationToken.None);
            }

            await Client.LogoutAsync(CancellationToken.None);
        }
        finally
        {
            Client.Dispose();
        }
    }

    public static async Task<LiveRestSession> OpenAsync()
    {
        var address = Require("CASSIE_DOTNET_TEST_REST_BASE_ADDRESS");
        var username = Require("CASSIE_DOTNET_TEST_REST_USERNAME");
        var password = Require("CASSIE_DOTNET_TEST_REST_PASSWORD");
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
        var name = LiveSession.UniqueName("rest_collection");
        _ = await Client.CreateCollectionAsync(new CassieCollectionDefinition(name, fields),
            TestContext.Current.CancellationToken);
        _collections.Add(name);
        return name;
    }

    /// <summary>Creates a uniquely named database that this session drops when it is disposed.</summary>
    public async Task<CassieDatabaseInfo> CreateDatabaseAsync()
    {
        var name = LiveSession.UniqueName("rest_database");
        var created = await Client.Admin.CreateDatabaseAsync(name, TestContext.Current.CancellationToken);
        _databases.Add(name);
        return created;
    }

    private static string Require(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

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
