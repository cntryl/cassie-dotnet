using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands
{
    public sealed class CommandTimeoutValidationTests
    {
        [Theory]
        [InlineData(-1)]
        [InlineData(2_147_484)]
        [InlineData(int.MaxValue)]
        public void ShouldRejectInvalidTimeoutGivenUnsupportedSecondsWhenCommandTimeoutIsAssigned(int seconds)
        {
            // Arrange
            using CassieCommand command = new();

            // Act
            void AssignTimeout()
            {
                command.CommandTimeout = seconds;
            }

            // Assert
            _ = Assert.Throws<ArgumentOutOfRangeException>(AssignTimeout);
            Assert.Equal(30, command.CommandTimeout);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(2_147_483)]
        public void ShouldAcceptBuilderRangeGivenSupportedSecondsWhenCommandTimeoutIsAssigned(int seconds)
        {
            // Arrange
            using CassieCommand command = new();
            CassieConnectionStringBuilder builder = new() { CommandTimeout = seconds };

            // Act
            command.CommandTimeout = seconds;

            // Assert
            Assert.Equal(builder.CommandTimeout, command.CommandTimeout);
        }

        [Fact]
        public async Task ShouldPreserveSessionGivenInvalidTimeoutWhenCommandIsConfiguredBeforeExecution()
        {
            // Arrange
            int queries = 0;
            using ScriptedServer server = new(session => session.ServeQueriesAsync(async sql =>
            {
                _ = Interlocked.Increment(ref queries);
                await session.WriteIntRowDescriptionAsync();
                await session.WriteTextRowAsync("7");
                await session.WriteCommandCompleteAsync("SELECT 1");
            }));
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Guid identity = connection.SessionIdentity;
            using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 7" };

            // Act
            void AssignTimeout()
            {
                command.CommandTimeout = int.MaxValue;
            }

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(AssignTimeout);
            int queriesBeforeExecution = Volatile.Read(ref queries);
            object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("value", error.ParamName);
            Assert.Equal(0, queriesBeforeExecution);
            Assert.Equal(1, Volatile.Read(ref queries));
            Assert.Equal(7, value);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(identity, connection.SessionIdentity);
        }
    }
}
