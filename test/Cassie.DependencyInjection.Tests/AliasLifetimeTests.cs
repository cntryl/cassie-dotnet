using System.Data.Common;
using Cntryl.Cassie.Connection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests;

public sealed class AliasLifetimeTests
{
    [Fact]
    public void ShouldPreserveScopedSourceLifetimeGivenExistingTypedFactoryWhenGenericAliasIsResolved()
    {
        // Arrange
        ServiceCollection services = new();
        _ = services.AddScoped(_ => new CassieDataSource("Host=scoped.example;Username=app"));
        _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var typed = first.ServiceProvider.GetRequiredService<CassieDataSource>();
        DbDataSource? alias = null;

        // Act
        var failure = Record.Exception(() => { alias = first.ServiceProvider.GetRequiredService<DbDataSource>(); });

        // Assert
        Assert.Null(failure);
        Assert.Same(typed, alias);
        Assert.Same(alias, first.ServiceProvider.GetRequiredService<DbDataSource>());
        Assert.NotSame(alias, second.ServiceProvider.GetRequiredService<DbDataSource>());
    }

    [Fact]
    public void ShouldPreserveSingletonConnectionLifetimeGivenExistingTypedFactoryWhenAliasScopeIsDisposed()
    {
        // Arrange
        var disposed = false;
        ServiceCollection services = new();
        _ = services.AddSingleton(_ =>
        {
            CassieConnection connection = new() { ConnectionString = "Host=singleton.example;Username=app" };
            connection.Disposed += (_, _) => disposed = true;
            return connection;
        });
        _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        var typed = provider.GetRequiredService<CassieConnection>();

        // Act
        using (var scope = provider.CreateScope())
        {
            Assert.Same(typed, scope.ServiceProvider.GetRequiredService<DbConnection>());
        }

        // Assert
        Assert.False(disposed);
        Assert.Same(typed, provider.GetRequiredService<DbConnection>());
    }
}
