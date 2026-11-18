using System.Data;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Live;

[Collection(LiveEndpoint.CollectionName)]
public sealed class LivePoolContractTests : IAsyncLifetime
{
    private readonly LiveSession _live = new();

    public ValueTask InitializeAsync()
    {
        return _live.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        return _live.DisposeAsync();
    }

    private CassieConnectionStringBuilder PoolSettings(string applicationName, string options = "")
    {
        return new CassieConnectionStringBuilder(_live.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
            ApplicationName = applicationName,
            Options = options
        };
    }

    [Fact]
    public async Task ShouldRestoreSessionStateGivenPooledSessionWhenConnectionIsReopened()
    {
        // Arrange
        var applicationName = LiveSession.UniqueName("cassie live pool reset");
        var pooled = PoolSettings(applicationName).ConnectionString;
        await using CassieConnection second = new() { ConnectionString = pooled };
        try
        {
            await using (CassieConnection first = new() { ConnectionString = pooled })
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
                using var poison = _live.CreateCommand(first);
                poison.CommandText = "SET application_name = 'poisoned'";
                _ = await poison.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                await first.CloseAsync();
            }

            // Act
            await second.OpenAsync(TestContext.Current.CancellationToken);
            using var show = _live.CreateCommand(second);
            show.CommandText = "SHOW application_name";
            var name = await show.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(applicationName, name);
        }
        finally
        {
            await second.CloseAsync();
            CassieConnection.ClearPool(second);
        }
    }

    [Fact]
    public async Task ShouldHonorCancellationGivenExhaustedPoolWhenOpenerWaits()
    {
        // Arrange
        var pooled = PoolSettings(LiveSession.UniqueName("cassie live pool wait")).ConnectionString;
        await using CassieConnection holder = new() { ConnectionString = pooled };
        await using CassieConnection waiter = new() { ConnectionString = pooled };
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        waitCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        try
        {
            // Act
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.OpenAsync(waitCancellation.Token));

            // Assert
            Assert.Equal(ConnectionState.Closed, waiter.State);
            CassieConnection.ClearPool(holder);
            await holder.CloseAsync();
            await waiter.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Open, waiter.State);
        }
        finally
        {
            await waiter.CloseAsync();
            CassieConnection.ClearPool(waiter);
        }
    }

    [Fact]
    public async Task ShouldSeparatePhysicalSessionsGivenDifferentStartupOptionsWhenPoolsAreRented()
    {
        // Arrange
        var applicationName = LiveSession.UniqueName("cassie live pool options");
        await using CassieConnection first = new()
        {
            ConnectionString = PoolSettings(applicationName, "-c application_name=options_pool_first").ConnectionString
        };
        await using CassieConnection second = new()
        {
            ConnectionString = PoolSettings(applicationName, "-c application_name=options_pool_second").ConnectionString
        };
        try
        {
            await first.OpenAsync(TestContext.Current.CancellationToken);
            using var showFirst = _live.CreateCommand(first);
            showFirst.CommandText = "SELECT current_setting('application_name')";
            var firstName = await showFirst.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            await first.CloseAsync();

            // Act
            await second.OpenAsync(TestContext.Current.CancellationToken);
            using var showSecond = _live.CreateCommand(second);
            showSecond.CommandText = "SELECT current_setting('application_name')";
            var secondName = await showSecond.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("options_pool_first", firstName);
            Assert.Equal("options_pool_second", secondName);
        }
        finally
        {
            await first.CloseAsync();
            await second.CloseAsync();
            CassieConnection.ClearPool(first);
            CassieConnection.ClearPool(second);
        }
    }
}
