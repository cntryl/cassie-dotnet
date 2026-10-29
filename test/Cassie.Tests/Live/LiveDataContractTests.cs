using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    public sealed class LiveDataContractTests(LiveServerFixture server) : LiveTestBase(server)
    {
        [Fact]
        public async Task ShouldFillDataTableGivenSelectCommandWhenAdapterFills()
        {
            // Arrange
            using CassieCommand select = CreateCommand("SELECT 7 AS id, 'seven' AS label");
            using CassieDataAdapter adapter = new(select);
            using DataTable rows = new("probe");

            // Act
            _ = await Task.Run(() => adapter.Fill(rows), TestContext.Current.CancellationToken);

            // Assert
            DataRow row = Assert.Single(rows.Rows.Cast<DataRow>());
            Assert.Equal(7, Convert.ToInt32(row["id"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("seven", row["label"]);
        }

        [Fact]
        public async Task ShouldFillNamedTableGivenSelectCommandWhenAdapterFillsDataSet()
        {
            // Arrange
            using CassieCommand select = CreateCommand("SELECT 7 AS id, 'seven' AS label");
            using CassieDataAdapter adapter = new(select);
            using DataSet dataSet = new();

            // Act
            _ = await Task.Run(() => adapter.Fill(dataSet, "probe"), TestContext.Current.CancellationToken);

            // Assert
            DataTable table = Assert.IsType<DataTable>(dataSet.Tables["probe"]);
            DataRow row = Assert.Single(table.Rows.Cast<DataRow>());
            Assert.Equal("seven", row["label"]);
        }

        [Fact]
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The schema is fixed and CassieDataReader reports only CLR types rooted by its OID map.")]
        public async Task ShouldInsertAndAcceptRowGivenParameterizedInsertCommandWhenAdapterUpdates()
        {
            // Arrange
            string table = await CreateTableAsync("id INT4, label TEXT");
            using DataTable updates = new("updates");
            using CassieDataAdapter adapter = new();
            using CassieCommand insert = CreateCommand($"INSERT INTO {table} (id, label) VALUES (@id, @label)");
            _ = updates.Columns.Add("id", typeof(int));
            _ = updates.Columns.Add("label", typeof(string));
            DataRow row = updates.Rows.Add(3, "gamma");
            _ = insert.Parameters.Add(new CassieParameter { ParameterName = "id", SourceColumn = "id", DbType = DbType.Int32 });
            _ = insert.Parameters.Add(new CassieParameter { ParameterName = "label", SourceColumn = "label", DbType = DbType.String });
            adapter.InsertCommand = insert;

            // Act
            int updated = await Task.Run(() => adapter.Update(updates), TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, updated);
            Assert.Equal(DataRowState.Unchanged, row.RowState);
            Assert.Equal("gamma", await ExecuteScalarAsync($"SELECT label FROM {table} WHERE id = 3"));
        }

        [Fact]
        public async Task ShouldInsertRowsGivenCsvStreamWhenCopyFromRuns()
        {
            // Arrange
            string table = await CreateTableAsync("id INT4, label TEXT");
            using CassieCommand command = CreateCommand($"COPY {table} FROM STDIN WITH (FORMAT CSV)");
            await using MemoryStream source = new("1,alpha\n2,beta\n"u8.ToArray());

            // Act
            int copied = await command.CopyFromAsync(source, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, copied);
            Assert.Equal(2L, await ExecuteScalarAsync($"SELECT count(*) FROM {table}"));
        }

        [Fact]
        public async Task ShouldStreamCsvGivenOrderedQueryWhenCopyToRuns()
        {
            // Arrange
            string table = await CreateTableAsync("id INT4, label TEXT");
            _ = await ExecuteNonQueryAsync($"INSERT INTO {table} VALUES (2, 'beta'), (1, 'alpha')");
            using CassieCommand command = CreateCommand($"COPY (SELECT id, label FROM {table} ORDER BY id) TO STDOUT WITH (FORMAT CSV)");
            await using MemoryStream destination = new();

            // Act
            int copied = await command.CopyToAsync(destination, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, copied);
            Assert.Equal("1,alpha\n2,beta\n", Encoding.UTF8.GetString(destination.ToArray()));
        }

        [Fact]
        public async Task ShouldReturnEveryRowGivenResultLargerThanOnePageWhenPortalIsPaged()
        {
            // Arrange
            string table = await CreateTableAsync("id INT4");
            using (CassieCommand copy = CreateCommand($"COPY {table} FROM STDIN WITH (FORMAT CSV)"))
            {
                await using MemoryStream source = new(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 260)) + "\n"));
                Assert.Equal(260, await copy.CopyFromAsync(source, TestContext.Current.CancellationToken));
            }

            using CassieCommand select = CreateCommand($"SELECT id, @marker::int4 AS marker FROM {table} ORDER BY id");
            _ = select.Parameters.Add(new CassieParameter { ParameterName = "marker", Value = 7 });

            // Act
            List<(int Id, int Marker)> rows = [];
            await using (DbDataReader reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            // Assert
            Assert.Equal(Enumerable.Range(1, 260).Select(id => (id, 7)), rows);
        }

        [Fact]
        public async Task ShouldDiscardInsertGivenRollbackWhenTransactionEnds()
        {
            // Arrange
            string table = await CreateTableAsync("id INT4, label TEXT");
            _ = await ExecuteNonQueryAsync($"INSERT INTO {table} VALUES (1, 'alpha')");

            // Act
            await using (DbTransaction transaction = await Connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                using CassieCommand command = CreateCommand($"INSERT INTO {table} VALUES (3, 'gamma')");
                command.Transaction = transaction;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }

            // Assert
            Assert.Equal(1L, await ExecuteScalarAsync($"SELECT count(*) FROM {table}"));
        }

        [Fact]
        [SuppressMessage("Security", "CA2100", Justification = "The source database is validated as a simple ASCII identifier and the restore database name is generated locally; identifiers cannot be bound as parameters.")]
        public async Task ShouldRestoreTableRowsGivenBackupImageWhenRestoredIntoNewDatabase()
        {
            // Arrange
            string sourceDatabase = new CassieConnectionStringBuilder(ConnectionString).Database;
            Assert.True(IsSimpleDatabaseName(sourceDatabase), "The database image test requires a simple Cassie database name.");
            string restoredDatabase = UniqueName("cassie_dotnet_restore");
            string table = await CreateTableAsync("id INT4, label TEXT");
            _ = await ExecuteNonQueryAsync($"INSERT INTO {table} VALUES (1, 'alpha'), (2, 'beta'), (3, 'gamma')");
            using MemoryStream image = new();
            using CassieCommand backup = CreateCommand($"BACKUP DATABASE {sourceDatabase} TO STDOUT");
            int backupRows = await backup.CopyToAsync(image, TestContext.Current.CancellationToken);
            image.Position = 0;
            using (CassieCommand restore = CreateCommand($"RESTORE DATABASE {restoredDatabase} FROM STDIN"))
            {
                // Act
                _ = await restore.CopyFromAsync(image, TestContext.Current.CancellationToken);
            }

            // Assert
            Assert.Equal(0, backupRows);
            Assert.NotEqual(0, image.Length);
            CassieConnectionStringBuilder restoredSettings = new(ConnectionString) { Database = restoredDatabase, Pooling = false };
            await using CassieConnection restored = new() { ConnectionString = restoredSettings.ConnectionString };
            await restored.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand verify = CreateCommand($"SELECT id, label FROM {table} ORDER BY id", connection: restored);
            List<(int Id, string Label)> rows = [];
            await using DbDataReader reader = await verify.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            Assert.Equal([(1, "alpha"), (2, "beta"), (3, "gamma")], rows);
        }

        private static bool IsSimpleDatabaseName(string value)
        {
            return value.Length > 0
                && (char.IsAsciiLetter(value[0]) || value[0] == '_')
                && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        }
    }
}
