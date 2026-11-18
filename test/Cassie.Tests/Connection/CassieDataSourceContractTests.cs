using System.Data;
using System.Data.Common;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class CassieDataSourceContractTests
{
    private const string Settings = "Host=db.example;Port=6432;Database=ledger;Username=app;SSL Mode=Require";

    [Fact]
    public void ShouldCreateDistinctClosedConnectionsGivenTypedAndBaseSourceWhenConnectionsAreRequested()
    {
        // Arrange
        using CassieDataSource source = new(Settings);
        DbDataSource generic = source;

        // Act
        using var first = source.CreateConnection();
        using var second = source.CreateConnection();
        using var third = generic.CreateConnection();

        // Assert
        Assert.NotSame(first, second);
        _ = Assert.IsType<CassieConnection>(third);
        Assert.Equal(Settings, source.ConnectionString);
        Assert.Equal(Settings, first.ConnectionString);
        Assert.Equal(Settings, second.ConnectionString);
        Assert.Equal(ConnectionState.Closed, first.State);
        Assert.Equal(ConnectionState.Closed, second.State);
        Assert.Equal(ConnectionState.Closed, third.State);
    }

    [Fact]
    public void ShouldReturnTypedSourceGivenProviderFactoryWhenDataSourceIsCreated()
    {
        // Arrange
        DbProviderFactory generic = CassieProviderFactory.Instance;

        // Act
        using var typed = CassieProviderFactory.Instance.CreateDataSource(Settings);
        using var untyped = generic.CreateDataSource(Settings);
        using var connection = typed.CreateConnection();

        // Assert
        _ = Assert.IsType<CassieDataSource>(untyped);
        Assert.Equal(Settings, untyped.ConnectionString);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void ShouldOpenTypedConnectionGivenScriptedEndpointWhenSourceOpensSynchronously()
    {
        // Arrange
        using ScriptedServer server = new(ServeQueriesAsync);
        using CassieDataSource source = new(server.ConnectionString("Pooling=false"));

        // Act
        using var connection = source.OpenConnection();
        using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 7" };
        var value = command.ExecuteScalar();

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(7, value);
    }

    [Fact]
    public async Task ShouldOpenTypedAndBaseConnectionsGivenScriptedEndpointWhenSourceOpensAsynchronously()
    {
        // Arrange
        using ScriptedServer server = new(ServeQueriesAsync);
        await using CassieDataSource source = new(server.ConnectionString("Pooling=false"));
        DbDataSource generic = source;

        // Act
        await using var typed = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var untyped = await generic.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, typed.State);
        Assert.Equal(ConnectionState.Open, untyped.State);
        _ = Assert.IsType<CassieConnection>(untyped);
        Assert.NotSame(typed, untyped);
    }

    [Fact]
    public async Task ShouldPreserveConnectionlessCommandsGivenBaseSourceWhenCommandExecutesTwice()
    {
        // Arrange
        using ScriptedServer server = new(ServeQueriesAsync);
        await using DbDataSource source = new CassieDataSource(server.ConnectionString("Pooling=false"));
        await using var command = source.CreateCommand("SELECT 7");

        // Act
        var first = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        var second = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.True(ScriptedServer.WaitUntil(() => server.Accepted == 2 && server.Terminated == 2));
    }

    [Fact]
    public void ShouldPreserveCallerLoggerGivenDisposedSourceWhenConnectionRemainsCallerOwned()
    {
        // Arrange
        using TrackingLoggerFactory loggerFactory = new();
        using CassieDataSource source = new(Settings) { LoggerFactory = loggerFactory };
        using var connection = source.CreateConnection();

        // Act
        source.Dispose();

        // Assert
        Assert.Same(loggerFactory, source.LoggerFactory);
        Assert.Same(loggerFactory, connection.LoggerFactory);
        Assert.False(loggerFactory.IsDisposed);
        Assert.Equal(Settings, connection.ConnectionString);
    }

    [Fact]
    public void ShouldRejectNewConnectionsGivenDisposedSourceWhenTypedAndBaseFactoriesAreCalled()
    {
        // Arrange
        using CassieDataSource source = new(Settings);
        DbDataSource generic = source;

        // Act
        source.Dispose();

        void CreateTyped()
        {
            using var connection = source.CreateConnection();
        }

        void CreateUntyped()
        {
            using var connection = generic.CreateConnection();
        }

        void OpenTyped()
        {
            using var connection = source.OpenConnection();
        }

        // Assert
        _ = Assert.Throws<ObjectDisposedException>(CreateTyped);
        _ = Assert.Throws<ObjectDisposedException>(CreateUntyped);
        _ = Assert.Throws<ObjectDisposedException>(OpenTyped);
    }

    [Fact]
    public async Task ShouldRejectNewConnectionsGivenAsynchronouslyDisposedSourceWhenBaseAndTypedFactoriesAreCalled()
    {
        // Arrange
        await using CassieDataSource source = new(Settings);
        DbDataSource generic = source;

        // Act
        await source.DisposeAsync();

        void CreateUntyped()
        {
            using var connection = generic.CreateConnection();
        }

        async Task OpenTypedAsync()
        {
            await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        _ = Assert.Throws<ObjectDisposedException>(CreateUntyped);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(OpenTypedAsync);
    }

    [Fact]
    public async Task ShouldKeepCreatedConnectionUsableGivenDisposedSourceWhenCallerOpensAndUsesConnection()
    {
        // Arrange
        using ScriptedServer server = new(ServeQueriesAsync);
        await using CassieDataSource source = new(server.ConnectionString("Pooling=false"));
        await using var connection = source.CreateConnection();

        // Act
        await source.DisposeAsync();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 7" };
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7, value);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldKeepSharedIdlePoolGivenDisposedSourceWhenAnotherSourceOpensConnection()
    {
        // Arrange
        using ScriptedServer server = new(ServeQueriesAsync);
        var connectionString = server.ConnectionString("Pooling=true;Application Name=" + Guid.NewGuid().ToString("N"));
        await using CassieDataSource first = new(connectionString);
        await using CassieDataSource second = new(connectionString);
        await using CassieConnection identity = new() { ConnectionString = connectionString };
        try
        {
            await using (var initial = await first.OpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                await initial.CloseAsync();
            }

            // Act
            await first.DisposeAsync();
            await using var reused = await second.OpenConnectionAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(ConnectionState.Open, reused.State);
            Assert.Equal(1, server.Accepted);
        }
        finally
        {
            CassieConnection.ClearPool(identity);
        }
    }

    private static Task ServeQueriesAsync(ScriptedSession session)
    {
        return session.ServeQueriesAsync(async sql =>
        {
            if (sql == "DISCARD ALL")
            {
                await session.WriteCommandCompleteAsync("DISCARD ALL");
                return;
            }

            await session.WriteIntRowDescriptionAsync();
            await session.WriteTextRowAsync("7");
            await session.WriteCommandCompleteAsync("SELECT 1");
        });
    }

    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        public bool IsDisposed { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
