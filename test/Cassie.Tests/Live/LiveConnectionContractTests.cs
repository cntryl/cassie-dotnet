using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Security.Authentication;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    [Collection(LiveEndpoint.CollectionName)]
    public sealed class LiveConnectionContractTests : IAsyncLifetime
    {
        private readonly LiveSession _live = new();

        public ValueTask InitializeAsync()
        {
            return _live.InitializeAsync();
        }

        public ValueTask DisposeAsync()
        {
            return _live.DisposeAsync();
        }

        [Fact]
        public async Task ShouldApplyApplicationNameGivenStartupOptionsWhenConnectionOpens()
        {
            // Arrange
            string? expected = Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME");
            Assert.SkipWhen(expected is null, "Set CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME and include a matching Options=-c application_name=... in the connection string.");

            // Act
            using CassieCommand show = _live.CreateCommand();
            show.CommandText = "SELECT current_setting('application_name')";
            object? actual = await show.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task ShouldRejectCertificateGivenHostNameMismatchWhenTlsPeerIdentityIsValidated()
        {
            // Arrange
            string? rejected = Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_REJECTED_TLS_CONNECTION_STRING");
            Assert.SkipWhen(rejected is null, "Set CASSIE_DOTNET_TEST_REJECTED_TLS_CONNECTION_STRING to a TLS endpoint addressed by a mismatched host name.");
            await using CassieConnection connection = new() { ConnectionString = rejected };

            // Act
            Exception? exception = await Record.ExceptionAsync(() => connection.OpenAsync(TestContext.Current.CancellationToken));

            // Assert
            _ = Assert.IsType<AuthenticationException>(exception, exactMatch: false);
        }
    }
}
