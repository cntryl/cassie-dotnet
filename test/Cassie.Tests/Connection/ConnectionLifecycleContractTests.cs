using System.Data;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

[Collection(nameof(PoolLifecycleContractTests))]
public sealed class ConnectionLifecycleContractTests
{
    [Fact]
    public async Task ShouldKeepReplacementSessionGivenOldCloseConnectionReaderWhenReaderClosesLate()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using ScriptedServer server = new(session => session.ServeQueriesAsync(async _ =>
        {
            await session.WriteIntRowDescriptionAsync();
            await session.WriteTextRowAsync("42");
            await session.WriteCommandCompleteAsync("SELECT 1");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42";
        await using var retired = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, token);
        await connection.CloseAsync();
        await connection.OpenAsync(token);

        // Act
        await retired.CloseAsync();

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(42, await command.ExecuteScalarAsync(token));
        Assert.Equal(2, server.Accepted);
    }

    [Fact]
    public void ShouldCloseConnectionGivenSyncDisposeWhenOpenConnectionIsDisposed()
    {
        // Arrange
        using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => Task.CompletedTask));
        CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        connection.Open();

        // Act
        connection.Dispose();

        // Assert
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.True(ScriptedServer.WaitUntil(() => server.Terminated == 1));
    }

    [Fact]
    public void ShouldReturnSessionToPoolGivenSyncDisposeWhenMaximumPoolSizeIsOne()
    {
        // Arrange
        using ScriptedServer server = new(session => session.ServeQueriesAsync(session.WriteCommandCompleteAsync));
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1");
        CassieConnection.ClearAllPools();
        using (CassieConnection first = new() { ConnectionString = connectionString })
        {
            first.Open();
        }

        // Act
        using CassieConnection second = new() { ConnectionString = connectionString };
        second.Open();

        // Assert
        Assert.Equal(ConnectionState.Open, second.State);
        Assert.Equal(1, server.Accepted);
        CassieConnection.ClearAllPools();
    }

    [Fact]
    public void ShouldNotThrowGivenServerClosedSocketWhenConnectionIsDisposed()
    {
        // Arrange
        using ScriptedServer server = new(session =>
        {
            session.Close();
            return Task.CompletedTask;
        });
        using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        connection.Open();
        Assert.True(ScriptedServer.WaitUntil(() => server.Accepted == 1));

        // Act
        var exception = Record.Exception(connection.Dispose);

        // Assert
        Assert.Null(exception);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
