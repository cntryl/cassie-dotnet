using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class CommandCancelContractTests
    {
        [Fact]
        public async Task ShouldAbortSessionGivenCancelEndpointUnreachableWhenCommandIsCanceled()
        {
            // Arrange
            using ScriptedServer server = new(session => session.StayVerySilentAsync());
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 1", CommandTimeout = 0 };
            Task<object?> running = Task.Run(() => command.ExecuteScalarAsync(CancellationToken.None), TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            server.StopAccepting();

            // Act
            Exception? cancelException = Record.Exception(command.Cancel);

            // Assert
            Assert.Null(cancelException);
            _ = await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Fact]
        public async Task ShouldAbortSessionGivenServerIgnoresCancelWhenGracePeriodElapses()
        {
            // Arrange
            using ScriptedServer server = new(session => session.StayVerySilentAsync());
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 1", CommandTimeout = 0 };
            Task<object?> running = Task.Run(() => command.ExecuteScalarAsync(CancellationToken.None), TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            // Act
            command.Cancel();

            // Assert
            _ = await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.Equal(1, server.CancelRequests);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
    }
}
