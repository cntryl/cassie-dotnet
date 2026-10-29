using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    /// <summary>
    /// Shared, per-collection description of the live Cassie endpoint. It reads the environment once,
    /// and verifies the endpoint is reachable before any test runs.
    /// </summary>
    public sealed class LiveServerFixture : IAsyncLifetime
    {
        public const string ConnectionStringVariable = "CASSIE_DOTNET_TEST_CONNECTION_STRING";
        public const string RequireLiveVariable = "CASSIE_DOTNET_REQUIRE_LIVE";

        public string? ConnectionString { get; } = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

        public async ValueTask InitializeAsync()
        {
            if (!IsConfigured)
            {
                return;
            }

            await using CassieConnection probe = new() { ConnectionString = ConnectionString };
            await probe.OpenAsync(TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Skips the calling test when no endpoint is configured, or fails it when
        /// <c>CASSIE_DOTNET_REQUIRE_LIVE</c> is true so CI cannot silently lose live coverage.
        /// </summary>
        public string RequireEndpoint()
        {
            if (IsConfigured)
            {
                return ConnectionString!;
            }

            if (LiveFeature.IsEnabled(RequireLiveVariable))
            {
                Assert.Fail($"{RequireLiveVariable} is set but {ConnectionStringVariable} is not.");
            }

            Assert.Skip($"Set {ConnectionStringVariable} to a disposable Cassie pgwire endpoint to run live tests.");
            return string.Empty;
        }
    }

    [CollectionDefinition(Name)]
    public sealed class LiveCollection : ICollectionFixture<LiveServerFixture>
    {
        public const string Name = "Live Cassie server";
    }

    public static class LiveFeature
    {
        public static bool IsEnabled(string variable)
        {
            return string.Equals(Environment.GetEnvironmentVariable(variable), "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Skips the calling test unless the named opt-in server capability is enabled.</summary>
        public static void Require(string suffix)
        {
            Assert.SkipUnless(
                IsEnabled("CASSIE_DOTNET_TEST_" + suffix),
                $"Set CASSIE_DOTNET_TEST_{suffix}=true against a Cassie revision that supports this feature.");
        }
    }
}
