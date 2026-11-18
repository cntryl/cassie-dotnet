using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands;

public sealed class BatchContractTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ShouldSendEveryCommandBeforeAnyResponseGivenThreeCommandsWhenBatchExecutes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1");
            await BatchPeer.ReplyWithCommandAsync(session, "UPDATE 2");
            await BatchPeer.ReplyWithRowsAsync(session, "5");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        var insert = batch.BatchCommands.Add("INSERT INTO ledger (name) VALUES (@name)");
        _ = insert.Parameters.Add(new CassieParameter { ParameterName = "name", Value = "alpha" });
        _ = batch.BatchCommands.Add("UPDATE ledger SET total = 1");
        _ = batch.BatchCommands.Add("SELECT 5");

        // Act
        var affected = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        var frame = Assert.Single(peer.Batches);
        Assert.Equal("PBDEPBDEPBDES", BatchPeer.Tags(frame));
        Assert.Equal(3, affected);
        Assert.Equal("INSERT INTO ledger (name) VALUES ($1)", BatchPeer.ParseSql(frame[0]));
        Assert.Equal("UPDATE ledger SET total = 1", BatchPeer.ParseSql(frame[4]));
        Assert.Equal("SELECT 5", BatchPeer.ParseSql(frame[8]));
        Assert.Contains("alpha", Encoding.UTF8.GetString(frame[1].Payload), StringComparison.Ordinal);
        byte[] executeUnlimited = [0, 0, 0, 0, 0];
        Assert.Equal(executeUnlimited, frame[3].Payload);
        Assert.Equal(executeUnlimited, frame[7].Payload);
        Assert.Equal(executeUnlimited, frame[11].Payload);
        Assert.Equal(1, server.Accepted);
    }

    [Fact]
    public async Task ShouldYieldOneResultPerRowReturningCommandGivenMixedBatchWhenReaderAdvances()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithRowsAsync(session, "1", "2");
            await BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 3");
            await BatchPeer.ReplyWithEmptyQueryAsync(session);
            await BatchPeer.ReplyWithRowsAsync(session, "9");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT value FROM numbers");
        _ = batch.BatchCommands.Add("INSERT INTO numbers SELECT 1");
        _ = batch.BatchCommands.Add(string.Empty);
        _ = batch.BatchCommands.Add("SELECT 9");
        List<(int Fields, List<int> Values)> results = [];

        // Act
        await using (var reader = await batch.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            do
            {
                List<int> values = [];
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    values.Add(reader.GetInt32(0));
                }

                results.Add((reader.FieldCount, values));
            } while (await reader.NextResultAsync(TestContext.Current.CancellationToken));

            // Assert
            // Statements without a result set are skipped, as they are for an ordinary command.
            Assert.Equal(2, results.Count);
            Assert.Equal([1, 1], results.Select(static result => result.Fields));
            Assert.Equal([1, 2], results[0].Values);
            Assert.Equal([9], results[1].Values);
            Assert.Equal(3, reader.RecordsAffected);
        }

        Assert.Equal([-1, 3, -1, -1], batch.BatchCommands.Select(static command => command.RecordsAffected));
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal("PBDEPBDEPBDEPBDES", BatchPeer.Tags(Assert.Single(peer.Batches)));
    }

    [Fact]
    public async Task ShouldReportCountPerCommandGivenCountingStatementsWhenNonQueryCompletes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 4");
            await BatchPeer.ReplyWithCommandAsync(session, "CREATE TABLE");
            await BatchPeer.ReplyWithCommandAsync(session, "DELETE 6");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        _ = batch.BatchCommands.Add("CREATE TABLE b (id int)");
        _ = batch.BatchCommands.Add("DELETE FROM c");

        // Act
        var total = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(10, total);
        Assert.Equal([4, -1, 6], batch.BatchCommands.Select(static command => command.RecordsAffected));
    }

    [Fact]
    public async Task ShouldReturnFirstColumnOfFirstResultSetGivenLeadingNonQueryWhenExecuteScalarRuns()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1");
            await BatchPeer.ReplyWithRowsAsync(session, "41", "42");
            await BatchPeer.ReplyWithRowsAsync(session, "99");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        _ = batch.BatchCommands.Add("SELECT value FROM numbers");
        _ = batch.BatchCommands.Add("SELECT 99");

        // Act
        var scalar = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(41, scalar);
        Assert.Equal(ConnectionState.Open, connection.State);
        using CassieCommand followUp = new() { Connection = connection, CommandText = "SELECT 7" };
        Assert.Equal(7, await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldSurfaceFirstErrorAndReuseConnectionGivenFailingMiddleCommandWhenBatchExecutes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 2");
            await BatchPeer.ReplyWithErrorAsync(session, "23505", "duplicate key");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var sessionIdentity = connection.SessionIdentity;
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 2");

        // Act
        var failure =
            await Assert.ThrowsAsync<CassieException>(() =>
                batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("23505", failure.SqlState);
        Assert.Same(batch.BatchCommands[1], failure.BatchCommand);
        Assert.Equal([2, 0, 0], batch.BatchCommands.Select(static command => command.RecordsAffected));
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(sessionIdentity, connection.SessionIdentity);
        using CassieCommand followUp = new() { Connection = connection, CommandText = "SELECT 7" };
        Assert.Equal(7, await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, server.Accepted);
    }

    [Fact]
    public async Task ShouldSurfaceErrorOnAdvanceGivenFailingSecondCommandWhenReaderMovesToNextResult()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithRowsAsync(session, "1");
            await BatchPeer.ReplyWithErrorAsync(session, "22012", "division by zero");
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT 1");
        _ = batch.BatchCommands.Add("SELECT 1/0");
        _ = batch.BatchCommands.Add("SELECT 3");
        await using var reader = await batch.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt32(0));

        // Act
        var failure =
            await Assert.ThrowsAsync<CassieException>(() =>
                reader.NextResultAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("22012", failure.SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
        using CassieCommand followUp = new() { Connection = connection, CommandText = "SELECT 7" };
        Assert.Equal(7, await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ShouldDrainResponseAndReuseConnectionGivenReaderDisposedUnreadWhenBatchHasUnreadResults(
        int commandCount)
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            for (var index = 0; index < commandCount; index++)
            {
                await BatchPeer.ReplyWithRowsAsync(session, "1", "2");
            }
        }));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        for (var index = 0; index < commandCount; index++)
        {
            _ = batch.BatchCommands.Add("SELECT value FROM numbers");
        }

        // Act
        await using (var reader = await batch.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        using CassieCommand followUp = new() { Connection = connection, CommandText = "SELECT 7" };
        Assert.Equal(7, await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, server.Accepted);
    }

    [Fact]
    public async Task ShouldSurfaceErrorOnExecuteGivenFirstCommandFailsWhenReaderIsRequested()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((_, session) =>
            BatchPeer.ReplyWithErrorAsync(session, "42P01", "relation does not exist")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT * FROM missing");
        _ = batch.BatchCommands.Add("SELECT 2");

        // Act
        var failure =
            await Assert.ThrowsAsync<CassieException>(() =>
                batch.ExecuteReaderAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("42P01", failure.SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
        using CassieCommand followUp = new() { Connection = connection, CommandText = "SELECT 7" };
        Assert.Equal(7, await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldSendNothingGivenInvalidParameterInLaterCommandWhenBatchExecutes(bool transactionActive)
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var sessionIdentity = connection.SessionIdentity;
        await using DbTransaction? transaction = transactionActive
            ? await connection.BeginTransactionAsync(TestContext.Current.CancellationToken)
            : null;
        await using CassieBatch batch = new(connection) { Transaction = (CassieTransaction?)transaction };
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        var invalid = batch.BatchCommands.Add("SELECT @value");
        _ = invalid.Parameters.Add(new CassieParameter
        {
            ParameterName = "value",
            Value = "unsupported",
            PostgresTypeOid = 999999,
            ParameterFormat = CassieParameterFormat.Binary
        });

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(sessionIdentity, connection.SessionIdentity);
        Assert.Empty(peer.Batches);
        Assert.Equal(transactionActive ? 1 : 0, peer.SimpleQueries.Length);
        invalid.Parameters.Clear();
        invalid.CommandText = "SELECT 1";
        Assert.Equal(1, await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, server.Accepted);
    }

    [Fact]
    public async Task ShouldRunInsideTransactionGivenTransactionAssignedWhenBatchExecutes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using DbTransaction transaction =
            await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection, (CassieTransaction)transaction);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");

        // Act
        var affected = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, affected);
        _ = Assert.Single(peer.Batches);
        Assert.StartsWith("BEGIN", peer.SimpleQueries[0], StringComparison.Ordinal);
        Assert.Equal("COMMIT", peer.SimpleQueries[^1]);
    }

    [Fact]
    public async Task ShouldRejectBatchGivenTransactionFromAnotherSourceWhenTransactionIsActive()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using DbTransaction transaction =
            await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using CassieConnection other = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await other.OpenAsync(TestContext.Current.CancellationToken);
        await using DbTransaction foreign = await other.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection, (CassieTransaction)foreign);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(peer.Batches);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShouldCancelAndRecoverGivenTokenCanceledWhenServerStillRunsBatch()
    {
        // Arrange
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StrongBox<ScriptedServer> serverBox = new();
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithRowsAsync(session, "1");
            await BatchPeer.ReplyWithRowsAsync(session, "2");
            await session.WriteAsync((byte)'1', []);
            await session.WriteAsync((byte)'2', []);
            await session.WriteIntRowDescriptionAsync();
            started.SetResult();
            Assert.True(ScriptedServer.WaitUntil(() => serverBox.Value!.CancelRequests >= 1));
            await BatchPeer.ReplyWithErrorAsync(session, "57014", "canceling statement due to user request");
        }));
        serverBox.Value = server;
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT 1");
        _ = batch.BatchCommands.Add("SELECT 2");
        _ = batch.BatchCommands.Add("SELECT pg_sleep(60)");
        using CancellationTokenSource cancellation = new();
        var running = batch.ExecuteNonQueryAsync(cancellation.Token);
        await started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Act
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            running.WaitAsync(Guard, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        using CassieCommand followUp = new() { Connection = connection, CommandText = "SELECT 7" };
        Assert.Equal(7, await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldCancelRunningBatchGivenCancelCalledWhenServerStillRunsBatch()
    {
        // Arrange
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StrongBox<ScriptedServer> serverBox = new();
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await session.WriteAsync((byte)'1', []);
            await session.WriteAsync((byte)'2', []);
            await session.WriteIntRowDescriptionAsync();
            started.SetResult();
            Assert.True(ScriptedServer.WaitUntil(() => serverBox.Value!.CancelRequests >= 1));
            await BatchPeer.ReplyWithErrorAsync(session, "57014", "canceling statement due to user request");
        }));
        serverBox.Value = server;
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection) { Timeout = 0 };
        _ = batch.BatchCommands.Add("SELECT pg_sleep(60)");
        var running = batch.ExecuteNonQueryAsync(CancellationToken.None);
        await started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Act
        batch.Cancel();

        // Assert
        var failure =
            await Assert.ThrowsAsync<CassieException>(() =>
                running.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal("57014", failure.SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldThrowTimeoutGivenSilentServerWhenBatchTimeoutElapses()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection) { Timeout = 1 };
        _ = batch.BatchCommands.Add("SELECT 1");
        _ = batch.BatchCommands.Add("SELECT 2");

        // Act
        var exception =
            await Assert.ThrowsAsync<TimeoutException>(() => batch.ExecuteNonQueryAsync(CancellationToken.None));

        // Assert
        Assert.Contains("CommandTimeout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ShouldSendNothingGivenUnboundParameterWhenPrepareRuns()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((number, session) =>
            BatchPeer.ReplyInKindAsync(session, peer.Batches[number - 1], "INSERT 0 1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("INSERT INTO a VALUES (@x)");

        // Act
        var unbound = await Record.ExceptionAsync(() => batch.PrepareAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(unbound);
        Assert.Empty(peer.Batches);
        Assert.Empty(peer.SimpleQueries);
    }

    [Fact]
    public async Task ShouldBindToPreparedStatementsGivenPreparedBatchWhenBatchExecutesTwice()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((number, session) =>
            BatchPeer.ReplyInKindAsync(session, peer.Batches[number - 1], "INSERT 0 1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        CassieBatch batch = new(connection);
        foreach (var table in new[] { "a", "b" })
        {
            var command = batch.BatchCommands.Add($"INSERT INTO {table} VALUES (@x)");
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "x", Value = 1 });
        }

        // Act
        await batch.PrepareAsync(TestContext.Current.CancellationToken);
        var first = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var second = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await batch.DisposeAsync();

        // Assert
        var exchanges = peer.Batches;
        Assert.Equal(["PDS", "PDS", "BDEBDES", "BDEBDES", "CS", "CS"], exchanges.Select(BatchPeer.Tags));
        string[] prepared =
            [BatchPeer.ParseStatementName(exchanges[0][0]), BatchPeer.ParseStatementName(exchanges[1][0])];
        Assert.All(prepared, static name => Assert.NotEmpty(name));
        Assert.NotEqual(prepared[0], prepared[1]);
        foreach (var execution in exchanges[2..4])
        {
            Assert.Equal(prepared,
                execution.Where(static frame => frame.Tag == (byte)'B').Select(BatchPeer.BindStatementName));
        }

        Assert.Equal(2, first);
        Assert.Equal(2, second);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldPrepareAgainGivenCommandTextChangedWhenPreparedBatchExecutes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((number, session) =>
            BatchPeer.ReplyInKindAsync(session, peer.Batches[number - 1], "INSERT 0 1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        var command = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        await batch.PrepareAsync(TestContext.Current.CancellationToken);

        // Act
        command.CommandText = "INSERT INTO b SELECT 1";
        var affected = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        var exchanges = peer.Batches;
        Assert.Equal(["PDS", "CS", "PDS", "BDES"], exchanges.Select(BatchPeer.Tags));
        Assert.Equal("INSERT INTO b SELECT 1", BatchPeer.ParseSql(exchanges[2][0]));
        Assert.Equal(BatchPeer.ParseStatementName(exchanges[2][0]), BatchPeer.BindStatementName(exchanges[3][0]));
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task ShouldOnlyValidateGivenBatchFromDataSourceWhenPrepareRuns()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((number, session) =>
            BatchPeer.ReplyInKindAsync(session, peer.Batches[number - 1], "INSERT 0 1")));
        await using CassieDataSource source = new(server.ConnectionString("Pooling=false"));
        await using var batch = source.CreateBatch();
        var command = batch.CreateBatchCommand();
        command.CommandText = "INSERT INTO a SELECT 1";
        batch.BatchCommands.Add(command);

        // Act
        await batch.PrepareAsync(TestContext.Current.CancellationToken);
        var affected = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, affected);
        Assert.Equal(["PBDES"], peer.Batches.Select(BatchPeer.Tags));
    }

    [Fact]
    public async Task ShouldRejectExecutionGivenEmptyBatchWhenBatchExecutes()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldRejectExecutionGivenSchemaOnlyBehaviorWhenReaderIsRequested()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT 1");

        // Act
        var failure = await Record.ExceptionAsync(() =>
            batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldRejectExecutionGivenBatchWhileReaderIsActiveWhenSecondBatchRuns()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((_, session) => BatchPeer.ReplyWithRowsAsync(session, "1")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT 1");
        await using var first = await batch.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(failure);
        _ = Assert.Single(peer.Batches);
    }

    [Fact]
    public async Task ShouldRejectUseGivenDisposedBatchWhenBatchExecutes()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("SELECT 1");
        await batch.DisposeAsync();

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<ObjectDisposedException>(failure);
    }

    [Fact]
    public void ShouldRejectForeignCommandGivenCollectionWhenNonCassieCommandIsAdded()
    {
        // Arrange
        CassieBatchCommandCollection commands = [];

        // Act
        var failure = Record.Exception(() => commands.Add(new ForeignBatchCommand()));

        // Assert
        _ = Assert.IsType<InvalidCastException>(failure);
        Assert.Empty(commands);
    }

    [Fact]
    public void ShouldExposeProviderBatchGivenConnectionAndFactoryWhenBatchesAreCreated()
    {
        // Arrange
        using CassieConnection connection = new()
        { ConnectionString = "Host=127.0.0.1;Username=app;Command Timeout=12" };

        // Act
        DbBatch fromConnection = connection.CreateBatch();
        var fromFactory = CassieProviderFactory.Instance.CreateBatch();
        var fromBatch = fromConnection.CreateBatchCommand();
        var fromFactoryCommand = CassieProviderFactory.Instance.CreateBatchCommand();

        // Assert
        Assert.True(connection.CanCreateBatch);
        Assert.True(CassieProviderFactory.Instance.CanCreateBatch);
        Assert.Same(connection, fromConnection.Connection);
        Assert.Equal(12, fromConnection.Timeout);
        Assert.Null(fromFactory.Connection);
        _ = Assert.IsType<CassieBatch>(fromConnection);
        _ = Assert.IsType<CassieBatchCommand>(fromBatch);
        _ = Assert.IsType<CassieBatchCommand>(fromFactoryCommand);
        Assert.True(fromBatch.CanCreateParameter);
        _ = Assert.IsType<CassieParameter>(fromBatch.CreateParameter());
    }

    [Fact]
    public void ShouldRejectTimeoutGivenOutOfRangeValueWhenTimeoutIsAssigned()
    {
        // Arrange
        using CassieBatch batch = new();

        // Act
        var failure = Record.Exception(() => batch.Timeout = -1);

        // Assert
        _ = Assert.IsType<ArgumentOutOfRangeException>(failure);
        Assert.Equal(30, batch.Timeout);
    }

    [Fact]
    public async Task ShouldOpenConnectionPerExecutionGivenBatchFromBaseSourceWhenBatchExecutesTwice()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((_, session) => BatchPeer.ReplyWithRowsAsync(session, "7")));
        await using DbDataSource source = new CassieDataSource(server.ConnectionString("Pooling=false"));
        await using var batch = source.CreateBatch();
        batch.BatchCommands.Add(batch.CreateBatchCommand());
        batch.BatchCommands[0].CommandText = "SELECT 7";

        // Act
        var first = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        var second = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7, first);
        Assert.Equal(7, second);
        Assert.True(ScriptedServer.WaitUntil(() => server.Accepted == 2 && server.Terminated == 2));
        Assert.Equal(2, peer.Batches.Length);
    }

    [Fact]
    public async Task ShouldNotOpenConnectionGivenInvalidParameterWhenConnectionlessBatchExecutes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1")));
        await using DbDataSource source = new CassieDataSource(server.ConnectionString("Pooling=false"));
        await using var batch = (CassieBatch)source.CreateBatch();
        var command = batch.BatchCommands.Add("SELECT @value");
        _ = command.Parameters.Add(new CassieParameter
        {
            ParameterName = "value",
            Value = "x",
            PostgresTypeOid = 999999,
            ParameterFormat = CassieParameterFormat.Binary
        });

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(0, server.Accepted);
    }

    [Fact]
    public void ShouldCloseConnectionAndStayOnCallingThreadGivenSynchronousConnectionlessBatchWhenExecuted()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 5")));
        using CassieDataSource source = new(server.ConnectionString("Pooling=false"));
        using var batch = source.CreateBatch();
        batch.BatchCommands.Add(new CassieBatchCommand("INSERT INTO a SELECT 1"));
        var before = SynchronousCall.PendingWaitsOnThisThread;

        // Act
        var affected = batch.ExecuteNonQuery();
        var pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;

        // Assert
        Assert.Equal(5, affected);
        Assert.Equal(0, pendingWaits);
        Assert.True(ScriptedServer.WaitUntil(() => server.Terminated == 1));
    }

    [Fact]
    public async Task ShouldResetRecordsAffectedGivenLaterExecutionFailsValidationWhenBatchRunsAgain()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 4")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        var command = batch.BatchCommands.Add("INSERT INTO a SELECT @value");
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 1 });
        _ = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, command.RecordsAffected);
        command.Parameters.Clear();

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, command.RecordsAffected);
        _ = Assert.Single(peer.Batches);
    }

    [Fact]
    public async Task ShouldResetRecordsAffectedGivenAttemptRejectedBeforeValidationWhenBatchRunsAgain()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 4")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        var command = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        _ = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var afterSuccess = command.RecordsAffected;

        // Act
        command.CommandType = CommandType.TableDirect;
        var unsupportedType =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        var afterUnsupportedType = command.RecordsAffected;
        command.CommandType = CommandType.Text;
        _ = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var schemaOnly = await Record.ExceptionAsync(() =>
            batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(4, afterSuccess);
        _ = Assert.IsType<NotSupportedException>(unsupportedType);
        Assert.Equal(0, afterUnsupportedType);
        _ = Assert.IsType<NotSupportedException>(schemaOnly);
        Assert.Equal(0, command.RecordsAffected);
        Assert.Equal(2, peer.Batches.Length);
    }

    [Fact]
    public async Task ShouldRejectTransactionGivenConnectionlessBatchWhenTransactionIsAssigned()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1")));
        await using CassieDataSource source = new(server.ConnectionString("Pooling=false"));
        await using var other = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using DbTransaction transaction =
            await other.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var batch = source.CreateBatch();
        batch.Transaction = transaction;
        batch.BatchCommands.Add(new CassieBatchCommand("INSERT INTO a SELECT 1"));

        // Act
        var failure =
            await Record.ExceptionAsync(() => batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(peer.Batches);
        Assert.Equal(1, server.Accepted);
    }

    [Fact]
    public async Task ShouldRunOnOwnedConnectionGivenBatchFromTypedSourceWhenConnectionIsCreated()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server =
            new(peer.Serve((_, session) => BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 2")));
        await using CassieDataSource source = new(server.ConnectionString("Pooling=false"));
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using DbBatch batch = connection.CreateBatch();
        batch.BatchCommands.Add(new CassieBatchCommand("INSERT INTO a SELECT 1"));

        // Act
        var affected = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, affected);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldEmitSanitizedBatchActivityGivenBatchWhenExecuted()
    {
        // Arrange
        ConcurrentQueue<(string Name, ActivityStatusCode Status, string Tags)> activities = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name == CassieDiagnostics.Name,
            Sample = SampleActivity,
            ActivityStopped = activity => activities.Enqueue((activity.OperationName, activity.Status,
                string.Join(';', activity.TagObjects.Select(static tag => tag.Key + "=" + tag.Value))))
        };
        ActivitySource.AddActivityListener(listener);
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((_, session) => BatchPeer.ReplyWithRowsAsync(session, "7")));
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(connection);
        var command = batch.BatchCommands.Add("SELECT @secret");
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "secret", Value = "hunter2" });

        // Act
        _ = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        var (_, status, tags) = Assert.Single(activities, static item => item.Name == "cassie.batch");
        Assert.NotEqual(ActivityStatusCode.Error, status);
        Assert.DoesNotContain("SELECT", tags, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", tags, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldStayOnCallingThreadGivenSynchronousBatchWhenRowsSpanManyTransportReads()
    {
        // Arrange
        const int rowCount = 3000;
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve(async (_, session) =>
        {
            await BatchPeer.ReplyWithCommandAsync(session, "INSERT 0 1");
            await BatchPeer.ReplyWithRowsAsync(session,
            [
                .. Enumerable.Range(0, rowCount).Select(static value => value.ToString(CultureInfo.InvariantCulture))
            ]);
        }));
        using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        connection.Open();
        using CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        _ = batch.BatchCommands.Add("SELECT value FROM numbers");
        var before = SynchronousCall.PendingWaitsOnThisThread;
        long sum = 0;
        var rows = 0;

        // Act
        var affected = batch.ExecuteNonQuery();
        var scalar = batch.ExecuteScalar();
        using (var reader = batch.ExecuteReader())
        {
            do
            {
                while (reader.Read())
                {
                    sum += reader.GetInt32(0);
                    rows++;
                }
            } while (reader.NextResult());
        }

        connection.Close();
        var pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;

        // Assert
        Assert.Equal(1, affected);
        Assert.Equal(0, scalar);
        Assert.Equal(rowCount, rows);
        Assert.Equal((long)rowCount * (rowCount - 1) / 2, sum);
        Assert.Equal(0, pendingWaits);
    }

    [Fact]
    public void ShouldStayOnCallingThreadGivenSynchronousPrepareWhenPreparedBatchExecutes()
    {
        // Arrange
        BatchPeer peer = new();
        using ScriptedServer server = new(peer.Serve((number, session) =>
            BatchPeer.ReplyInKindAsync(session, peer.Batches[number - 1], "INSERT 0 1")));
        using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        connection.Open();
        CassieBatch batch = new(connection);
        _ = batch.BatchCommands.Add("INSERT INTO a SELECT 1");
        var before = SynchronousCall.PendingWaitsOnThisThread;

        // Act
        batch.Prepare();
        var affected = batch.ExecuteNonQuery();
        batch.Dispose();
        var pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;

        // Assert
        Assert.Equal(1, affected);
        Assert.Equal(["PDS", "BDES", "CS"], peer.Batches.Select(BatchPeer.Tags));
        Assert.Equal(0, pendingWaits);
    }

    private static ActivitySamplingResult SampleActivity(ref ActivityCreationOptions<ActivityContext> options)
    {
        return ActivitySamplingResult.AllDataAndRecorded;
    }

    private sealed class ForeignBatchCommand : DbBatchCommand
    {
        public override string CommandText { get; set; } = string.Empty;
        public override CommandType CommandType { get; set; }
        public override int RecordsAffected => 0;
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
    }
}
