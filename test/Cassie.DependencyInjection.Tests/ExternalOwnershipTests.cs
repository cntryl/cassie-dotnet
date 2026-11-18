using System.Data;
using System.Data.Common;
using Cntryl.Cassie.Connection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests;

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
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        Assert.Same(external, provider.GetRequiredService<CassieDataSource>());
        Assert.Same(external, provider.GetRequiredService<DbDataSource>());

        // Act
        provider.Dispose();
        var failure = Record.Exception(() =>
        {
            using var connection = external.CreateConnection();
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
        var disposed = false;
        external.Disposed += (_, _) => disposed = true;
        ServiceCollection services = new();
        _ = services.AddSingleton(external);
        _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        using (var scope = provider.CreateScope())
        {
            Assert.Same(external, scope.ServiceProvider.GetRequiredService<CassieConnection>());
            Assert.Same(external, scope.ServiceProvider.GetRequiredService<DbConnection>());
        }

        // Act
        var disposedByScope = disposed;
        provider.Dispose();

        // Assert
        Assert.False(disposedByScope);
        Assert.False(disposed);
        Assert.Equal("Host=external.example;Username=app", external.ConnectionString);
    }
}
