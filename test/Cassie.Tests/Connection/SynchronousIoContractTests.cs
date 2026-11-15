using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    /// <summary>
    /// Synchronous ADO.NET calls must run entirely on the calling thread. Each test compares the
    /// per-thread count of waits on unfinished tasks before and after driving the synchronous API.
    /// </summary>
    public sealed class SynchronousIoContractTests
    {
        private const int RowCount = 3000;

        [Fact]
        public void ShouldCountPendingWaitGivenUnfinishedTaskWhenSynchronousCallBlocks()
        {
            // Arrange
            int before = SynchronousCall.PendingWaitsOnThisThread;
            using ManualResetEventSlim release = new();
            Task pending = Task.Run(release.Wait, TestContext.Current.CancellationToken);

            // Act
            using Timer releaseSoon = new(_ => release.Set(), null, TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
            SynchronousCall.Wait(pending);
            SynchronousCall.Wait(Task.CompletedTask);

            // Assert
            Assert.Equal(before + 1, SynchronousCall.PendingWaitsOnThisThread);
        }

        [Theory]
        [InlineData("Pooling=false", false)]
        [InlineData("Pooling=false;Command Timeout=0", false)]
        [InlineData("Pooling=true;Maximum Pool Size=2", true)]
        public void ShouldStayOnCallingThreadGivenSynchronousCommandsWhenRowsSpanManyTransportReads(string settings, bool pooled)
        {
            // Arrange
            using ScriptedServer server = new(ServeRowsAsync);
            int before = SynchronousCall.PendingWaitsOnThisThread;
            long sum = 0;
            int rows = 0;

            // Act
            for (int lease = 0; lease < 2; lease++)
            {
                using CassieConnection connection = new() { ConnectionString = server.ConnectionString(settings) };
                connection.Open();
                using DbCommand command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM numbers";
                object? scalar = command.ExecuteScalar();
                int affected = command.ExecuteNonQuery();
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        sum += reader.GetInt32(0);
                        rows++;
                    }

                    Assert.False(reader.NextResult());
                }

                Assert.Equal(0, scalar);
                Assert.Equal(-1, affected);
                connection.Close();
            }

            int pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;
            if (pooled)
            {
                using CassieConnection poolIdentity = new() { ConnectionString = server.ConnectionString(settings) };
                CassieConnection.ClearPool(poolIdentity);
            }

            // Assert
            Assert.Equal(2 * RowCount, rows);
            Assert.Equal(2L * RowCount * (RowCount - 1) / 2, sum);
            Assert.Equal(0, pendingWaits);
        }

        [Fact]
        public void ShouldStayOnCallingThreadGivenSynchronousTransactionWhenCommittedAndRolledBack()
        {
            // Arrange
            using ScriptedServer server = new(ServeRowsAsync);
            using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            int before = SynchronousCall.PendingWaitsOnThisThread;

            // Act
            connection.Open();
            using (DbTransaction committed = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                committed.Commit();
            }

            using (DbTransaction rolledBack = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                rolledBack.Rollback();
            }

            using (DbTransaction abandoned = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                // Disposed without completing: the provider rolls back synchronously.
            }

            connection.Close();
            int pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;

            // Assert
            Assert.Equal(0, pendingWaits);
        }

        [Fact]
        public void ShouldWaitOnCallingThreadGivenFullPoolWhenSynchronousOpenWaitsForSlot()
        {
            // Arrange
            using ScriptedServer server = new(ServeRowsAsync);
            string connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1;Timeout=10");
            using CassieConnection holder = new() { ConnectionString = connectionString };
            holder.Open();
            using Timer releaseHolder = new(_ => holder.Close(), null, TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
            using CassieConnection waiter = new() { ConnectionString = connectionString };
            int before = SynchronousCall.PendingWaitsOnThisThread;

            // Act
            waiter.Open();
            int pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;
            ConnectionState state = waiter.State;
            waiter.Close();
            CassieConnection.ClearPool(waiter);

            // Assert
            Assert.Equal(ConnectionState.Open, state);
            Assert.Equal(1, server.Accepted);
            Assert.Equal(0, pendingWaits);
        }

        [Fact]
        public void ShouldReportTimeoutGivenSilentServerWhenSynchronousOpenExceedsTimeout()
        {
            // Arrange
            using System.Net.Sockets.TcpListener silent = new(System.Net.IPAddress.Loopback, 0);
            silent.Start();
            int port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=1;Pooling=false"
            };
            int before = SynchronousCall.PendingWaitsOnThisThread;

            // Act
            void Open()
            {
                connection.Open();
            }

            // Assert
            _ = Assert.Throws<TimeoutException>(Open);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, SynchronousCall.PendingWaitsOnThisThread - before);
        }

        [Fact]
        public async Task ShouldUseAsynchronousIoGivenSynchronousCallFinishedWhenAsyncCallFollowsOnSameConnection()
        {
            // Arrange
            using ScriptedServer server = new(ServeRowsAsync);
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM numbers";
            object? first = await Task.Run(() =>
            {
                connection.Open();
                return command.ExecuteScalar();
            }, TestContext.Current.CancellationToken);

            // Act
            int rows = 0;
            await using (DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    rows++;
                }
            }

            // Assert
            Assert.Equal(0, first);
            Assert.Equal(RowCount, rows);
        }

        private static Task ServeRowsAsync(ScriptedSession session)
        {
            return session.ServeQueriesAsync(async sql =>
            {
                if (!sql.StartsWith("SELECT", StringComparison.Ordinal))
                {
                    await session.WriteCommandCompleteAsync(sql.Split(' ')[0]);
                    return;
                }

                await session.WriteIntRowDescriptionAsync();
                for (int row = 0; row < RowCount; row++)
                {
                    await session.WriteTextRowAsync(row.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                await session.WriteCommandCompleteAsync($"SELECT {RowCount}");
            });
        }
    }
}
