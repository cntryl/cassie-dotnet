using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    public sealed class LivePoolContractTests(LiveServerFixture server) : LiveTestBase(server)
    {
        private CassieConnectionStringBuilder PoolSettings(string applicationName, string options = "")
        {
            return new CassieConnectionStringBuilder(ConnectionString)
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
            string applicationName = UniqueName("cassie live pool reset");
            string pooled = PoolSettings(applicationName).ConnectionString;
            await using CassieConnection second = new() { ConnectionString = pooled };
            try
            {
                await using (CassieConnection first = new() { ConnectionString = pooled })
                {
                    await first.OpenAsync(TestContext.Current.CancellationToken);
                    using CassieCommand poison = CreateCommand("SET application_name = 'poisoned'", connection: first);
                    _ = await poison.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    await first.CloseAsync();
                }

                // Act
                await second.OpenAsync(TestContext.Current.CancellationToken);
                using CassieCommand show = CreateCommand("SHOW application_name", connection: second);
                object? name = await show.ExecuteScalarAsync(TestContext.Current.CancellationToken);

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
            string pooled = PoolSettings(UniqueName("cassie live pool wait")).ConnectionString;
            await using CassieConnection holder = new() { ConnectionString = pooled };
            await using CassieConnection waiter = new() { ConnectionString = pooled };
            await holder.OpenAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
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
            string applicationName = UniqueName("cassie live pool options");
            await using CassieConnection first = new() { ConnectionString = PoolSettings(applicationName, "-c application_name=options_pool_first").ConnectionString };
            await using CassieConnection second = new() { ConnectionString = PoolSettings(applicationName, "-c application_name=options_pool_second").ConnectionString };
            try
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
                using CassieCommand showFirst = CreateCommand("SELECT current_setting('application_name')", connection: first);
                object? firstName = await showFirst.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                await first.CloseAsync();

                // Act
                await second.OpenAsync(TestContext.Current.CancellationToken);
                using CassieCommand showSecond = CreateCommand("SELECT current_setting('application_name')", connection: second);
                object? secondName = await showSecond.ExecuteScalarAsync(TestContext.Current.CancellationToken);

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
}
