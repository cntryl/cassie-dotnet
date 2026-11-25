using System.Data;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

// ClearAllPools changes process-wide state, including pools owned by other test classes.
[CollectionDefinition(nameof(PoolLifecycleContractTests), DisableParallelization = true)]
[Collection(nameof(PoolLifecycleContractTests))]
public sealed class PoolLifecycleContractTests
{
    [Fact]
    public async Task ShouldOpenFreshSessionGivenServerClosedIdleSocketWhenPooledConnectionOpens()
    {
        // Arrange
        CassieConnection.ClearAllPools();
        var sessions = 0;
        TaskCompletionSource firstSessionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            var first = Interlocked.Increment(ref sessions) == 1;

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
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=2");
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
        var affected = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, server.Accepted);
        Assert.Equal(-1, affected);
        CassieConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("Connection Idle Lifetime=1")]
    [InlineData("Connection Lifetime=1")]
    public async Task ShouldDiscardIdleSessionGivenLifetimeElapsedWhenConnectionClockAdvances(string lifetime)
    {
        // Arrange
        CassieConnection.ClearAllPools();
        using var clock = new DeadlineClock();
        using ScriptedServer server = new(session => session.ServeQueriesAsync(session.WriteCommandCompleteAsync));
        var connectionString = server.ConnectionString($"Pooling=true;Maximum Pool Size=2;{lifetime}");
        await using var source = new CassieDataSource(connectionString, clock);
        await using (var first = await source.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await first.CloseAsync();
        }

        clock.Advance(TimeSpan.FromSeconds(2));

        // Act
        await using var second = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, server.Accepted);
        CassieConnection.ClearPool(second);
    }

    [Fact]
    public async Task ShouldKeepSeparatePoolsGivenMatchingSettingsWhenClockInstancesDiffer()
    {
        // Arrange
        CassieConnection.ClearAllPools();
        using var firstClock = new DeadlineClock();
        using var secondClock = new DeadlineClock();
        using ScriptedServer server = new(session => session.ServeQueriesAsync(session.WriteCommandCompleteAsync));
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1");
        await using var firstSource = new CassieDataSource(connectionString, firstClock);
        await using var secondSource = new CassieDataSource(connectionString, secondClock);
        await using var first = await firstSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await first.CloseAsync();

        // Act
        await using var second = await secondSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await second.CloseAsync();
        await using var reopened = await firstSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, server.Accepted);
        CassieConnection.ClearPool(first);
        CassieConnection.ClearPool(second);
    }

    [Fact]
    public async Task ShouldReuseIdleSessionGivenIdleLifetimeNotElapsedWhenPooledConnectionOpens()
    {
        // Arrange
        CassieConnection.ClearAllPools();
        using ScriptedServer server = new(session => session.ServeQueriesAsync(session.WriteCommandCompleteAsync));
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=2;Connection Idle Lifetime=60");
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
    public async Task ShouldOpenFreshSessionGivenAllPoolsClearedWhenIdleLifetimeNotElapsed()
    {
        // Arrange
        using ScriptedServer server = new(session => session.ServeQueriesAsync(session.WriteCommandCompleteAsync));
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=2;Connection Idle Lifetime=60");
        await using (CassieConnection first = new() { ConnectionString = connectionString })
        {
            await first.OpenAsync(TestContext.Current.CancellationToken);
        }

        // Act
        CassieConnection.ClearAllPools();
        await using CassieConnection second = new() { ConnectionString = connectionString };
        await second.OpenAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, server.Accepted);
        CassieConnection.ClearAllPools();
    }

    [Fact]
    public async Task ShouldOpenInReplacementPoolGivenPoolClearedWhenOpenerWaits()
    {
        // Arrange
        CassieConnection.ClearAllPools();
        using ScriptedServer server = new(session => session.ServeQueriesAsync(session.WriteCommandCompleteAsync));
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1");
        await using CassieConnection holder = new() { ConnectionString = connectionString };
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieConnection waiter = new() { ConnectionString = connectionString };
        var waiting = waiter.OpenAsync(TestContext.Current.CancellationToken);
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
