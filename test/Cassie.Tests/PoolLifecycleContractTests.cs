using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class PoolLifecycleContractTests
    {
        [Fact]
        public async Task ShouldOpenFreshSessionGivenServerClosedIdleSocketWhenPooledConnectionOpens()
        {
            // Arrange
            CassieConnection.ClearAllPools();
            int sessions = 0;
            TaskCompletionSource firstSessionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ScriptedServer server = new(async session =>
            {
                bool first = Interlocked.Increment(ref sessions) == 1;
                if (!first)
                {
                    await session.ServeQueriesAsync(sql => session.WriteCommandCompleteAsync("SELECT"));
                    return;
                }

                _ = await session.ReadFrameAsync();
                await session.WriteCommandCompleteAsync("DISCARD ALL");
                await session.WriteReadyAsync();
                session.Close();
                _ = firstSessionClosed.TrySetResult();
            });
            string connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=2");
            await using (CassieConnection first = new() { ConnectionString = connectionString })
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
            }

            await firstSessionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            // Act
            await using CassieConnection second = new() { ConnectionString = connectionString };
            await second.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = second, CommandText = "SELECT 1" };
            int affected = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, server.Accepted);
            Assert.Equal(-1, affected);
            CassieConnection.ClearAllPools();
        }

        [Fact]
        public async Task ShouldDiscardIdleSessionGivenIdleLifetimeElapsedWhenPooledConnectionOpens()
        {
            // Arrange
            CassieConnection.ClearAllPools();
            using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => Task.CompletedTask));
            string connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=2;Connection Idle Lifetime=1");
            await using (CassieConnection first = new() { ConnectionString = connectionString })
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
            }

            await Task.Delay(1300, TestContext.Current.CancellationToken);

            // Act
            await using CassieConnection second = new() { ConnectionString = connectionString };
            await second.OpenAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, server.Accepted);
            CassieConnection.ClearAllPools();
        }

        [Fact]
        public async Task ShouldReuseIdleSessionGivenIdleLifetimeNotElapsedWhenPooledConnectionOpens()
        {
            // Arrange
            CassieConnection.ClearAllPools();
            using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => Task.CompletedTask));
            string connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=2;Connection Idle Lifetime=60");
            await using (CassieConnection first = new() { ConnectionString = connectionString })
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
            }

            // Act
            await using CassieConnection second = new() { ConnectionString = connectionString };
            await second.OpenAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, server.Accepted);
            CassieConnection.ClearAllPools();
        }

        [Fact]
        public async Task ShouldOpenInReplacementPoolGivenPoolClearedWhenOpenerWaits()
        {
            // Arrange
            CassieConnection.ClearAllPools();
            using ScriptedServer server = new(session => session.ServeQueriesAsync(_ => Task.CompletedTask));
            string connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1");
            await using CassieConnection holder = new() { ConnectionString = connectionString };
            await holder.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieConnection waiter = new() { ConnectionString = connectionString };
            Task waiting = waiter.OpenAsync(TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            // Act
            CassieConnection.ClearAllPools();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await holder.CloseAsync();

            // Assert
            Assert.Equal(ConnectionState.Open, waiter.State);
            Assert.Equal(2, server.Accepted);
            CassieConnection.ClearAllPools();
        }
    }
}
