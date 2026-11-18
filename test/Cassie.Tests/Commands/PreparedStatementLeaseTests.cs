using System.Collections.Concurrent;
using System.Data;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands;

public sealed class PreparedStatementLeaseTests
{
    [Fact]
    public async Task ShouldPreserveStatementAndSessionGivenEncodingFailureWhenCommandWouldBePreparedAgain()
    {
        // Arrange
        ConcurrentQueue<string> prepared = new();
        ConcurrentQueue<string> closed = new();
        ConcurrentQueue<string> bound = new();
        using ScriptedServer server = new(session => ServePreparedAsync(session, prepared, closed, bound));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var identity = connection.SessionIdentity;
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT @value", CommandTimeout = 3 };
        CassieParameter parameter = new()
        {
            ParameterName = "value",
            Value = 7,
            PostgresTypeOid = 23,
            ParameterFormat = CassieParameterFormat.Binary
        };
        _ = command.Parameters.Add(parameter);
        await command.PrepareAsync(TestContext.Current.CancellationToken);
        var firstStatement = Assert.Single(prepared);
        command.CommandText = "SELECT @value + 1";
        parameter.PostgresTypeOid = 20;
        parameter.Value = "not-an-integer";

        // Act
        _ = await Assert.ThrowsAsync<NotSupportedException>(() =>
            command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        string[] preparedAfterFailure = [.. prepared];
        string[] closedAfterFailure = [.. closed];
        var stateAfterFailure = connection.State;
        var identityAfterFailure = connection.SessionIdentity;
        parameter.Value = 9L;
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await command.DisposeAsync();

        // Assert
        Assert.Equal([firstStatement], preparedAfterFailure);
        Assert.Empty(closedAfterFailure);
        Assert.Equal(ConnectionState.Open, stateAfterFailure);
        Assert.Equal(identity, identityAfterFailure);
        Assert.Equal(7, result);
        Assert.Equal(2, prepared.Count);
        Assert.Equal(prepared, closed);
    }

    [Fact]
    public async Task ShouldReprepareWithoutClosingStaleStatementGivenReopenedConnectionWhenCommandIsPreparedAgain()
    {
        // Arrange
        ConcurrentQueue<string> prepared = new();
        ConcurrentQueue<string> closed = new();
        ConcurrentQueue<string> bound = new();
        using ScriptedServer server = new(session => ServePreparedAsync(session, prepared, closed, bound));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT 7", CommandTimeout = 3 };
        await command.PrepareAsync(TestContext.Current.CancellationToken);
        var first = Assert.Single(prepared);
        await connection.CloseAsync();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Act
        await command.PrepareAsync(TestContext.Current.CancellationToken);
        string[] beforeDisposal = [.. closed];
        await command.DisposeAsync();

        // Assert
        Assert.Equal(2, prepared.Count);
        var second = prepared.Last();
        Assert.NotEqual(first, second);
        Assert.Empty(beforeDisposal);
        Assert.Equal([second], closed);
    }

    [Fact]
    public async Task ShouldReuseStatementGivenSameSqlAndTypeWhenBoundValueChanges()
    {
        // Arrange
        ConcurrentQueue<string> prepared = new();
        ConcurrentQueue<string> closed = new();
        ConcurrentQueue<string> bound = new();
        using ScriptedServer server = new(session => ServePreparedAsync(session, prepared, closed, bound));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT @value", CommandTimeout = 3 };
        CassieParameter parameter = new() { ParameterName = "value", Value = 7 };
        _ = command.Parameters.Add(parameter);
        await command.PrepareAsync(TestContext.Current.CancellationToken);

        // Act
        var first = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        parameter.Value = 9;
        var second = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await command.DisposeAsync();

        // Assert
        Assert.Equal(7, first);
        Assert.Equal(7, second);
        var statement = Assert.Single(prepared);
        Assert.Equal([statement, statement], bound);
        Assert.Equal([statement], closed);
    }

    [Fact]
    public async Task ShouldReplaceStatementGivenChangedTypeAndSqlWhenCommandIsPreparedAgain()
    {
        // Arrange
        ConcurrentQueue<string> prepared = new();
        ConcurrentQueue<string> closed = new();
        ConcurrentQueue<string> bound = new();
        using ScriptedServer server = new(session => ServePreparedAsync(session, prepared, closed, bound));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT @value", CommandTimeout = 3 };
        CassieParameter parameter = new() { ParameterName = "value", DbType = DbType.Int32, Value = 7 };
        _ = command.Parameters.Add(parameter);
        await command.PrepareAsync(TestContext.Current.CancellationToken);

        // Act
        parameter.DbType = DbType.Int64;
        await command.PrepareAsync(TestContext.Current.CancellationToken);
        command.CommandText = "SELECT @value + 1";
        await command.PrepareAsync(TestContext.Current.CancellationToken);
        await command.DisposeAsync();

        // Assert
        Assert.Equal(3, prepared.Count);
        Assert.Equal(3, prepared.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(prepared, closed);
    }

    [Fact]
    public async Task ShouldDeferStatementCloseGivenActiveReaderWhenCommandIsDisposed()
    {
        // Arrange
        ConcurrentQueue<string> prepared = new();
        ConcurrentQueue<string> closed = new();
        ConcurrentQueue<string> bound = new();
        using ScriptedServer server = new(session => ServePreparedAsync(session, prepared, closed, bound));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT 7", CommandTimeout = 3 };
        await command.PrepareAsync(TestContext.Current.CancellationToken);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        // Act
        await command.DisposeAsync();
        string[] beforeReaderCloses = [.. closed];
        var read = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var value = reader.GetInt32(0);
        await reader.DisposeAsync();
        using CassieCommand followup = new() { Connection = connection, CommandText = "SELECT 7" };
        var next = await followup.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(beforeReaderCloses);
        Assert.True(read);
        Assert.Equal(7, value);
        Assert.Equal(7, next);
        Assert.Equal(prepared, closed);
        _ = Assert.Single(closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDrainGroupedClosesGivenDeferredStatementsWhenReaderFinishes(bool loseSync)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using ScriptedServer server = new(async session =>
        {
            Assert.Equal((byte)'Q', (await session.ReadFrameAsync()).Tag);
            await session.WriteIntRowDescriptionAsync();
            await session.WriteCommandCompleteAsync("SELECT 0");
            await session.WriteReadyAsync();
            for (var group = 0; group < 2; group++)
            {
                var count = group == 0 ? 64 : 2;
                List<string> names = [];
                for (var index = 0; index < count; index++)
                {
                    var (tag, payload) = await session.ReadFrameAsync();
                    Assert.Equal((byte)'C', tag);
                    Assert.Equal((byte)'S', (await session.ReadFrameAsync()).Tag);
                    var offset = 1;
                    names.Add(PgPayload.ReadCString(payload, ref offset));
                }

                if (loseSync)
                {
                    await session.WriteReadyAsync();
                    return;
                }

                Assert.Equal(count, names.Distinct(StringComparer.Ordinal).Count());
                for (var index = 0; index < count; index++)
                {
                    if (group == 0 && index < 3)
                    {
                        var state = index == 0 ? "26000" : "XX000";
                        var message = index == 1 ? "first failure" : "other failure";
                        await BatchPeer.ReplyWithErrorAsync(session, state, message);
                    }
                    else
                    {
                        await session.WriteAsync((byte)'3', []);
                    }

                    await session.WriteReadyAsync();
                }
            }

            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1";
        await using var reader = await query.ExecuteReaderAsync(token);
        for (var index = 0; index < 66; index++)
        {
            await connection.CloseServerStatementAsync(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                connection.SessionIdentity, token);
        }

        await connection.CloseServerStatementAsync("0", connection.SessionIdentity, token);

        // Act
        var failure = await Record.ExceptionAsync(() => reader.CloseAsync().WaitAsync(TimeSpan.FromSeconds(4), token));

        // Assert
        if (loseSync)
        {
            _ = Assert.IsType<IOException>(failure);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        else
        {
            Assert.Equal("first failure", Assert.IsType<CassieException>(failure).Message);
            Assert.Equal(ConnectionState.Open, connection.State);
            _ = await query.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ServePreparedAsync(ScriptedSession session, ConcurrentQueue<string> prepared,
        ConcurrentQueue<string> closed, ConcurrentQueue<string> bound)
    {
        while (true)
        {
            var (tag, payload) = await session.ReadFrameAsync();
            switch (tag)
            {
                case (byte)'P':
                    var parseOffset = 0;
                    prepared.Enqueue(PgPayload.ReadCString(payload, ref parseOffset));
                    await session.WriteAsync((byte)'1', []);
                    break;
                case (byte)'D':
                    if (payload[0] == (byte)'S')
                    {
                        await session.WriteAsync((byte)'t', [0, 0]);
                        await session.WriteAsync((byte)'n', []);
                    }
                    else
                    {
                        await session.WriteIntRowDescriptionAsync();
                    }

                    break;
                case (byte)'B':
                    var bindOffset = 0;
                    _ = PgPayload.ReadCString(payload, ref bindOffset);
                    bound.Enqueue(PgPayload.ReadCString(payload, ref bindOffset));
                    await session.WriteAsync((byte)'2', []);
                    break;
                case (byte)'E':
                    await session.WriteTextRowAsync("7");
                    await session.WriteCommandCompleteAsync("SELECT 1");
                    break;
                case (byte)'C':
                    var closeOffset = 1;
                    closed.Enqueue(PgPayload.ReadCString(payload, ref closeOffset));
                    await session.WriteAsync((byte)'3', []);
                    break;
                case (byte)'S':
                    await session.WriteReadyAsync();
                    break;
                case (byte)'H':
                    break;
                case (byte)'Q':
                    await session.WriteIntRowDescriptionAsync();
                    await session.WriteTextRowAsync("7");
                    await session.WriteCommandCompleteAsync("SELECT 1");
                    await session.WriteReadyAsync();
                    break;
                case (byte)'X':
                    return;
                default:
                    throw new IOException("The scripted prepared-statement peer received an unexpected message.");
            }
        }
    }
}
