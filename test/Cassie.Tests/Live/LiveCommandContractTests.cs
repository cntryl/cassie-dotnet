using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    public sealed class LiveCommandContractTests(LiveServerFixture server) : LiveTestBase(server)
    {
        [Fact]
        public async Task ShouldRebindValueGivenServerPreparedCommandWhenParameterChanges()
        {
            // Arrange
            using CassieCommand command = CreateCommand("SELECT @value::int AS value");
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
            using CassieCommand command = CreateCommand("SELECT @value::int AS value");
            CassieParameter parameter = new() { ParameterName = "value", DbType = DbType.Int32, Value = 17 };
            _ = command.Parameters.Add(parameter);
            await command.PrepareAsync(TestContext.Current.CancellationToken);
            _ = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            _ = await ExecuteNonQueryAsync("DEALLOCATE ALL");
            await Connection.CloseAsync();
            await Connection.OpenAsync(TestContext.Current.CancellationToken);

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
            using CassieCommand invalid = CreateCommand("SELECT FROM");

            // Act
            CassieException exception = await Assert.ThrowsAsync<CassieException>(() => invalid.PrepareAsync(TestContext.Current.CancellationToken));
            object? recovered = await ExecuteScalarAsync("SELECT 1");

            // Assert
            Assert.Equal("42601", exception.SqlState);
            Assert.Equal(1, recovered);
        }

        [Fact]
        public async Task ShouldRunProcedureBodyGivenBoundInputWhenSqlTextCallIsExecuted()
        {
            // Arrange
            string table = await CreateTableAsync("title TEXT");
            string procedure = UniqueName("cassie_live_store");
            RegisterCleanup("DROP PROCEDURE IF EXISTS " + procedure);
            _ = await ExecuteNonQueryAsync($"CREATE PROCEDURE {procedure}(title TEXT) AS \"INSERT INTO {table} (title) VALUES ($1)\"");
            using CassieCommand call = CreateCommand($"CALL {procedure}(@title)");
            _ = call.Parameters.Add(new CassieParameter { ParameterName = "title", Value = "input-bound-call" });

            // Act
            _ = await call.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("input-bound-call", await ExecuteScalarAsync($"SELECT title FROM {table}"));
        }

        [Fact]
        public async Task ShouldBindPositionallyGivenPreparedStoredProcedureWhenParameterValueChanges()
        {
            // Arrange
            string table = await CreateTableAsync("title TEXT");
            string procedure = UniqueName("cassie_live_store");
            RegisterCleanup("DROP PROCEDURE IF EXISTS " + procedure);
            _ = await ExecuteNonQueryAsync($"CREATE PROCEDURE {procedure}(title TEXT) AS \"INSERT INTO {table} (title) VALUES ($1)\"");
            using CassieCommand call = CreateCommand(procedure);
            call.CommandType = CommandType.StoredProcedure;
            CassieParameter title = new() { ParameterName = "title", Value = "ado-stored-procedure-call" };
            _ = call.Parameters.Add(title);
            await call.PrepareAsync(TestContext.Current.CancellationToken);

            // Act
            _ = await call.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            title.Value = "ado-stored-procedure-call-updated";
            _ = await call.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("ado-stored-procedure-call", await ExecuteScalarAsync($"SELECT title FROM {table} WHERE title = 'ado-stored-procedure-call'"));
            Assert.Equal("ado-stored-procedure-call-updated", await ExecuteScalarAsync($"SELECT title FROM {table} WHERE title = 'ado-stored-procedure-call-updated'"));
        }

        [Fact]
        public async Task ShouldExposeStreamsGivenByteaAndTextColumnsWhenReaderOpens()
        {
            // Arrange
            using CassieCommand command = CreateCommand("SELECT @blob::bytea AS blob, @text::text AS text");
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "blob", Value = new byte[] { 0, 1, 255 } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "text", Value = "stream access" });
            await using DbDataReader reader = await ReadFirstRowAsync(command);

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
            using CassieCommand command = CreateCommand("SELECT 1 AS value UNION ALL SELECT 2 AS value");

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command, CommandBehavior.SingleRow);

            // Assert
            Assert.Equal(1, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldHideLaterResultsAndKeepConnectionUsableGivenSingleResultBehaviorWhenBatchRuns()
        {
            // Arrange
            using CassieCommand command = CreateCommand("SELECT 11 AS value; SELECT 12 AS value");

            // Act
            bool hasSecondResult;
            await using (DbDataReader reader = await ReadFirstRowAsync(command, CommandBehavior.SingleResult))
            {
                Assert.Equal(11, reader.GetInt32(0));
                hasSecondResult = await reader.NextResultAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, reader.FieldCount);
            }

            object? reused = await ExecuteScalarAsync("SELECT 13 AS value");

            // Assert
            Assert.False(hasSecondResult);
            Assert.Equal(13, reused);
        }

        [Fact]
        public async Task ShouldEnforceColumnOrderGivenSequentialAccessWhenFieldsAreReadBackwards()
        {
            // Arrange
            using CassieCommand command = CreateCommand("SELECT 1 AS first_value, 2 AS second_value UNION ALL SELECT 3, 4");
            await using DbDataReader reader = await ReadFirstRowAsync(command, CommandBehavior.SequentialAccess);
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
            await using CassieConnection owned = new() { ConnectionString = ConnectionString };
            await owned.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = CreateCommand("SELECT 1 AS value", connection: owned);
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
            using CassieCommand command = CreateCommand("SELECT 1 AS no_rows WHERE false");

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
            string table = await CreateTableAsync("id INT4, label TEXT");
            using CassieCommand command = CreateCommand($"SELECT id, label, @requested::int4 AS requested FROM {table}");
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
            string table = await CreateTableAsync("id INT4, label TEXT");
            using CassieCommand command = CreateCommand($"INSERT INTO {table} VALUES (99, 'schema-only')");

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
            Assert.Equal(0L, await ExecuteScalarAsync($"SELECT count(*) FROM {table}"));
        }

        [Fact]
        public async Task ShouldDiscoverTableAndColumnsGivenSchemaRestrictionsWhenCollectionsAreRequested()
        {
            // Arrange
            string table = await CreateTableAsync("id INT4, label TEXT");

            // Act
            DataTable tables = await Connection.GetSchemaAsync("Tables", [null, "public", table, "BASE TABLE"], TestContext.Current.CancellationToken);
            DataTable columns = await Connection.GetSchemaAsync("Columns", [null, "public", table], TestContext.Current.CancellationToken);

            // Assert
            DataRow tableRow = Assert.Single(tables.Rows.Cast<DataRow>());
            Assert.Equal(table, tableRow["TABLE_NAME"]);
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
            using CassieCommand command = CreateCommand("SELECT pg_sleep(0.0) AS waited");

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
            using CassieCommand command = CreateCommand("SELECT pg_sleep(@seconds) AS waited", binaryResults: true);
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
            using CassieCommand command = CreateCommand("WITH RECURSIVE walk(node) AS (SELECT 1::BIGINT UNION ALL SELECT node + 1 FROM walk WHERE node < 3) SEARCH BREADTH FIRST BY node SET sequence SELECT sequence FROM walk ORDER BY sequence");

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
            string edges = await CreateTableAsync("source BIGINT, target BIGINT");
            _ = await ExecuteNonQueryAsync($"INSERT INTO {edges} VALUES (1, 2), (2, 1)");
            using CassieCommand command = CreateCommand($"WITH RECURSIVE walk(node) AS (SELECT 1::BIGINT UNION ALL SELECT edge.target FROM {edges} AS edge JOIN walk AS parent ON edge.source = parent.node) CYCLE node SET is_cycle USING path SELECT path FROM walk ORDER BY node, is_cycle");

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
            using CassieCommand slowQuery = CreateCommand("SELECT pg_sleep(10)");
            slowQuery.CommandTimeout = 5;
            Task<object?> execution = slowQuery.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            // Act
            slowQuery.Cancel();
            CassieException exception = await Assert.ThrowsAsync<CassieException>(() => execution);
            object? recovered = await ExecuteScalarAsync("SELECT 73::int4");

            // Assert
            Assert.Equal("57014", exception.SqlState);
            Assert.Equal(73, recovered);
        }
    }
}
