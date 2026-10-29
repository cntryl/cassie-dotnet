using Cntryl.Cassie.Connection;
using Cntryl.Cassie.DependencyInjection.Tests.Infrastructure;
using Cntryl.Cassie.Rest;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cntryl.Cassie.DependencyInjection.Tests
{
    public sealed class DataSourceRegistrationTests
    {
        private const string Settings = "Host=localhost;Database=ledger;Username=app;Pooling=false";

        [Fact]
        public void ShouldShareSourceAndScopeConnectionsGivenValidatedProviderWhenTypedAndBaseServicesAreResolved()
        {
            // Arrange
            ServiceCollection services = new();
            _ = services.AddCassieDataSource(Settings);
            using ServiceProvider provider = BuildProvider(services);
            using IServiceScope first = provider.CreateScope();
            using IServiceScope second = provider.CreateScope();

            // Act
            CassieDataSource source = provider.GetRequiredService<CassieDataSource>();
            CassieConnection firstConnection = first.ServiceProvider.GetRequiredService<CassieConnection>();
            CassieConnection secondConnection = second.ServiceProvider.GetRequiredService<CassieConnection>();

            // Assert
            Assert.Same(source, provider.GetRequiredService<DbDataSource>());
            Assert.Same(source, first.ServiceProvider.GetRequiredService<CassieDataSource>());
            Assert.Same(source, second.ServiceProvider.GetRequiredService<CassieDataSource>());
            Assert.Same(firstConnection, first.ServiceProvider.GetRequiredService<CassieConnection>());
            Assert.Same(firstConnection, first.ServiceProvider.GetRequiredService<DbConnection>());
            Assert.Same(secondConnection, second.ServiceProvider.GetRequiredService<DbConnection>());
            Assert.NotSame(firstConnection, secondConnection);
            Assert.Equal(ConnectionState.Closed, firstConnection.State);
            Assert.Equal(ConnectionState.Closed, secondConnection.State);
            Assert.Equal(Settings, source.ConnectionString);
            Assert.Equal(Settings, firstConnection.ConnectionString);
            Assert.Null(source.LoggerFactory);
            Assert.Null(firstConnection.LoggerFactory);
        }

        [Fact]
        public void ShouldRejectRootConnectionResolutionGivenScopeValidationWhenScopedServicesAreRequested()
        {
            // Arrange
            ServiceCollection services = new();
            _ = services.AddCassieDataSource(Settings);
            using ServiceProvider provider = BuildProvider(services);

            // Act
            Exception? typed = Record.Exception(provider.GetRequiredService<CassieConnection>);
            Exception? alias = Record.Exception(provider.GetRequiredService<DbConnection>);

            // Assert
            _ = Assert.IsType<InvalidOperationException>(typed);
            _ = Assert.IsType<InvalidOperationException>(alias);
        }

        [Fact]
        public void ShouldPreserveOtherProviderAliasesGivenExistingRegistrationsWhenCassieIsAdded()
        {
            // Arrange
            using OtherDataSource otherSource = new();
            using OtherConnection otherConnection = new();
            ServiceCollection services = new();
            _ = services.AddSingleton<DbDataSource>(otherSource);
            _ = services.AddSingleton<DbConnection>(otherConnection);
            _ = services.AddCassieDataSource(Settings);
            using ServiceProvider provider = BuildProvider(services);
            using IServiceScope scope = provider.CreateScope();

            // Act
            DbDataSource selectedSource = provider.GetRequiredService<DbDataSource>();
            DbConnection selectedConnection = scope.ServiceProvider.GetRequiredService<DbConnection>();
            CassieConnection cassieConnection = scope.ServiceProvider.GetRequiredService<CassieConnection>();

            // Assert
            Assert.Same(otherSource, selectedSource);
            Assert.Same(otherConnection, selectedConnection);
            Assert.NotSame(selectedSource, provider.GetRequiredService<CassieDataSource>());
            Assert.NotSame(selectedConnection, cassieConnection);
            Assert.Equal(Settings, cassieConnection.ConnectionString);
            Assert.Equal(ConnectionState.Closed, cassieConnection.State);
        }

        [Fact]
        public void ShouldBorrowLoggerFactoryGivenExistingInstanceWhenScopeAndProviderAreDisposed()
        {
            // Arrange
            using TrackingLoggerFactory loggerFactory = new();
            ServiceCollection services = new();
            _ = services.AddSingleton<ILoggerFactory>(loggerFactory);
            _ = services.AddCassieDataSource(Settings);
            using ServiceProvider provider = BuildProvider(services);
            CassieDataSource source = provider.GetRequiredService<CassieDataSource>();
            CassieConnection connection;

            // Act
            using (IServiceScope scope = provider.CreateScope())
            {
                connection = scope.ServiceProvider.GetRequiredService<CassieConnection>();
                Assert.Same(loggerFactory, connection.LoggerFactory);
            }
            provider.Dispose();

            // Assert
            Assert.Same(loggerFactory, source.LoggerFactory);
            Assert.False(loggerFactory.IsDisposed);
            Assert.NotNull(loggerFactory.CreateLogger("AfterProviderDisposal"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldDisposeOwnedServicesGivenResolvedScopeWhenProviderLifetimesEnd(bool asynchronously)
        {
            // Arrange
            ServiceCollection services = new();
            _ = services.AddCassieDataSource(Settings);
            _ = services.AddCassieRestClient(new Uri("http://127.0.0.1:1/"));
            await using ServiceProvider provider = BuildProvider(services);
            CassieDataSource source = provider.GetRequiredService<CassieDataSource>();
            using CassieConnection directConnection = source.CreateConnection();
            bool directDisposed = false;
            bool scopedDisposed = false;
            directConnection.Disposed += (_, _) => directDisposed = true;
            AsyncServiceScope scope = provider.CreateAsyncScope();
            CassieConnection connection = scope.ServiceProvider.GetRequiredService<CassieConnection>();
            connection.Disposed += (_, _) => scopedDisposed = true;
            CassieRestClient rest = scope.ServiceProvider.GetRequiredService<CassieRestClient>();
            CassieAdminClient admin = scope.ServiceProvider.GetRequiredService<CassieAdminClient>();

            // Act
            if (asynchronously)
            {
                await scope.DisposeAsync();
                await provider.DisposeAsync();
            }
            else
            {
                DisposeSynchronously(scope, provider);
            }

            // Assert
            Assert.True(scopedDisposed);
            Assert.False(directDisposed);
            Assert.Equal(ConnectionState.Closed, connection.State);
            void CreateConnection()
            {
                using CassieConnection created = source.CreateConnection();
            }
            _ = Assert.Throws<ObjectDisposedException>(CreateConnection);
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => rest.GetSessionAsync(TestContext.Current.CancellationToken));
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => admin.ListDatabasesAsync(TestContext.Current.CancellationToken));
        }

        private static ServiceProvider BuildProvider(ServiceCollection services)
        {
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        private static void DisposeSynchronously(AsyncServiceScope scope, ServiceProvider provider)
        {
            scope.Dispose();
            provider.Dispose();
        }
    }
}
