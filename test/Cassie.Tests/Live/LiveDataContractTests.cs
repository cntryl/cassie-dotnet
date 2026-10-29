using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using System.Data;
using System.Data.Common;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    [Collection(LiveEndpoint.CollectionName)]
    public sealed class LiveDataContractTests : IAsyncLifetime
    {
        private const string SourceDatabase = "postgres";
        private const string RestoredDatabase = "cassie_live_restore";

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
        public async Task ShouldFillDataTableGivenSelectCommandWhenAdapterFills()
        {
            // Arrange
            using CassieCommand select = _live.CreateCommand();
            select.CommandText = "SELECT 7 AS id, 'seven' AS label";
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
            using CassieCommand select = _live.CreateCommand();
            select.CommandText = "SELECT 7 AS id, 'seven' AS label";
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
        public async Task ShouldInsertAndAcceptRowGivenParameterizedInsertCommandWhenAdapterUpdates()
        {
            // Arrange
            await _live.CreateProbeTableAsync();
            using DataTable updates = new("updates");
            using CassieDataAdapter adapter = new();
            using CassieCommand insert = _live.CreateCommand();
            insert.CommandText = $"INSERT INTO {LiveSession.ProbeTable} (id, label) VALUES (@id, @label)";
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
            using CassieCommand verify = _live.CreateCommand();
            verify.CommandText = $"SELECT label FROM {LiveSession.ProbeTable} WHERE id = 3";
            Assert.Equal("gamma", await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldInsertRowsGivenCsvStreamWhenCopyFromRuns()
        {
            // Arrange
            await _live.CreateProbeTableAsync();
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = $"COPY {LiveSession.ProbeTable} FROM STDIN WITH (FORMAT CSV)";
            await using MemoryStream source = new("1,alpha\n2,beta\n"u8.ToArray());

            // Act
            int copied = await command.CopyFromAsync(source, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, copied);
            using CassieCommand countRows = _live.CreateCommand();
            countRows.CommandText = $"SELECT count(*) FROM {LiveSession.ProbeTable}";
            Assert.Equal(2L, await countRows.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldStreamCsvGivenOrderedQueryWhenCopyToRuns()
        {
            // Arrange
            await _live.CreateProbeTableAsync();
            using CassieCommand seed = _live.CreateCommand();
            seed.CommandText = $"INSERT INTO {LiveSession.ProbeTable} VALUES (2, 'beta'), (1, 'alpha')";
            _ = await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = $"COPY (SELECT id, label FROM {LiveSession.ProbeTable} ORDER BY id) TO STDOUT WITH (FORMAT CSV)";
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
            await _live.CreatePortalTableAsync();
            using (CassieCommand copy = _live.CreateCommand())
            {
                copy.CommandText = $"COPY {LiveSession.PortalTable} FROM STDIN WITH (FORMAT CSV)";
                await using MemoryStream source = new(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 260)) + "\n"));
                Assert.Equal(260, await copy.CopyFromAsync(source, TestContext.Current.CancellationToken));
            }

            using CassieCommand select = _live.CreateCommand();
            select.CommandText = $"SELECT id, @marker::int4 AS marker FROM {LiveSession.PortalTable} ORDER BY id";
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
            await _live.CreateProbeTableAsync();
            using CassieCommand seed = _live.CreateCommand();
            seed.CommandText = $"INSERT INTO {LiveSession.ProbeTable} VALUES (1, 'alpha')";
            _ = await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Act
            await using (DbTransaction transaction = await _live.Connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                using CassieCommand command = _live.CreateCommand();
                command.CommandText = $"INSERT INTO {LiveSession.ProbeTable} VALUES (3, 'gamma')";
                command.Transaction = transaction;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }

            // Assert
            using CassieCommand countRows = _live.CreateCommand();
            countRows.CommandText = $"SELECT count(*) FROM {LiveSession.ProbeTable}";
            Assert.Equal(1L, await countRows.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        [Trait("Isolation", "Server")]
        public async Task ShouldRestoreTableRowsGivenBackupImageWhenRestoredIntoNewDatabase()
        {
            // Arrange
            Assert.SkipUnless(
                string.Equals(new CassieConnectionStringBuilder(_live.ConnectionString).Database, SourceDatabase, StringComparison.Ordinal),
                $"The database image test is written for Database={SourceDatabase}.");
            await _live.CreateProbeTableAsync();
            using CassieCommand seed = _live.CreateCommand();
            seed.CommandText = $"INSERT INTO {LiveSession.ProbeTable} VALUES (1, 'alpha'), (2, 'beta'), (3, 'gamma')";
            _ = await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            using MemoryStream image = new();
            using CassieCommand backup = _live.CreateCommand();
            backup.CommandText = $"BACKUP DATABASE {SourceDatabase} TO STDOUT";
            int backupRows = await backup.CopyToAsync(image, TestContext.Current.CancellationToken);
            image.Position = 0;
            await _live.DropProbeTableAsync();
            using CassieCommand restore = _live.CreateCommand();
            restore.CommandText = $"RESTORE DATABASE {RestoredDatabase} FROM STDIN";

            // Act
            CassieException? duplicate = null;
            try
            {
                _ = await restore.CopyFromAsync(image, TestContext.Current.CancellationToken);
            }
            catch (CassieException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                duplicate = exception;
            }

            Assert.SkipWhen(duplicate is not null, $"{RestoredDatabase} already exists and this server cannot drop restored databases; use a fresh server.");
            await using CassieConnection restored = new()
            {
                ConnectionString = new CassieConnectionStringBuilder(_live.ConnectionString) { Database = RestoredDatabase, Pooling = false }.ConnectionString
            };
            await restored.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand verify = _live.CreateCommand(restored);
            verify.CommandText = $"SELECT id, label FROM {LiveSession.ProbeTable} ORDER BY id";
            List<(int Id, string Label)> rows = [];
            await using DbDataReader reader = await verify.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            // Assert
            Assert.Equal(0, backupRows);
            Assert.NotEqual(0, image.Length);
            Assert.Equal([(1, "alpha"), (2, "beta"), (3, "gamma")], rows);
        }
    }
}
