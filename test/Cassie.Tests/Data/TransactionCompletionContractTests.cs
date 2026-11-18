using System.Data;
using System.Text;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Tests.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Data;

public sealed class TransactionCompletionContractTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDiscardSessionGivenActiveReaderWhenTransactionIsDisposed(bool synchronous)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using ScriptedServer server = new(async session =>
        {
            await BeginAsync(session);
            await ExpectQueryAsync(session, "SELECT 1");
            await session.WriteIntRowDescriptionAsync();
            await session.WriteCommandCompleteAsync("SELECT 0");
            await session.WriteReadyAsync('T');
            await session.StayVerySilentAsync();
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        var transaction = await connection.BeginTransactionAsync(token);
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT 1";
        await using var reader = await query.ExecuteReaderAsync(token);

        // Act
        var closing = synchronous ? Task.Run(transaction.Dispose, token) : transaction.DisposeAsync().AsTask();
        await closing.WaitAsync(TestTimeout, token);

        // Assert
        Assert.Null(transaction.Connection);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDiscardSessionGivenSilentRollbackWhenTransactionDisposalDeadlineExpires(bool synchronous)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using DeadlineClock clock = new();
        TaskCompletionSource<ScriptedSession> rollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            await BeginAsync(session);
            await ExpectQueryAsync(session, "ROLLBACK");
            rollback.SetResult(session);
            await session.StayVerySilentAsync();
        });
        await using CassieConnection connection = new()
        {
            ConnectionString = server.ConnectionString("Pooling=false;Command Timeout=0")
        };
        await connection.OpenAsync(token);
        var transaction = await connection.BeginTransactionAsync(token);
        connection.CleanupTimeProvider = clock;

        // Act
        var closing = synchronous ? Task.Run(transaction.Dispose, token) : transaction.DisposeAsync().AsTask();
        try
        {
            _ = await rollback.Task.WaitAsync(TestTimeout, token);
            clock.Advance(TimeSpan.FromSeconds(5));
            await closing.WaitAsync(TestTimeout, token);

            // Assert
            Assert.Null(transaction.Connection);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally
        {
            if (rollback.Task.IsCompletedSuccessfully)
            {
                (await rollback.Task).Close();
            }
            else
            {
                await connection.CloseAsync();
            }

            await closing;
        }
    }

    [Fact]
    public async Task ShouldFlushDeferredStatementCloseGivenRollbackInFlightWhenTransactionIsDisposed()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        TaskCompletionSource rollbackReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closeReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            await BeginAsync(session);
            await ExpectQueryAsync(session, "ROLLBACK");
            rollbackReceived.SetResult();
            await finishRollback.Task;
            await session.WriteCommandCompleteAsync("ROLLBACK");
            await session.WriteReadyAsync();
            var (tag, _) = await session.ReadFrameAsync();
            if (tag == (byte)'X')
            {
                return;
            }

            Assert.Equal((byte)'C', tag);
            Assert.Equal((byte)'S', (await session.ReadFrameAsync()).Tag);
            closeReceived.SetResult();
            await finishClose.Task;
            await session.WriteAsync((byte)'3', []);
            await session.WriteReadyAsync();
            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        var transaction = await connection.BeginTransactionAsync(token);

        // Act
        var disposing = transaction.DisposeAsync().AsTask();
        try
        {
            await rollbackReceived.Task.WaitAsync(TestTimeout, token);
            await connection.CloseServerStatementAsync("prepared_statement", connection.SessionIdentity, token);
            finishRollback.SetResult();
            await closeReceived.Task.WaitAsync(TestTimeout, token);

            // Assert
            Assert.False(disposing.IsCompleted);
            finishClose.SetResult();
            await disposing.WaitAsync(TestTimeout, token);
            Assert.Null(transaction.Connection);
            Assert.Equal(ConnectionState.Open, connection.State);
        }
        finally
        {
            _ = finishRollback.TrySetResult();
            _ = finishClose.TrySetResult();
            await disposing;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReportRollbackAndKeepIdleSessionGivenRollbackAcknowledgementWhenCommitRuns(bool synchronous)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using ScriptedServer server = new(async session =>
        {
            await BeginAsync(session);
            await ExpectQueryAsync(session, "COMMIT");
            await session.WriteCommandCompleteAsync("ROLLBACK");
            await session.WriteReadyAsync();
            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var lease = connection.SessionIdentity;

        // Act
        var failure = await Record.ExceptionAsync(() => synchronous ? Task.Run(transaction.Commit, token) :
            transaction.CommitAsync(token));

        // Assert
        Assert.Contains("rolled back", Assert.IsType<CassieException>(failure).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(transaction.Connection);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(lease, connection.SessionIdentity);
        using var followUp = connection.CreateCommand();
        followUp.CommandText = "SELECT 1";
        _ = await followUp.ExecuteNonQueryAsync(token);
    }

    [Theory]
    [InlineData(null, 'I')]
    [InlineData("COMMIT", 'T')]
    public async Task ShouldDiscardSessionGivenUnconfirmedOutcomeWhenCommitRuns(string? completionTag, char status)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using ScriptedServer server = new(async session =>
        {
            await BeginAsync(session);
            await ExpectQueryAsync(session, "COMMIT");
            if (completionTag is not null)
            {
                await session.WriteCommandCompleteAsync(completionTag);
            }

            await session.WriteReadyAsync(status);
            await session.StayVerySilentAsync();
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);

        // Act
        var failure = await Record.ExceptionAsync(() => transaction.CommitAsync(token));

        // Assert
        _ = Assert.IsType<IOException>(failure);
        Assert.Null(transaction.Connection);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ShouldRetireTransactionGivenIdleReplyWhenCancellationRacesCommit()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        TaskCompletionSource commitReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            await BeginAsync(session);
            await ExpectQueryAsync(session, "COMMIT");
            commitReceived.SetResult();
            await cancelReceived.Task.WaitAsync(TestTimeout);
            await session.WriteCommandCompleteAsync("COMMIT");
            await session.WriteReadyAsync();
            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        }, acknowledgeCancel: () =>
        {
            cancelReceived.SetResult();
            return Task.CompletedTask;
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        using CancellationTokenSource cancellation = new();
        var committing = transaction.CommitAsync(cancellation.Token);
        await commitReceived.Task.WaitAsync(TestTimeout, token);

        // Act
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committing);

        // Assert
        Assert.Null(transaction.Connection);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    private static async Task BeginAsync(ScriptedSession session)
    {
        await ExpectQueryAsync(session, "BEGIN ISOLATION LEVEL READ COMMITTED");
        await session.WriteCommandCompleteAsync("BEGIN");
        await session.WriteReadyAsync('T');
    }

    private static async Task ExpectQueryAsync(ScriptedSession session, string sql)
    {
        var (tag, payload) = await session.ReadFrameAsync();
        Assert.Equal((byte)'Q', tag);
        Assert.Equal(sql + "\0", Encoding.UTF8.GetString(payload));
    }
}
