using Cntryl.Cassie.Connection;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests
{
    public sealed class AliasLifetimeTests
    {
        [Fact]
        public void ShouldPreserveScopedSourceLifetimeGivenExistingTypedFactoryWhenGenericAliasIsResolved()
        {
            // Arrange
            ServiceCollection services = new();
            _ = services.AddScoped(_ => new CassieDataSource("Host=scoped.example;Username=app"));
            _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
            using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            using IServiceScope first = provider.CreateScope();
            using IServiceScope second = provider.CreateScope();
            CassieDataSource typed = first.ServiceProvider.GetRequiredService<CassieDataSource>();
            DbDataSource? alias = null;

            // Act
            Exception? failure = Record.Exception(() => { alias = first.ServiceProvider.GetRequiredService<DbDataSource>(); });

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
            bool disposed = false;
            ServiceCollection services = new();
            _ = services.AddSingleton(_ =>
            {
                CassieConnection connection = new() { ConnectionString = "Host=singleton.example;Username=app" };
                connection.Disposed += (_, _) => disposed = true;
                return connection;
            });
            _ = services.AddCassieDataSource("Host=ignored.example;Username=app");
            using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            CassieConnection typed = provider.GetRequiredService<CassieConnection>();

            // Act
            using (IServiceScope scope = provider.CreateScope())
            {
                Assert.Same(typed, scope.ServiceProvider.GetRequiredService<DbConnection>());
            }

            // Assert
            Assert.False(disposed);
            Assert.Same(typed, provider.GetRequiredService<DbConnection>());
        }
    }
}
