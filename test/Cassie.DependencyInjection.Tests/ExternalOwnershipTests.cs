using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests
{
    public sealed class ExternalOwnershipTests
    {
        [Fact]
        public void ShouldKeepSourceCallerOwnedGivenExternalTypedInstanceWhenGenericAliasProviderIsDisposed()
        {
            // Arrange
            using CassieDataSource external = new("Host=external.example;Username=app");
            ServiceCollection services = new();
            _ = services.AddSingleton(external);
            _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
            using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            Assert.Same(external, provider.GetRequiredService<CassieDataSource>());
            Assert.Same(external, provider.GetRequiredService<DbDataSource>());

            // Act
            provider.Dispose();
            Exception? failure = Record.Exception(() =>
            {
                using CassieConnection connection = external.CreateConnection();
                Assert.Equal(ConnectionState.Closed, connection.State);
            });

            // Assert
            Assert.Null(failure);
        }

        [Fact]
        public void ShouldKeepConnectionCallerOwnedGivenExternalTypedInstanceWhenGenericAliasScopeAndProviderAreDisposed()
        {
            // Arrange
            using CassieConnection external = new() { ConnectionString = "Host=external.example;Username=app" };
            bool disposed = false;
            external.Disposed += (_, _) => disposed = true;
            ServiceCollection services = new();
            _ = services.AddSingleton(external);
            _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
            using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            using (IServiceScope scope = provider.CreateScope())
            {
                Assert.Same(external, scope.ServiceProvider.GetRequiredService<CassieConnection>());
                Assert.Same(external, scope.ServiceProvider.GetRequiredService<DbConnection>());
            }

            // Act
            bool disposedByScope = disposed;
            provider.Dispose();

            // Assert
            Assert.False(disposedByScope);
            Assert.False(disposed);
            Assert.Equal("Host=external.example;Username=app", external.ConnectionString);
        }
    }
}
