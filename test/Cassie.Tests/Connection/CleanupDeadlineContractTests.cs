using System.Data;
using System.Text;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class CleanupDeadlineContractTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReleasePoolSlotGivenSlowRollbackAndSilentResetWhenSharedDeadlineExpires(bool synchronous)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using DeadlineClock clock = new();
        TaskCompletionSource<ScriptedSession> rollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = 0;
        using ScriptedServer server = new(async session =>
        {
            if (Interlocked.Increment(ref sessions) != 1)
            {
                await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
                return;
            }

            await ExpectQueryAsync(session, "BEGIN ISOLATION LEVEL READ COMMITTED");
            await session.WriteCommandCompleteAsync("BEGIN");
            await session.WriteReadyAsync('T');
            await ExpectQueryAsync(session, "ROLLBACK");
            rollback.SetResult(session);
            await resumeRollback.Task;
            await session.WriteCommandCompleteAsync("ROLLBACK");
            await session.WriteReadyAsync();
            await ExpectQueryAsync(session, "DISCARD ALL");
            reset.SetResult();
            await session.StayVerySilentAsync();
        });
        var connectionString = server.ConnectionString("Maximum Pool Size=1;Command Timeout=0");
        await using CassieConnection connection = new() { ConnectionString = connectionString };
        await using CassieConnection next = new() { ConnectionString = connectionString };
        await connection.OpenAsync(token);
        var transaction = await connection.BeginTransactionAsync(token);
        connection.CleanupTimeProvider = clock;
        // Act
        var closing = synchronous ? Task.Run(connection.Close, token) : connection.CloseAsync();
        try
        {
            _ = await rollback.Task.WaitAsync(TestTimeout, token);
            var opening = next.OpenAsync(token);
            Assert.False(opening.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(4));
            resumeRollback.SetResult();
            await reset.Task.WaitAsync(TestTimeout, token);
            clock.Advance(TimeSpan.FromSeconds(1));

            // Assert
            await closing.WaitAsync(TestTimeout, token);
            await opening.WaitAsync(TestTimeout, token);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Null(transaction.Connection);
            Assert.Equal(2, server.Accepted);
            Assert.Equal([TimeSpan.FromSeconds(5)], clock.InitialDueTimes);
            await next.CloseAsync();
            CassieConnection.ClearPool(next);
        }
        finally
        {
            _ = resumeRollback.TrySetResult();
            if (rollback.Task.IsCompletedSuccessfully)
            {
                (await rollback.Task).Close();
            }
        }
    }

    [Theory]
    [InlineData('I', 1)]
    [InlineData('T', 2)]
    [InlineData('E', 2)]
    public async Task ShouldRequireIdleReadyForQueryGivenResetResponseWhenSessionReturnsToPool(char status, int expectedSessions)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using DeadlineClock clock = new();
        var sessions = 0;
        using ScriptedServer server = new(async session =>
        {
            if (Interlocked.Increment(ref sessions) == 1)
            {
                await ExpectQueryAsync(session, "DISCARD ALL");
                await session.WriteCommandCompleteAsync("DISCARD ALL");
                await session.WriteReadyAsync(status);
            }

            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        });
        var connectionString = server.ConnectionString("Maximum Pool Size=1");
        await using CassieConnection connection = new() { ConnectionString = connectionString, CleanupTimeProvider = clock };
        await connection.OpenAsync(token);
        // Act
        await connection.CloseAsync();
        await using CassieConnection next = new() { ConnectionString = connectionString };
        await next.OpenAsync(token);
        clock.Advance(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(expectedSessions, server.Accepted);
        using var query = next.CreateCommand();
        query.CommandText = "SELECT 1";
        _ = await query.ExecuteNonQueryAsync(token);
        await next.CloseAsync();
        CassieConnection.ClearPool(next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDiscardSessionGivenSilentReaderAndUnlimitedCommandTimeoutWhenReaderIsDisposed(bool synchronous)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using DeadlineClock clock = new();
        TaskCompletionSource<ScriptedSession> active = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            await ExpectQueryAsync(session, "SELECT 1");
            await session.WriteIntRowDescriptionAsync();
            active.SetResult(session);
            await session.StayVerySilentAsync();
        });
        await using CassieConnection connection = new()
        {
            ConnectionString = server.ConnectionString("Maximum Pool Size=1;Command Timeout=0"),
            CleanupTimeProvider = clock
        };
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var reader = await command.ExecuteReaderAsync(token);
        var session = await active.Task.WaitAsync(TestTimeout, token);
        // Act
        var closing = synchronous ? Task.Run(reader.Dispose, token) : reader.DisposeAsync().AsTask();
        try
        {
            await clock.TimerCreated.WaitAsync(TestTimeout, token);
            clock.Advance(TimeSpan.FromSeconds(5));
            // Assert
            await closing.WaitAsync(TestTimeout, token);

            Assert.True(reader.IsClosed);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.CancelRequests);
        }
        finally
        {
            session.Close();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDiscardSessionGivenSilentPeerWhenPreparedCloseDeadlineExpires(bool deferred)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using DeadlineClock clock = new();
        TaskCompletionSource<ScriptedSession> waitingForAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            Assert.Equal((byte)'P', (await session.ReadFrameAsync()).Tag);
            Assert.Equal((byte)'D', (await session.ReadFrameAsync()).Tag);
            Assert.Equal((byte)'S', (await session.ReadFrameAsync()).Tag);
            await session.WriteAsync((byte)'1', []);
            await session.WriteAsync((byte)'t', [0, 0]);
            await session.WriteAsync((byte)'n', []);
            await session.WriteReadyAsync();
            if (deferred)
            {
                await ExpectQueryAsync(session, "SELECT 1");
                await session.WriteIntRowDescriptionAsync();
                await session.WriteCommandCompleteAsync("SELECT 0");
                await session.WriteReadyAsync();
            }

            Assert.Equal((byte)'C', (await session.ReadFrameAsync()).Tag);
            Assert.Equal((byte)'S', (await session.ReadFrameAsync()).Tag);
            waitingForAck.SetResult(session);
            await session.StayVerySilentAsync();
        });
        await using CassieConnection connection = new()
        {
            ConnectionString = server.ConnectionString("Maximum Pool Size=1;Command Timeout=0"),
            CleanupTimeProvider = clock
        };
        await connection.OpenAsync(token);
        var prepared = connection.CreateCommand();
        prepared.CommandText = "SELECT 7";
        await prepared.PrepareAsync(token);
        // Act
        Task closing;
        if (deferred)
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT 1";
            var reader = await query.ExecuteReaderAsync(token);
            await prepared.DisposeAsync();
            closing = reader.NextResultAsync(token);
        }
        else
        {
            closing = prepared.DisposeAsync().AsTask();
        }

        var session = await waitingForAck.Task.WaitAsync(TestTimeout, token);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));

            // Assert
            var failure = await Record.ExceptionAsync(() => closing).AsTask().WaitAsync(TestTimeout, token);
            if (deferred)
            {
                _ = Assert.IsType<TimeoutException>(failure);
            }
            else
            {
                Assert.Null(failure);
            }
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal([TimeSpan.FromSeconds(5)], clock.InitialDueTimes);
        }
        finally
        {
            session.Close();
        }
    }

    private static async Task ExpectQueryAsync(ScriptedSession session, string sql)
    {
        var (tag, payload) = await session.ReadFrameAsync();
        Assert.Equal((byte)'Q', tag);
        Assert.Equal(sql + "\0", Encoding.UTF8.GetString(payload));
    }
}
