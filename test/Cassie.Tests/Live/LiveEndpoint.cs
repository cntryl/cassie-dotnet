using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    /// <summary>
    /// The live Cassie endpoint shared by every live test, read once from the environment.
    /// </summary>
    internal static class LiveEndpoint
    {
        public const string ConnectionStringVariable = "CASSIE_DOTNET_TEST_CONNECTION_STRING";
        public const string RequireLiveVariable = "CASSIE_DOTNET_REQUIRE_LIVE";

        /// <summary>Name of the xUnit collection that serializes live tests so database-wide operations cannot overlap.</summary>
        public const string CollectionName = "Live Cassie server";

        private static readonly string? Configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        /// <summary>
        /// Returns the configured connection string. Skips the calling test when no endpoint is configured,
        /// or fails it when <c>CASSIE_DOTNET_REQUIRE_LIVE</c> is true so CI cannot silently lose live coverage.
        /// </summary>
        public static string Require()
        {
            if (!string.IsNullOrWhiteSpace(Configured))
            {
                return Configured;
            }

            if (LiveFeature.IsEnabled(RequireLiveVariable))
            {
                Assert.Fail($"{RequireLiveVariable} is set but {ConnectionStringVariable} is not.");
            }

            Assert.Skip($"Set {ConnectionStringVariable} to a disposable Cassie pgwire endpoint to run live tests.");
            return string.Empty;
        }
    }

    internal static class LiveFeature
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
