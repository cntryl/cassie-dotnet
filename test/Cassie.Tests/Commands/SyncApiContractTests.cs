using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands
{
    public sealed class SyncApiContractTests
    {
        [Fact]
        public void ShouldReturnScalarGivenSyncExecuteScalarWhenServerReturnsOneRow()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(async _ =>
            {
                await session.WriteIntRowDescriptionAsync();
                await session.WriteTextRowAsync("7");
                await session.WriteCommandCompleteAsync("SELECT 1");
            }));
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 7";

            // Act
            object? result = command.ExecuteScalar();

            // Assert
            Assert.Equal(7, result);
        }

        [Theory]
        [InlineData("INSERT 0 3", 3)]
        [InlineData("UPDATE 5", 5)]
        [InlineData("DELETE 2", 2)]
        [InlineData("CREATE TABLE", -1)]
        [InlineData("SET", -1)]
        public void ShouldReportAffectedRowsGivenCommandTagWhenSyncExecuteNonQueryRuns(string tag, int expected)
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => session.WriteCommandCompleteAsync(tag)));
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "STATEMENT";

            // Act
            int affected = command.ExecuteNonQuery();

            // Assert
            Assert.Equal(expected, affected);
        }

        [Fact]
        public void ShouldReportSavepointSupportGivenOpenTransactionWhenCapabilityIsQueried()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(sql => session.WriteCommandCompleteAsync(sql.Split(' ')[0])));
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();

            // Act
            using DbTransaction transaction = connection.BeginTransaction();
            bool supported = transaction.SupportsSavepoints;
            transaction.Save("before_change");
            transaction.Rollback("before_change");
            transaction.Release("before_change");
            transaction.Commit();

            // Assert
            Assert.True(supported);
        }

        [Fact]
        public void ShouldExposeProviderTypesGivenOpenConnectionWhenTypedMembersAreUsed()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(sql => session.WriteCommandCompleteAsync(sql.Split(' ')[0])));
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();

            // Act
            using CassieCommand command = connection.CreateCommand();
            CassieParameter parameter = command.CreateParameter();
            _ = command.Parameters.Add(parameter);
            using CassieBatch batch = connection.CreateBatch();
            using Cassie.Data.CassieTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            batch.Transaction = transaction;
            CassieConnection? whileActive = transaction.Connection;
            transaction.Commit();

            // Assert
            Assert.Same(connection, command.Connection);
            Assert.Same(connection, batch.Connection);
            Assert.Same(connection, whileActive);
            Assert.Same(parameter, command.Parameters[0]);
            Assert.Same(transaction, command.Transaction);
            Assert.Same(transaction, batch.Transaction);
            Assert.Null(transaction.Connection);
            Assert.Null(((DbTransaction)transaction).Connection);
            Assert.Same(CassieProviderFactory.Instance, DbProviderFactories.GetFactory(connection));
        }

        [Fact]
        public void ShouldRejectForeignObjectsGivenBaseTypedPropertiesWhenNonCassieValuesAreAssigned()
        {
            // Arrange
            using CassieCommand command = new();
            using CassieBatch batch = new();
            using System.Data.DataTable unrelated = new();
            using DbConnection foreignConnection = new ForeignConnection();

            // Act
            void AssignCommandConnection()
            {
                ((DbCommand)command).Connection = foreignConnection;
            }

            void AssignBatchConnection()
            {
                ((DbBatch)batch).Connection = foreignConnection;
            }

            // Assert
            _ = Assert.Throws<InvalidCastException>(AssignCommandConnection);
            _ = Assert.Throws<InvalidCastException>(AssignBatchConnection);
            Assert.Null(command.Connection);
            Assert.Null(batch.Connection);
        }

        [Fact]
        public void ShouldReportFactoryCapabilitiesGivenProviderFactoryWhenCapabilitiesAreQueried()
        {
            // Arrange
            DbProviderFactory factory = CassieProviderFactory.Instance;

            // Act
            using DbDataAdapter? adapter = factory.CanCreateDataAdapter ? factory.CreateDataAdapter() : null;

            // Assert
            Assert.True(factory.CanCreateDataAdapter);
            Assert.NotNull(adapter);
            Assert.True(factory.CanCreateBatch);
            Assert.False(factory.CanCreateCommandBuilder);
            Assert.False(factory.CanCreateDataSourceEnumerator);
        }

        [Fact]
        public void ShouldCommitGivenSyncTransactionWhenCommitIsCalled()
        {
            // Arrange
            List<string> statements = [];
            using ScriptedServer server = new(session => session.ServeQueriesAsync(sql =>
            {
                statements.Add(sql);
                return session.WriteCommandCompleteAsync(sql.Split(' ')[0]);
            }));
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();

            // Act
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                transaction.Commit();
            }

            // Assert
            Assert.Equal(["BEGIN ISOLATION LEVEL READ COMMITTED", "COMMIT"], statements);
        }

        [Fact]
        public void ShouldNotThrowGivenServerClosedSocketWhenTransactionIsDisposed()
        {
            // Arrange
            using ScriptedServer server = new(async session =>
            {
                await session.ServeQueriesAsync(sql => session.WriteCommandCompleteAsync("BEGIN"));
            });
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();
            DbTransaction transaction = connection.BeginTransaction();
            server.StopAccepting();

            // Act
            Exception? exception = Record.Exception(connection.Close);
            Exception? disposeException = Record.Exception(transaction.Dispose);

            // Assert
            Assert.Null(exception);
            Assert.Null(disposeException);
        }

        [Fact]
        public void ShouldNotThrowGivenServerClosedSocketWhenPreparedCommandIsDisposed()
        {
            // Arrange
            using ScriptedServer server = new(async session =>
            {
                _ = await session.ReadFrameAsync();
                _ = await session.ReadFrameAsync();
                _ = await session.ReadFrameAsync();
                await session.WriteAsync((byte)'1', []);
                await session.WriteAsync((byte)'t', [0, 0]);
                await session.WriteAsync((byte)'n', []);
                await session.WriteReadyAsync();
                session.Close();
            });
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();
            using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 1" };
            command.Prepare();
            Assert.True(ScriptedServer.WaitUntil(() => server.Accepted == 1));

            // Act
            Exception? exception = Record.Exception(command.Dispose);

            // Assert
            Assert.Null(exception);
        }

        private sealed class ForeignConnection : DbConnection
        {
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ConnectionString { get; set; } = string.Empty;

            public override string Database => string.Empty;

            public override string DataSource => string.Empty;

            public override string ServerVersion => string.Empty;

            public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;

            public override void ChangeDatabase(string databaseName)
            {
            }

            public override void Close()
            {
            }

            public override void Open()
            {
            }

            protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
            {
                throw new NotSupportedException();
            }

            protected override DbCommand CreateDbCommand()
            {
                throw new NotSupportedException();
            }
        }
    }
}
