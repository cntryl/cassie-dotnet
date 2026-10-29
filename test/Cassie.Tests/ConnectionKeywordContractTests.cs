using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class ConnectionKeywordContractTests
    {
        [Fact]
        public async Task ShouldRejectUnknownKeywordGivenTypoWhenConnectionOpens()
        {
            // Arrange
            await using CassieConnection connection = new() { ConnectionString = "Server=db.example;Username=app;SSL Mode=Disable" };

            // Act
            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => connection.OpenAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("server", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        }

        [Fact]
        public void ShouldAcceptKeywordsCaseInsensitivelyGivenLowerCaseNamesWhenValidated()
        {
            // Arrange
            CassieConnectionStringBuilder builder = new("host=db.example;username=app;ssl mode=Disable;fetch size=10");

            // Act
            Exception? exception = Record.Exception(builder.ValidateKeywords);

            // Assert
            Assert.Null(exception);
            Assert.Equal(10, builder.FetchSize);
        }

        [Fact]
        public void ShouldUseDefaultsGivenNoPerformanceKeywordsWhenBuilderIsRead()
        {
            // Arrange
            CassieConnectionStringBuilder builder = new("Username=app");

            // Act
            int[] values = [builder.FetchSize, builder.CommandTimeout, builder.ConnectionIdleLifetime, builder.ConnectionLifetime];

            // Assert
            Assert.Equal([128, 30, 300, 0], values);
        }

        [Fact]
        public void ShouldRejectNonPositiveFetchSizeGivenZeroWhenBuilderIsRead()
        {
            // Arrange
            CassieConnectionStringBuilder builder = new("Fetch Size=0");

            // Act
            ArgumentException exception = Assert.Throws<ArgumentException>(() => builder.FetchSize);

            // Assert
            Assert.Contains("Fetch Size", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldApplyConnectionCommandTimeoutGivenKeywordWhenCommandIsCreated()
        {
            // Arrange
            using CassieConnection connection = new() { ConnectionString = "Username=app;Command Timeout=7" };

            // Act
            using DbCommand command = connection.CreateCommand();

            // Assert
            Assert.Equal(7, command.CommandTimeout);
        }

        [Fact]
        public async Task ShouldRequestConfiguredRowsGivenFetchSizeWhenParameterizedQueryExecutes()
        {
            // Arrange
            int maxRows = -1;
            using ScriptedServer server = new(async session =>
            {
                while (true)
                {
                    (byte tag, byte[] payload) = await session.ReadFrameAsync();
                    if (tag == (byte)'E')
                    {
                        maxRows = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1));
                    }
                    else if (tag == (byte)'H')
                    {
                        await session.WriteAsync((byte)'1', []);
                        await session.WriteAsync((byte)'2', []);
                        await session.WriteIntRowDescriptionAsync();
                        await session.WriteCommandCompleteAsync("SELECT 0");
                    }
                    else if (tag == (byte)'S')
                    {
                        await session.WriteReadyAsync();
                    }
                    else if (tag == (byte)'X')
                    {
                        return;
                    }
                }
            });
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false;Fetch Size=2") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = connection, CommandText = "SELECT @id" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "id", Value = 1 });

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            bool hasRow = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.False(hasRow);
            Assert.Equal(2, maxRows);
        }
    }
}
