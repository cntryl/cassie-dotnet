using System.Collections.Concurrent;
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

    [Fact]
    public async Task ShouldRefreshPasswordGivenPooledSourceWhenNewPhysicalConnectionsOpen()
    {
        // Arrange
        ConcurrentQueue<string> passwords = [];
        using ScriptedServer server = new(ServeQueriesAsync, observePassword: passwords.Enqueue);
        var settings = PasswordlessSettings(server);
        var calls = 0;
        await using CassieDataSource source = new(settings, TimeProvider.System,
            _ => ValueTask.FromResult($"token-{Interlocked.Increment(ref calls)}"));
        await using var connection = source.CreateConnection();
        var token = TestContext.Current.CancellationToken;

        // Act
        await connection.OpenAsync(token);
        await connection.CloseAsync();
        await connection.OpenAsync(token);
        await connection.CloseAsync();
        CassieConnection.ClearPool(connection);
        await connection.OpenAsync(token);
        await connection.CloseAsync();

        // Assert
        Assert.Equal(["token-1", "token-2"], [.. passwords]);
        Assert.Equal(2, calls);
        Assert.Equal(2, server.Accepted);
        Assert.Equal(settings, connection.ConnectionString);
        Assert.DoesNotContain("token-", connection.ToString(), StringComparison.Ordinal);
        CassieConnection.ClearPool(connection);
    }

    [Fact]
    public async Task ShouldReleasePoolSlotGivenThrowingPasswordProviderWhenConnectionOpenIsRetried()
    {
        // Arrange
        const string secret = "secret-from-provider-exception";
        using ScriptedServer server = new(ServeQueriesAsync);
        var calls = 0;
        await using CassieDataSource source = new(PasswordlessSettings(server), TimeProvider.System, _ =>
            Interlocked.Increment(ref calls) == 1
                ? ValueTask.FromException<string>(new InvalidOperationException(secret))
                : ValueTask.FromResult("fresh-token"));
        await using var connection = source.CreateConnection();
        var token = TestContext.Current.CancellationToken;

        // Act
        var failure = await Record.ExceptionAsync(() => connection.OpenAsync(token));
        var stateAfterFailure = connection.State;
        await connection.OpenAsync(token);

        // Assert
        var error = Assert.IsType<InvalidOperationException>(failure);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(ConnectionState.Closed, stateAfterFailure);
        Assert.Equal(1, server.Accepted);
        CassieConnection.ClearPool(connection);
    }

    [Theory]
    [InlineData("Password=static")]
    [InlineData("pAsSwOrD=\"\"")]
    public void ShouldRejectPasswordEntryGivenPasswordProviderWhenCreatingSource(string passwordEntry)
    {
        // Arrange
        var settings = $"{Settings};{passwordEntry}";

        // Act
        var failure = Record.Exception(() =>
        {
            using CassieDataSource source = new(settings, TimeProvider.System, _ => ValueTask.FromResult("token"));
        });

        // Assert
        _ = Assert.IsType<ArgumentException>(failure);
    }

    [Fact]
    public async Task ShouldIsolatePoolsGivenDifferentProvidersWhenConnectionsOpen()
    {
        // Arrange
        ConcurrentQueue<string> passwords = [];
        using ScriptedServer server = new(ServeQueriesAsync, observePassword: passwords.Enqueue);
        var settings = PasswordlessSettings(server);
        await using CassieDataSource first = new(settings, TimeProvider.System, _ => ValueTask.FromResult("first"));
        await using CassieDataSource second = new(settings, TimeProvider.System, _ => ValueTask.FromResult("second"));
        await using var firstConnection = first.CreateConnection();
        await using var secondConnection = second.CreateConnection();
        var token = TestContext.Current.CancellationToken;

        // Act
        await firstConnection.OpenAsync(token);
        await firstConnection.CloseAsync();
        await secondConnection.OpenAsync(token);
        await secondConnection.CloseAsync();

        // Assert
        Assert.Equal(["first", "second"], [.. passwords]);
        CassieConnection.ClearPool(firstConnection);
        CassieConnection.ClearPool(secondConnection);
    }

    private static string PasswordlessSettings(ScriptedServer server)
    {
        CassieConnectionStringBuilder settings = [with(server.ConnectionString("Maximum Pool Size=1"))];
        _ = settings.Remove("Password");
        return settings.ConnectionString;
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
