using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    public sealed class ParameterEncodingRecoveryContractTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldKeepSessionOpenGivenTooManyParametersWhenPreparationIsRequested(bool schemaOnly)
        {
            // Arrange
            using ScriptedServer server = new(session => session.StayVerySilentAsync());
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            using CassieCommand command = new() { Connection = connection, CommandText = "too_many", CommandType = CommandType.StoredProcedure };
            await command.PrepareAsync(TestContext.Current.CancellationToken);
            for (int index = 0; index <= short.MaxValue; index++)
            {
                _ = command.Parameters.Add(new CassieParameter { ParameterName = index.ToString(CultureInfo.InvariantCulture), Value = index });
            }

            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // Act
            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                if (schemaOnly)
                {
                    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, TestContext.Current.CancellationToken);
                }
                else
                {
                    await command.PrepareAsync(TestContext.Current.CancellationToken);
                }
            });

            // Assert
            _ = Assert.IsType<NotSupportedException>(failure);
            Assert.Equal(ConnectionState.Open, connection.State);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ShouldKeepSessionReusableGivenLocalEncodingFailureWhenCommandRuns(bool transactionActive, bool unsupportedFormat)
        {
            // Arrange
            ConcurrentQueue<byte> receivedTags = new();
            using ScriptedServer server = new(async session =>
            {
                bool inTransaction = false;
                while (true)
                {
                    (byte tag, byte[] payload) = await session.ReadFrameAsync();
                    if (tag == (byte)'X')
                    {
                        return;
                    }

                    receivedTags.Enqueue(tag);
                    if (tag != (byte)'Q')
                    {
                        continue;
                    }

                    string sql = Encoding.UTF8.GetString(payload, 0, payload.Length - 1);
                    if (sql.StartsWith("BEGIN", StringComparison.Ordinal))
                    {
                        inTransaction = true;
                        await session.WriteCommandCompleteAsync("BEGIN");
                    }
                    else if (sql == "ROLLBACK")
                    {
                        inTransaction = false;
                        await session.WriteCommandCompleteAsync("ROLLBACK");
                    }
                    else
                    {
                        await session.WriteIntRowDescriptionAsync();
                        await session.WriteTextRowAsync("7");
                        await session.WriteCommandCompleteAsync("SELECT 1");
                    }

                    await session.WriteReadyAsync(inTransaction ? 'T' : 'I');
                }
            });
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Guid sessionIdentity = connection.SessionIdentity;
            await using DbTransaction? transaction = transactionActive
                ? await connection.BeginTransactionAsync(TestContext.Current.CancellationToken)
                : null;
            using CassieCommand invalid = new()
            {
                Connection = connection,
                Transaction = transaction,
                CommandText = "SELECT set_config('DateStyle', 'SQL, DMY', false), @value"
            };
            _ = invalid.Parameters.Add(new CassieParameter
            {
                ParameterName = "value",
                Value = unsupportedFormat ? "unsupported" : TimeSpan.FromHours(25),
                PostgresTypeOid = unsupportedFormat ? 999999 : 1083,
                ParameterFormat = CassieParameterFormat.Binary
            });

            // Act
            Exception? failure = await Record.ExceptionAsync(() => invalid.ExecuteScalarAsync(TestContext.Current.CancellationToken));

            // Assert
            if (unsupportedFormat)
            {
                _ = Assert.IsType<NotSupportedException>(failure);
            }
            else
            {
                _ = Assert.IsType<ArgumentOutOfRangeException>(failure);
            }

            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(sessionIdentity, connection.SessionIdentity);
            Assert.Equal(transactionActive ? "Q"u8.ToArray() : [], receivedTags.ToArray());
            using CassieCommand valid = new() { Connection = connection, Transaction = transaction, CommandText = "SELECT 7" };
            Assert.Equal(7, await valid.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(transactionActive ? "QQ"u8.ToArray() : "Q"u8.ToArray(), receivedTags.ToArray());
            Assert.Equal(1, server.Accepted);
            if (transaction is not null)
            {
                Assert.Same(connection, transaction.Connection);
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task ShouldAbortSessionGivenPeerResetDuringBatchWriteWhenParameterizedCommandRuns()
        {
            // Arrange
            TaskCompletionSource prefixReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ScriptedServer server = new(async session =>
            {
                await session.Stream.ReadExactlyAsync(new byte[8], TestContext.Current.CancellationToken);
                session.Stream.Socket.LingerState = new LingerOption(true, 0);
                session.Close();
                prefixReceived.SetResult();
            });
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = new() { Connection = connection, CommandText = "SELECT @value", CommandTimeout = 5 };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = new string('x', 8 * 1024 * 1024) });

            // Act
            Exception? failure = await Record.ExceptionAsync(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            // Assert
            Assert.True(failure is IOException or SocketException, failure?.ToString());
            await prefixReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
    }
}
