using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    [Collection(LiveEndpoint.CollectionName)]
    public sealed class LiveCommandContractTests : IAsyncLifetime
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
        public async Task ShouldRebindValueGivenServerPreparedCommandWhenParameterChanges()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::int AS value";
            CassieParameter parameter = new() { ParameterName = "value", DbType = DbType.Int32, Value = 17 };
            _ = command.Parameters.Add(parameter);
            await command.PrepareAsync(TestContext.Current.CancellationToken);
            object? first = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Act
            parameter.Value = 29;
            object? second = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(17, first);
            Assert.Equal(29, second);
        }

        [Fact]
        public async Task ShouldRecreatePreparedStatementGivenDeallocateAndReconnectWhenCommandRunsAgain()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::int AS value";
            CassieParameter parameter = new() { ParameterName = "value", DbType = DbType.Int32, Value = 17 };
            _ = command.Parameters.Add(parameter);
            await command.PrepareAsync(TestContext.Current.CancellationToken);
            _ = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            using CassieCommand deallocate = _live.CreateCommand();
            deallocate.CommandText = "DEALLOCATE ALL";
            _ = await deallocate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await _live.Connection.CloseAsync();
            await _live.Connection.OpenAsync(TestContext.Current.CancellationToken);

            // Act
            parameter.Value = 37;
            object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(37, value);
        }

        [Fact]
        public async Task ShouldRecoverConnectionGivenParseErrorWhenPrepareIsRejected()
        {
            // Arrange
            using CassieCommand invalid = _live.CreateCommand();
            invalid.CommandText = "SELECT FROM";

            // Act
            CassieException exception = await Assert.ThrowsAsync<CassieException>(() => invalid.PrepareAsync(TestContext.Current.CancellationToken));
            using CassieCommand recover = _live.CreateCommand();
            recover.CommandText = "SELECT 1";
            object? recovered = await recover.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("42601", exception.SqlState);
            Assert.Equal(1, recovered);
        }

        [Fact]
        public async Task ShouldRunProcedureBodyGivenBoundInputWhenSqlTextCallIsExecuted()
        {
            // Arrange
            await _live.CreateStoreProcedureAsync();
            using CassieCommand call = _live.CreateCommand();
            call.CommandText = $"CALL {LiveSession.StoreProcedure}(@title)";
            _ = call.Parameters.Add(new CassieParameter { ParameterName = "title", Value = "input-bound-call" });

            // Act
            _ = await call.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            using CassieCommand verify = _live.CreateCommand();
            verify.CommandText = $"SELECT title FROM {LiveSession.CallTable}";
            Assert.Equal("input-bound-call", await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldBindPositionallyGivenPreparedStoredProcedureWhenParameterValueChanges()
        {
            // Arrange
            await _live.CreateStoreProcedureAsync();
            using CassieCommand call = _live.CreateCommand();
            call.CommandText = LiveSession.StoreProcedure;
            call.CommandType = CommandType.StoredProcedure;
            CassieParameter title = new() { ParameterName = "title", Value = "ado-stored-procedure-call" };
            _ = call.Parameters.Add(title);
            await call.PrepareAsync(TestContext.Current.CancellationToken);

            // Act
            _ = await call.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            title.Value = "ado-stored-procedure-call-updated";
            _ = await call.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            using CassieCommand verifyFirst = _live.CreateCommand();
            verifyFirst.CommandText = $"SELECT title FROM {LiveSession.CallTable} WHERE title = 'ado-stored-procedure-call'";
            Assert.Equal("ado-stored-procedure-call", await verifyFirst.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            using CassieCommand verifyUpdated = _live.CreateCommand();
            verifyUpdated.CommandText = $"SELECT title FROM {LiveSession.CallTable} WHERE title = 'ado-stored-procedure-call-updated'";
            Assert.Equal("ado-stored-procedure-call-updated", await verifyUpdated.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldExposeStreamsGivenByteaAndTextColumnsWhenReaderOpens()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @blob::bytea AS blob, @text::text AS text";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "blob", Value = new byte[] { 0, 1, 255 } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "text", Value = "stream access" });
            await using DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Act
            await using Stream stream = reader.GetStream(0);
            using TextReader textReader = reader.GetTextReader(1);
            byte[] bytes = new byte[3];
            int read = await stream.ReadAsync(bytes.AsMemory(), TestContext.Current.CancellationToken);
            string text = await textReader.ReadToEndAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(3, read);
            Assert.Equal(new byte[] { 0, 1, 255 }, bytes);
            Assert.Equal("stream access", text);
        }

        [Fact]
        public async Task ShouldStopAfterFirstRowGivenSingleRowBehaviorWhenQueryReturnsTwoRows()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT 1 AS value UNION ALL SELECT 2 AS value";

            // Act
            await using DbDataReader reader = await LiveSession.ReadFirstRowAsync(command, CommandBehavior.SingleRow);

            // Assert
            Assert.Equal(1, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldHideLaterResultsAndKeepConnectionUsableGivenSingleResultBehaviorWhenBatchRuns()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT 11 AS value; SELECT 12 AS value";

            // Act
            bool hasSecondResult;
            await using (DbDataReader reader = await LiveSession.ReadFirstRowAsync(command, CommandBehavior.SingleResult))
            {
                Assert.Equal(11, reader.GetInt32(0));
                hasSecondResult = await reader.NextResultAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, reader.FieldCount);
            }

            using CassieCommand reuse = _live.CreateCommand();
            reuse.CommandText = "SELECT 13 AS value";
            object? reused = await reuse.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.False(hasSecondResult);
            Assert.Equal(13, reused);
        }

        [Fact]
        public async Task ShouldEnforceColumnOrderGivenSequentialAccessWhenFieldsAreReadBackwards()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT 1 AS first_value, 2 AS second_value UNION ALL SELECT 3, 4";
            await using DbDataReader reader = await LiveSession.ReadFirstRowAsync(command, CommandBehavior.SequentialAccess);
            Assert.Equal(2, reader.GetInt32(1));

            // Act
            _ = Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));

            // Assert
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(3, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldCloseConnectionGivenCloseConnectionBehaviorWhenReaderCloses()
        {
            // Arrange
            await using CassieConnection owned = new() { ConnectionString = _live.ConnectionString };
            await owned.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = _live.CreateCommand(owned);
            command.CommandText = "SELECT 1 AS value";
            await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, TestContext.Current.CancellationToken);

            // Act
            await reader.CloseAsync();

            // Assert
            Assert.Equal(ConnectionState.Closed, owned.State);
        }

        [Fact]
        public async Task ShouldReportNoRowsGivenFilteredQueryWhenReaderOpens()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT 1 AS no_rows WHERE false";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, reader.FieldCount);
            Assert.False(reader.HasRows);
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldDescribeColumnsWithoutRowsGivenSchemaOnlyBehaviorWhenQueryIsParameterized()
        {
            // Arrange
            await _live.CreateProbeTableAsync();
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = $"SELECT id, label, @requested::int4 AS requested FROM {LiveSession.ProbeTable}";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "requested", DbType = DbType.Int32, Value = 7 });

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, TestContext.Current.CancellationToken);
            DataTable schema = await reader.GetSchemaTableAsync(TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Cassie returned no reader schema table.");
            ReadOnlyCollection<DbColumn> columns = reader.GetColumnSchema();

            // Assert
            Assert.Equal(3, reader.FieldCount);
            Assert.Equal(["id", "label", "requested"], [reader.GetName(0), reader.GetName(1), reader.GetName(2)]);
            Assert.Equal([typeof(int), typeof(string), typeof(int)], [reader.GetFieldType(0), reader.GetFieldType(1), reader.GetFieldType(2)]);
            Assert.Equal(3, schema.Rows.Count);
            Assert.Equal(23, Convert.ToInt32(schema.Rows[0]["ProviderType"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("int4", schema.Rows[0]["DataTypeName"]);
            Assert.Equal(4, Convert.ToInt32(schema.Rows[0]["ColumnSize"], System.Globalization.CultureInfo.InvariantCulture));
            _ = Assert.IsType<DBNull>(schema.Rows[0]["IsExpression"]);
            _ = Assert.IsType<DBNull>(schema.Rows[2]["IsExpression"]);
            Assert.Equal(3, columns.Count);
            Assert.Equal("id", columns[0].ColumnName);
            Assert.Equal(typeof(int), columns[0].DataType);
            Assert.Equal(4, columns[0].ColumnSize);
            Assert.Null(columns[0].IsExpression);
            Assert.Null(columns[2].IsExpression);
            Assert.False(reader.HasRows);
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldNotExecuteStatementGivenSchemaOnlyBehaviorWhenCommandIsInsert()
        {
            // Arrange
            await _live.CreateProbeTableAsync();
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = $"INSERT INTO {LiveSession.ProbeTable} VALUES (99, 'schema-only')";

            // Act
            bool read;
            int fieldCount;
            await using (DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, TestContext.Current.CancellationToken))
            {
                fieldCount = reader.FieldCount;
                read = await reader.ReadAsync(TestContext.Current.CancellationToken);
            }

            // Assert
            Assert.Equal(0, fieldCount);
            Assert.False(read);
            using CassieCommand countRows = _live.CreateCommand();
            countRows.CommandText = $"SELECT count(*) FROM {LiveSession.ProbeTable}";
            Assert.Equal(0L, await countRows.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldDiscoverTableAndColumnsGivenSchemaRestrictionsWhenCollectionsAreRequested()
        {
            // Arrange
            await _live.CreateProbeTableAsync();

            // Act
            DataTable tables = await _live.Connection.GetSchemaAsync("Tables", [null, "public", LiveSession.ProbeTable, "BASE TABLE"], TestContext.Current.CancellationToken);
            DataTable columns = await _live.Connection.GetSchemaAsync("Columns", [null, "public", LiveSession.ProbeTable], TestContext.Current.CancellationToken);

            // Assert
            DataRow tableRow = Assert.Single(tables.Rows.Cast<DataRow>());
            Assert.Equal(LiveSession.ProbeTable, tableRow["TABLE_NAME"]);
            Assert.Equal(2, columns.Rows.Count);
            Assert.Equal("id", columns.Rows[0]["COLUMN_NAME"]);
            Assert.Equal("label", columns.Rows[1]["COLUMN_NAME"]);
            Assert.Equal(1, Convert.ToInt32(columns.Rows[0]["ORDINAL_POSITION"], System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task ShouldExposeVoidCarrierGivenPgSleepWhenResultIsText()
        {
            // Arrange
            LiveFeature.Require("RECORD_TYPES");
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT pg_sleep(0.0) AS waited";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("void", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(string.Empty, reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldDecodeEmptyVoidGivenPreparedBinaryPgSleepWhenResultIsBinary()
        {
            // Arrange
            LiveFeature.Require("RECORD_TYPES");
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT pg_sleep(@seconds) AS waited";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "seconds", Value = 0d, PostgresTypeOid = 701 });
            await command.PrepareAsync(TestContext.Current.CancellationToken);

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("void", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(string.Empty, reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldExposeRecordCarrierGivenSearchClauseWhenRecursiveQueryRuns()
        {
            // Arrange
            LiveFeature.Require("RECORD_TYPES");
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "WITH RECURSIVE walk(node) AS (SELECT 1::BIGINT UNION ALL SELECT node + 1 FROM walk WHERE node < 3) SEARCH BREADTH FIRST BY node SET sequence SELECT sequence FROM walk ORDER BY sequence";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("record", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            _ = Assert.IsType<string>(reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldExposeRecordArrayCarrierGivenCycleClauseWhenRecursiveQueryRuns()
        {
            // Arrange
            LiveFeature.Require("RECORD_TYPES");
            await _live.CreateEdgesTableAsync();
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = $"WITH RECURSIVE walk(node) AS (SELECT 1::BIGINT UNION ALL SELECT edge.target FROM {LiveSession.EdgesTable} AS edge JOIN walk AS parent ON edge.source = parent.node) CYCLE node SET is_cycle USING path SELECT path FROM walk ORDER BY node, is_cycle";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("record[]", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            _ = Assert.IsType<string>(reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldReturnQueryCanceledGivenCancelWhileSleepingWhenSessionIsReused()
        {
            // Arrange
            using CassieCommand slowQuery = _live.CreateCommand();
            slowQuery.CommandText = "SELECT pg_sleep(10)";
            slowQuery.CommandTimeout = 5;
            Task<object?> execution = slowQuery.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            // Act
            slowQuery.Cancel();
            CassieException exception = await Assert.ThrowsAsync<CassieException>(() => execution);
            using CassieCommand recoverAfterCancel = _live.CreateCommand();
            recoverAfterCancel.CommandText = "SELECT 73::int4";
            object? recovered = await recoverAfterCancel.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("57014", exception.SqlState);
            Assert.Equal(73, recovered);
        }
    }
}
