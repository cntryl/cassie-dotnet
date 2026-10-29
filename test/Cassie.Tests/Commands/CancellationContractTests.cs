using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands
{
    public sealed class CancellationContractTests
    {
        [Fact]
        public async Task ShouldCancelActiveQueryGivenReaderWaitsForRowWhenCommandIsCancelled()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeCancelableQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT pg_sleep(60)";
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Act
            Task<bool> pendingRead = reader.ReadAsync(TestContext.Current.CancellationToken);
            command.Cancel();
            CassieException exception = await Assert.ThrowsAsync<CassieException>(() => pendingRead.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal("57014", exception.SqlState);
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldTimeoutActiveQueryGivenCommandTimeoutWhenReaderWaitsForRow()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeCancelableQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT pg_sleep(60)";
            command.CommandTimeout = 1;
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Act
            TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("CommandTimeout", exception.Message, StringComparison.Ordinal);
            await connection.CloseAsync();
            await peer;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ShouldCancelExecutionGivenServerWaitsForResultWhenAsyncScalarOrNonQueryRuns(bool executeScalar)
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> rowDescriptionSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServeCancelableCommandAsync(listener, rowDescriptionSent), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT 1";
            using CancellationTokenSource cancellation = new();
            Func<Task> execute = executeScalar
                ? () => command.ExecuteScalarAsync(cancellation.Token)
                : () => command.ExecuteNonQueryAsync(cancellation.Token);
            Task execution = Task.Run(execute, TestContext.Current.CancellationToken);
            _ = await rowDescriptionSent.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            ConnectionState state = connection.State;

            // Assert
            Assert.Equal(ConnectionState.Open, state);
            using DbCommand followup = connection.CreateCommand();
            followup.CommandText = "SELECT 2";
            Assert.Equal(2, await followup.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldCancelSingleResultDrainGivenServerWaitsForNextResultWhenNextResultRuns()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> rowDescriptionSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServeCancelableCommandAsync(listener, rowDescriptionSent), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, TestContext.Current.CancellationToken);
            using CancellationTokenSource cancellation = new();
            Task<bool> nextResult = reader.NextResultAsync(cancellation.Token);
            _ = await rowDescriptionSent.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextResult.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(ConnectionState.Open, connection.State);
            using DbCommand followup = connection.CreateCommand();
            followup.CommandText = "SELECT 2";
            Assert.Equal(2, await followup.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            await connection.CloseAsync();
            await peer;
        }

        private static async Task ServeCancelableQueryAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            int startupLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(startup, 0));
            await stream.ReadExactlyAsync(new byte[startupLength - startup.Length]);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            byte[] backendKey = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(backendKey, 1234);
            BinaryPrimitives.WriteInt32BigEndian(backendKey.AsSpan(4), 5678);
            await WriteFrameAsync(stream, (byte)'K', backendKey);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            _ = await ReadFrontendFrameAsync(stream);
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "pg_sleep");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 25);
            WriteInt16(description, -1);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());

            using TcpClient cancelClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            byte[] cancel = new byte[16];
            await cancelClient.GetStream().ReadExactlyAsync(cancel);
            Assert.Equal(16, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 0)));
            Assert.Equal(80877102, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 4)));
            Assert.Equal(1234, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 8)));
            Assert.Equal(5678, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 12)));
            byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("ERROR"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("57014"), 0, (byte)'M', .. Encoding.UTF8.GetBytes("canceling statement due to user request"), 0, 0];
            await WriteFrameAsync(stream, (byte)'E', error);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task ServeCancelableCommandAsync(TcpListener listener, TaskCompletionSource<bool> rowDescriptionSent)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            int startupLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(startup, 0));
            await stream.ReadExactlyAsync(new byte[startupLength - startup.Length]);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            byte[] backendKey = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(backendKey, 1234);
            BinaryPrimitives.WriteInt32BigEndian(backendKey.AsSpan(4), 5678);
            await WriteFrameAsync(stream, (byte)'K', backendKey);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            byte[] query = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'Q', query[0]);
            Assert.Equal("SELECT 1\0", Encoding.UTF8.GetString(query, 1, query.Length - 1));
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 23);
            WriteInt16(description, 4);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            _ = rowDescriptionSent.TrySetResult(true);

            try
            {
                using TcpClient cancelClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromMilliseconds(250));
                byte[] cancel = new byte[16];
                await cancelClient.GetStream().ReadExactlyAsync(cancel);
                Assert.Equal(16, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 0)));
                Assert.Equal(80877102, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 4)));
                Assert.Equal(1234, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 8)));
                Assert.Equal(5678, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(cancel, 12)));
                byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("ERROR"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("57014"), 0, (byte)'M', .. Encoding.UTF8.GetBytes("canceling statement due to user request"), 0, 0];
                await WriteFrameAsync(stream, (byte)'E', error);
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] followup = await ReadFrontendFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal((byte)'Q', followup[0]);
                Assert.Equal("SELECT 2\0", Encoding.UTF8.GetString(followup, 1, followup.Length - 1));
                using MemoryStream followupDescription = new();
                WriteInt16(followupDescription, 1);
                WriteCString(followupDescription, "value");
                followupDescription.Write(new byte[4]);
                WriteInt16(followupDescription, 0);
                WriteInt32(followupDescription, 23);
                WriteInt16(followupDescription, 4);
                WriteInt32(followupDescription, -1);
                WriteInt16(followupDescription, 0);
                await WriteFrameAsync(stream, (byte)'T', followupDescription.ToArray());
                using MemoryStream row = new();
                WriteInt16(row, 1);
                WriteInt32(row, 1);
                row.WriteByte((byte)'2');
                await WriteFrameAsync(stream, (byte)'D', row.ToArray());
                await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] terminate = await ReadFrontendFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal((byte)'X', terminate[0]);
            }
            catch (TimeoutException)
            {
                using MemoryStream row = new();
                WriteInt16(row, 1);
                WriteInt32(row, 1);
                row.WriteByte((byte)'1');
                await WriteFrameAsync(stream, (byte)'D', row.ToArray());
                await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] terminate = await ReadFrontendFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal((byte)'X', terminate[0]);
            }
        }

        private static async Task<byte[]> ReadFrontendFrameAsync(NetworkStream stream)
        {
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header);
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 1));
            byte[] payload = new byte[length - 4];
            await stream.ReadExactlyAsync(payload);
            return [header[0], .. payload];
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length + 4)).CopyTo(frame, 1);
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame);
        }

        private static void WriteInt16(Stream stream, short value)
        {
            stream.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value)));
        }

        private static void WriteInt32(Stream stream, int value)
        {
            stream.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value)));
        }

        private static void WriteCString(Stream stream, string value) { stream.Write(Encoding.UTF8.GetBytes(value)); stream.WriteByte(0); }
    }
}
