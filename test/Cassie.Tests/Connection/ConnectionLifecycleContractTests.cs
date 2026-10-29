using Cntryl.Cassie.Connection;
using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    [Collection(nameof(PoolLifecycleContractTests))]
    public sealed class ConnectionLifecycleContractTests
    {
        [Fact]
        public void ShouldCloseConnectionGivenSyncDisposeWhenOpenConnectionIsDisposed()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => Task.CompletedTask));
            CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();

            // Act
            connection.Dispose();

            // Assert
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.True(ScriptedServer.WaitUntil(() => server.Terminated == 1));
        }

        [Fact]
        public void ShouldReturnSessionToPoolGivenSyncDisposeWhenMaximumPoolSizeIsOne()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => Task.CompletedTask));
            string connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1");
            CassieConnection.ClearAllPools();
            using (CassieConnection first = new() { ConnectionString = connectionString })
            {
                first.Open();
            }

            // Act
            using CassieConnection second = new() { ConnectionString = connectionString };
            second.Open();

            // Assert
            Assert.Equal(ConnectionState.Open, second.State);
            Assert.Equal(1, server.Accepted);
            CassieConnection.ClearAllPools();
        }

        [Fact]
        public void ShouldNotThrowGivenServerClosedSocketWhenConnectionIsDisposed()
        {
            // Arrange
            using ScriptedServer server = new(session =>
            {
                session.Close();
                return Task.CompletedTask;
            });
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            connection.Open();
            Assert.True(ScriptedServer.WaitUntil(() => server.Accepted == 1));

            // Act
            Exception? exception = Record.Exception(connection.Dispose);

            // Assert
            Assert.Null(exception);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
    }
}
