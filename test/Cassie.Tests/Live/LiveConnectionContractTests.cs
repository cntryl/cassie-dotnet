using System.Security.Authentication;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    public sealed class LiveConnectionContractTests(LiveServerFixture server) : LiveTestBase(server)
    {
        [Fact]
        public async Task ShouldApplyApplicationNameGivenStartupOptionsWhenConnectionOpens()
        {
            // Arrange
            string? expected = Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME");
            Assert.SkipWhen(expected is null, "Set CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME and include a matching Options=-c application_name=... in the connection string.");

            // Act
            object? actual = await ExecuteScalarAsync("SELECT current_setting('application_name')");

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
