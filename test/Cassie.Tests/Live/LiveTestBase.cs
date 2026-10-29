using System.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    /// <summary>
    /// Per-test setup and teardown for live tests: one dedicated non-pooled connection and
    /// uniquely named schema objects that are dropped even when the test fails.
    /// </summary>
    [Collection(LiveCollection.Name)]
    public abstract class LiveTestBase(LiveServerFixture server) : IAsyncLifetime
    {
        private readonly List<string> _cleanup = [];

        protected LiveServerFixture Server { get; } = server;

        protected CassieConnection Connection { get; private set; } = null!;

        protected string ConnectionString { get; private set; } = string.Empty;

        public async ValueTask InitializeAsync()
        {
            ConnectionString = new CassieConnectionStringBuilder(Server.RequireEndpoint()) { Pooling = false }.ConnectionString;
            Connection = new CassieConnection { ConnectionString = ConnectionString };
            await Connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            if (Connection is null)
            {
                return;
            }

            await using (Connection)
            {
                for (int index = _cleanup.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        _ = await ExecuteNonQueryAsync(_cleanup[index]);
                    }
                    catch (CassieException)
                    {
                        // Best-effort cleanup on a disposable server must not mask the test result.
                    }
                }
            }
        }

        /// <summary>Registers a statement to run during teardown, in reverse registration order.</summary>
        protected void RegisterCleanup(string sql)
        {
            _cleanup.Add(sql);
        }

        /// <summary>Returns a unique identifier so tests never collide on schema object names.</summary>
        protected static string UniqueName(string prefix)
        {
            return prefix + "_" + Guid.NewGuid().ToString("N");
        }

        /// <summary>Creates a uniquely named table and registers its removal.</summary>
        protected async Task<string> CreateTableAsync(string columns)
        {
            string name = UniqueName("cassie_live");
            RegisterCleanup("DROP TABLE IF EXISTS " + name);
            _ = await ExecuteNonQueryAsync($"CREATE TABLE {name} ({columns})");
            return name;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Live tests build SQL from literals and locally generated identifiers against a disposable server.")]
        protected CassieCommand CreateCommand(string sql, bool binaryResults = false, CassieConnection? connection = null)
        {
            CassieCommand command = (CassieCommand)(connection ?? Connection).CreateCommand();
            command.CommandText = sql;
            command.BinaryResults = binaryResults;
            return command;
        }

        protected async Task<int> ExecuteNonQueryAsync(string sql)
        {
            using CassieCommand command = CreateCommand(sql);
            return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        protected async Task<object?> ExecuteScalarAsync(string sql)
        {
            using CassieCommand command = CreateCommand(sql);
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Executes the command and advances to the first row, failing the test when there is none.</summary>
        protected static async Task<DbDataReader> ReadFirstRowAsync(CassieCommand command, CommandBehavior behavior = CommandBehavior.Default)
        {
            ArgumentNullException.ThrowIfNull(command);
            DbDataReader reader = await command.ExecuteReaderAsync(behavior, TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), "The query returned no row.");
            return reader;
        }
    }
}
