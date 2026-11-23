using System.Data;
using System.Data.Common;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Live;

[Collection(LiveEndpoint.CollectionName)]
public sealed class LiveBatchContractTests : IAsyncLifetime
{
    private readonly LiveSession _live = new();

    public ValueTask InitializeAsync()
    {
        return _live.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        return _live.DisposeAsync();
    }

    [Fact]
    public async Task ShouldReturnOneResultPerQueryGivenWritesAndQueriesWhenBatchIsRead()
    {
        // Arrange
        await _live.CreateProbeTableAsync();
        await using CassieBatch batch = new(_live.Connection);
        var insertFirst =
            batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (@id, @label)");
        _ = insertFirst.Parameters.Add(new CassieParameter { ParameterName = "id", DbType = DbType.Int32, Value = 1 });
        _ = insertFirst.Parameters.Add(new CassieParameter
        { ParameterName = "label", DbType = DbType.String, Value = "first" });
        var insertSecond =
            batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (@id, @label)");
        _ = insertSecond.Parameters.Add(new CassieParameter { ParameterName = "id", DbType = DbType.Int32, Value = 2 });
        _ = insertSecond.Parameters.Add(new CassieParameter
        { ParameterName = "label", DbType = DbType.String, Value = "second" });
        _ = batch.BatchCommands.Add($"SELECT id, label FROM {LiveSession.ProbeTable} ORDER BY id");
        var count = batch.BatchCommands.Add($"SELECT count(*) FROM {LiveSession.ProbeTable} WHERE id > @minimum");
        _ = count.Parameters.Add(new CassieParameter { ParameterName = "minimum", DbType = DbType.Int32, Value = 1 });

        // Act
        List<(int Id, string Label)> rows = [];
        long counted;
        await using (var reader = await batch.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            // The reader starts on the first command that returns rows; the two inserts are skipped.
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            Assert.True(await reader.NextResultAsync(TestContext.Current.CancellationToken));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            counted = reader.GetInt64(0);
            Assert.False(await reader.NextResultAsync(TestContext.Current.CancellationToken));
        }

        // Assert
        Assert.Equal([(1, "first"), (2, "second")], rows);
        Assert.Equal(1, counted);
        Assert.Equal(1, insertFirst.RecordsAffected);
        Assert.Equal(1, insertSecond.RecordsAffected);
    }

    [Fact]
    public async Task ShouldSumAffectedRowsGivenSeveralWritesWhenBatchRunsAsNonQuery()
    {
        // Arrange
        await _live.CreateProbeTableAsync();
        await using CassieBatch batch = new(_live.Connection);

        for (var id = 1; id <= 3; id++)
        {
            var insert =
                batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (@id, 'row')");
            _ = insert.Parameters.Add(new CassieParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
        }

        // Act

        var affected = await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var verify = _live.CreateCommand();
        verify.CommandText = $"SELECT count(*) FROM {LiveSession.ProbeTable}";
        var stored = await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, affected);
        Assert.Equal(3L, stored);
    }

    [Fact]
    public async Task ShouldKeepConnectionUsableGivenFailingCommandInMiddleWhenBatchRuns()
    {
        // Arrange
        await _live.CreateProbeTableAsync();
        await using CassieBatch batch = new(_live.Connection);
        var first = batch.BatchCommands.Add(
            $"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (1, 'kept-or-rolled-back')");
        var failing = batch.BatchCommands.Add("SELECT * FROM cassie_live_table_that_does_not_exist");
        var skipped =
            batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (2, 'never-runs')");

        // Act
        var error = await Assert.ThrowsAsync<CassieException>(() =>
            batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        using var followUp = _live.CreateCommand();
        followUp.CommandText = $"SELECT count(*) FROM {LiveSession.ProbeTable} WHERE id = 2";
        var skippedRows = await followUp.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(string.IsNullOrEmpty(error.SqlState));
        Assert.Same(failing, error.BatchCommand);
        Assert.Equal(ConnectionState.Open, _live.Connection.State);
        Assert.Equal(0L, skippedRows);
        Assert.Equal(0, failing.RecordsAffected);
        Assert.Equal(0, skipped.RecordsAffected);
        Assert.Equal(1, first.RecordsAffected);
    }

    [Fact]
    public async Task ShouldUndoWritesGivenTransactionRolledBackWhenBatchRanInsideIt()
    {
        // Arrange
        await _live.CreateProbeTableAsync();
        await using DbTransaction transaction =
            await _live.Connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using CassieBatch batch = new(_live.Connection, (CassieTransaction)transaction);
        _ = batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (1, 'a')");
        _ = batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (2, 'b')");
        _ = batch.BatchCommands.Add($"SELECT count(*) FROM {LiveSession.ProbeTable}");

        // Act
        var insideTransaction = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        using var verify = _live.CreateCommand();
        verify.CommandText = $"SELECT count(*) FROM {LiveSession.ProbeTable}";
        var afterRollback = await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2L, insideTransaction);
        Assert.Equal(0L, afterRollback);
    }

    [Fact]
    public async Task ShouldReuseServerStatementsGivenPreparedBatchWhenParametersChangeBetweenExecutions()
    {
        // Arrange
        await _live.CreateProbeTableAsync();
        await using CassieBatch batch = new(_live.Connection);
        var insert = batch.BatchCommands.Add($"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (@id, @label)");
        CassieParameter id = new() { ParameterName = "id", DbType = DbType.Int32, Value = 1 };
        CassieParameter label = new() { ParameterName = "label", DbType = DbType.String, Value = "first" };
        _ = insert.Parameters.Add(id);
        _ = insert.Parameters.Add(label);
        var count = batch.BatchCommands.Add($"SELECT count(*) FROM {LiveSession.ProbeTable} WHERE id >= @minimum");
        _ = count.Parameters.Add(new CassieParameter { ParameterName = "minimum", DbType = DbType.Int32, Value = 1 });
        await batch.PrepareAsync(TestContext.Current.CancellationToken);

        // Act
        var afterFirst = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        id.Value = 2;
        label.Value = "second";
        var afterSecond = await batch.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1L, afterFirst);
        Assert.Equal(2L, afterSecond);
        Assert.Equal(1, insert.RecordsAffected);
    }
}
