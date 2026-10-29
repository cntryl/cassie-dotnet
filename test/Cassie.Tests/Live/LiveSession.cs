using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    /// <summary>
    /// Per-test setup and teardown for live tests: one dedicated non-pooled connection and a fixed
    /// catalog of schema objects shared by this serialized test collection. Teardown drops only objects created by this session and reports failures.
    /// </summary>
    internal sealed class LiveSession : IAsyncDisposable
    {
        private readonly List<CatalogObject> _createdObjects = [];

        public const string ProbeTable = "cassie_live_probe";
        public const string PortalTable = "cassie_live_portal";
        public const string CallTable = "cassie_live_call";
        public const string EdgesTable = "cassie_live_edges";
        public const string StoreProcedure = "cassie_live_store";
        public const string DateStyleView = "cassie_live_datestyle_view";

        public CassieConnection Connection { get; private set; } = null!;

        public string ConnectionString { get; private set; } = string.Empty;

        public async ValueTask InitializeAsync()
        {
            ConnectionString = new CassieConnectionStringBuilder(LiveEndpoint.Require()) { Pooling = false }.ConnectionString;
            Connection = new CassieConnection { ConnectionString = ConnectionString };
            await Connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Connection is null)
            {
                return;
            }

            await Connection.DisposeAsync();
            if (_createdObjects.Count > 0)
            {
                await using CassieConnection cleanup = new() { ConnectionString = ConnectionString };
                await cleanup.OpenAsync(CancellationToken.None);
                await DropCatalogAsync(cleanup);
            }
        }

        /// <summary>Returns a unique identifier for schema objects and pool identities.</summary>
        public static string UniqueName(string prefix)
        {
            return prefix + "_" + Guid.NewGuid().ToString("N");
        }

        public CassieCommand CreateCommand(CassieConnection? connection = null)
        {
            return (CassieCommand)(connection ?? Connection).CreateCommand();
        }

        /// <summary>Creates the probe table with integer and text columns.</summary>
        public async Task CreateProbeTableAsync()
        {
            using CassieCommand command = CreateCommand();
            command.CommandText = $"CREATE TABLE {ProbeTable} (id INT4, label TEXT)";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _createdObjects.Add(CatalogObject.ProbeTable);
        }

        /// <summary>Removes the source table after its backup is complete, before a server-wide restore.</summary>
        public async Task DropProbeTableAsync()
        {
            using CassieCommand command = CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS {ProbeTable}";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _ = _createdObjects.Remove(CatalogObject.ProbeTable);
        }

        /// <summary>Creates the single-column table used by portal paging.</summary>
        public async Task CreatePortalTableAsync()
        {
            using CassieCommand command = CreateCommand();
            command.CommandText = $"CREATE TABLE {PortalTable} (id INT4)";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _createdObjects.Add(CatalogObject.PortalTable);
        }

        /// <summary>Creates the call table and the procedure that inserts its argument.</summary>
        public async Task CreateStoreProcedureAsync()
        {
            using CassieCommand table = CreateCommand();
            table.CommandText = $"CREATE TABLE {CallTable} (title TEXT)";
            _ = await table.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _createdObjects.Add(CatalogObject.CallTable);
            using CassieCommand procedure = CreateCommand();
            procedure.CommandText = $"CREATE PROCEDURE {StoreProcedure}(title TEXT) AS \"INSERT INTO {CallTable} (title) VALUES ($1)\"";
            _ = await procedure.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _createdObjects.Add(CatalogObject.StoreProcedure);
        }

        /// <summary>Creates the two-node cyclic edge table used by recursive CTE tests.</summary>
        public async Task CreateEdgesTableAsync()
        {
            using CassieCommand create = CreateCommand();
            create.CommandText = $"CREATE TABLE {EdgesTable} (source BIGINT, target BIGINT)";
            _ = await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _createdObjects.Add(CatalogObject.EdgesTable);
            using CassieCommand insert = CreateCommand();
            insert.CommandText = $"INSERT INTO {EdgesTable} VALUES (1, 2), (2, 1)";
            _ = await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Creates a view whose evaluation changes the session DateStyle.</summary>
        public async Task CreateDateStyleViewAsync()
        {
            using CassieCommand command = CreateCommand();
            command.CommandText = $"CREATE VIEW {DateStyleView} AS SELECT set_config('datestyle', 'SQL, DMY', false) AS changed";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            _createdObjects.Add(CatalogObject.DateStyleView);
        }

        /// <summary>Executes the command and advances to the first row, failing the test when there is none.</summary>
        public static async Task<DbDataReader> ReadFirstRowAsync(CassieCommand command, CommandBehavior behavior = CommandBehavior.Default)
        {
            ArgumentNullException.ThrowIfNull(command);
            DbDataReader reader = await command.ExecuteReaderAsync(behavior, TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), "The query returned no row.");
            return reader;
        }

        private async Task DropCatalogAsync(CassieConnection cleanup)
        {
            List<CassieException> failures = [];
            for (int index = _createdObjects.Count - 1; index >= 0; index--)
            {
                using CassieCommand drop = _createdObjects[index] switch
                {
                    CatalogObject.ProbeTable => new() { CommandText = $"DROP TABLE IF EXISTS {ProbeTable}" },
                    CatalogObject.PortalTable => new() { CommandText = $"DROP TABLE IF EXISTS {PortalTable}" },
                    CatalogObject.CallTable => new() { CommandText = $"DROP TABLE IF EXISTS {CallTable}" },
                    CatalogObject.EdgesTable => new() { CommandText = $"DROP TABLE IF EXISTS {EdgesTable}" },
                    CatalogObject.StoreProcedure => new() { CommandText = $"DROP PROCEDURE IF EXISTS {StoreProcedure}" },
                    CatalogObject.DateStyleView => new() { CommandText = $"DROP VIEW IF EXISTS {DateStyleView}" },
                    _ => throw new InvalidOperationException("The live session registered an unknown schema object.")
                };
                drop.Connection = cleanup;
                try
                {
                    _ = await drop.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (CassieException exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Live test schema cleanup failed.", failures);
            }
        }

        private enum CatalogObject
        {
            ProbeTable,
            PortalTable,
            CallTable,
            EdgesTable,
            StoreProcedure,
            DateStyleView
        }
    }
}
