using Cntryl.Cassie.Connection;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    public sealed class DatabaseChangeContractTests
    {
        [Fact]
        public async Task ShouldReconnectToSelectedDatabaseGivenOpenConnectionWhenDatabaseChanges()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<(string InitialDatabase, string ChangedDatabase)> peer = Task.Run(async () =>
            {
                using TcpClient initialClient = await listener.AcceptTcpClientAsync();
                NetworkStream initialStream = initialClient.GetStream();
                string initialDatabase = await ReadDatabaseAsync(initialStream);
                await CompleteStartupAsync(initialStream);
                (byte initialCloseTag, _) = await ReadFrameAsync(initialStream);

                using TcpClient changedClient = await listener.AcceptTcpClientAsync();
                NetworkStream changedStream = changedClient.GetStream();
                string changedDatabase = await ReadDatabaseAsync(changedStream);
                await CompleteStartupAsync(changedStream);
                (byte changedCloseTag, _) = await ReadFrameAsync(changedStream);

                Assert.Equal((byte)'X', initialCloseTag);
                Assert.Equal((byte)'X', changedCloseTag);
                return (initialDatabase, changedDatabase);
            });
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=projection;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // Act
            await connection.ChangeDatabaseAsync("analytics", TestContext.Current.CancellationToken);
            string selectedDatabase = connection.Database;
            ConnectionState state = connection.State;
            await connection.CloseAsync();

            // Assert
            (string initialDatabase, string changedDatabase) = await peer;
            Assert.Equal("projection", initialDatabase);
            Assert.Equal("analytics", changedDatabase);
            Assert.Equal("analytics", selectedDatabase);
            Assert.Equal(ConnectionState.Open, state);
        }

        [Fact]
        public void ShouldRejectDatabaseChangeGivenClosedConnectionWhenDatabaseChanges()
        {
            // Arrange
            using CassieConnection connection = new()
            {
                ConnectionString = "Host=localhost;Database=projection;Username=app"
            };

            // Act
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("analytics"));

            // Assert
            Assert.Contains("must be open", exception.Message, StringComparison.Ordinal);
            Assert.Equal("projection", connection.Database);
        }

        [Fact]
        public async Task ShouldPreserveSessionGivenActiveTransactionWhenDatabaseChangeIsRejected()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeTransactionAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=projection;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);

            // Act
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("analytics"));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            string database = connection.Database;
            ConnectionState state = connection.State;

            // Assert
            Assert.Contains("transaction is active", exception.Message, StringComparison.Ordinal);
            Assert.Equal("projection", database);
            Assert.Equal(ConnectionState.Open, state);
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldPreserveSessionGivenActiveReaderWhenDatabaseChangeIsRejected()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeReaderAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=projection;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Act
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("analytics"));
            await reader.CloseAsync();
            string database = connection.Database;
            ConnectionState state = connection.State;

            // Assert
            Assert.Contains("reader is active", exception.Message, StringComparison.Ordinal);
            Assert.Equal("projection", database);
            Assert.Equal(ConnectionState.Open, state);
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldRollbackCanceledTransactionStartGivenBeginTransactionAsyncWhenCancellationIsRequested()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> beginReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> cancelReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServeCanceledBeginAsync(listener, beginReceived, cancelReceived), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=projection;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource cancellation = new();
            Task<DbTransaction> begin = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token).AsTask();
            _ = await beginReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

            // Act
            await cancellation.CancelAsync();
            _ = await cancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(ConnectionState.Open, connection.State);
            await using DbCommand followup = connection.CreateCommand();
            followup.CommandText = "SELECT 1";
            Assert.Equal(1, await followup.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldPreserveConnectionGivenTransactionStartTokenIsCanceledWhenBeginTransactionAsyncRuns()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeScalarQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=projection;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            // Act
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token).AsTask());

            // Assert
            Assert.Equal(ConnectionState.Open, connection.State);
            await using DbCommand query = connection.CreateCommand();
            query.CommandText = "SELECT 1";
            Assert.Equal(1, await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            await connection.CloseAsync();
            await peer;
        }

        private static async Task<string> ReadDatabaseAsync(NetworkStream stream)
        {
            byte[] lengthBytes = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(lengthBytes);
            int messageLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            byte[] payload = new byte[messageLength - sizeof(int)];
            await stream.ReadExactlyAsync(payload);

            Dictionary<string, string> parameters = new(StringComparer.Ordinal);
            int offset = sizeof(int);
            while (offset < payload.Length && payload[offset] != 0)
            {
                string key = ReadCString(payload, ref offset);
                string value = ReadCString(payload, ref offset);
                parameters.Add(key, value);
            }

            return parameters["database"];
        }

        private static string ReadCString(byte[] payload, ref int offset)
        {
            int end = Array.IndexOf(payload, (byte)0, offset);
            if (end < 0)
            {
                throw new IOException("The startup packet contains an unterminated string.");
            }

            string value = Encoding.UTF8.GetString(payload, offset, end - offset);
            offset = end + 1;
            return value;
        }

        private static string ReadCString(byte[] payload)
        {
            int terminator = Array.IndexOf(payload, (byte)0);
            return Encoding.UTF8.GetString(payload, 0, terminator < 0 ? payload.Length : terminator);
        }

        private static async Task CompleteStartupAsync(NetworkStream stream)
        {
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task ServeTransactionAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            Assert.Equal("projection", await ReadDatabaseAsync(stream));
            await CompleteStartupAsync(stream);

            (byte beginTag, byte[] beginPayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', beginTag);
            Assert.Equal("BEGIN ISOLATION LEVEL READ COMMITTED", ReadCString(beginPayload));
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("BEGIN"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'T']);

            (byte rollbackTag, byte[] rollbackPayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', rollbackTag);
            Assert.Equal("ROLLBACK", ReadCString(rollbackPayload));
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("ROLLBACK"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte closeTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'X', closeTag);
        }

        private static async Task ServeCanceledBeginAsync(TcpListener listener, TaskCompletionSource<bool> beginReceived, TaskCompletionSource<bool> cancelReceived)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            Assert.Equal("projection", await ReadDatabaseAsync(stream));
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            byte[] backendKey = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(backendKey, 1234);
            BinaryPrimitives.WriteInt32BigEndian(backendKey.AsSpan(4), 5678);
            await WriteFrameAsync(stream, (byte)'K', backendKey);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte beginTag, byte[] beginPayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', beginTag);
            Assert.Equal("BEGIN ISOLATION LEVEL READ COMMITTED", ReadCString(beginPayload));
            _ = beginReceived.TrySetResult(true);

            using TcpClient cancelClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            byte[] cancel = new byte[16];
            await cancelClient.GetStream().ReadExactlyAsync(cancel);
            Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(cancel));
            Assert.Equal(80877102, BinaryPrimitives.ReadInt32BigEndian(cancel.AsSpan(4)));
            Assert.Equal(1234, BinaryPrimitives.ReadInt32BigEndian(cancel.AsSpan(8)));
            Assert.Equal(5678, BinaryPrimitives.ReadInt32BigEndian(cancel.AsSpan(12)));
            _ = cancelReceived.TrySetResult(true);
            byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("ERROR"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("57014"), 0, (byte)'M', .. Encoding.UTF8.GetBytes("canceling statement due to user request"), 0, 0];
            await WriteFrameAsync(stream, (byte)'E', error);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'E']);

            (byte rollbackTag, byte[] rollbackPayload) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'Q', rollbackTag);
            Assert.Equal("ROLLBACK", ReadCString(rollbackPayload));
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("ROLLBACK"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte queryTag, byte[] queryPayload) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'Q', queryTag);
            Assert.Equal("SELECT 1", ReadCString(queryPayload));
            using MemoryStream description = new();
            description.Write(Int16(1));
            description.Write(CStringBytes("one"));
            description.Write(Int32(0));
            description.Write(Int16(0));
            description.Write(Int32(23));
            description.Write(Int16(4));
            description.Write(Int32(-1));
            description.Write(Int16(0));
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            using MemoryStream row = new();
            row.Write(Int16(1));
            row.Write(Int32(1));
            row.WriteByte((byte)'1');
            await WriteFrameAsync(stream, (byte)'D', row.ToArray());
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("SELECT 1"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte closeTag, _) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'X', closeTag);
        }

        private static async Task ServeScalarQueryAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            Assert.Equal("projection", await ReadDatabaseAsync(stream));
            await CompleteStartupAsync(stream);

            (byte queryTag, byte[] queryPayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', queryTag);
            Assert.Equal("SELECT 1", ReadCString(queryPayload));
            using MemoryStream description = new();
            description.Write(Int16(1));
            description.Write(CStringBytes("one"));
            description.Write(Int32(0));
            description.Write(Int16(0));
            description.Write(Int32(23));
            description.Write(Int16(4));
            description.Write(Int32(-1));
            description.Write(Int16(0));
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            using MemoryStream row = new();
            row.Write(Int16(1));
            row.Write(Int32(1));
            row.WriteByte((byte)'1');
            await WriteFrameAsync(stream, (byte)'D', row.ToArray());
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("SELECT 1"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte closeTag, _) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'X', closeTag);
        }

        private static async Task ServeReaderAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            Assert.Equal("projection", await ReadDatabaseAsync(stream));
            await CompleteStartupAsync(stream);

            (byte queryTag, byte[] queryPayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', queryTag);
            Assert.Equal("SELECT 1", ReadCString(queryPayload));
            using MemoryStream description = new();
            description.Write(Int16(1));
            description.Write(CStringBytes("one"));
            description.Write(Int32(0));
            description.Write(Int16(0));
            description.Write(Int32(23));
            description.Write(Int16(4));
            description.Write(Int32(-1));
            description.Write(Int16(0));
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("SELECT 1"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte closeTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'X', closeTag);
        }

        private static async Task<(byte Tag, byte[] Payload)> ReadFrameAsync(NetworkStream stream)
        {
            byte[] header = new byte[sizeof(byte) + sizeof(int)];
            await stream.ReadExactlyAsync(header);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(sizeof(byte)));
            byte[] payload = new byte[length - sizeof(int)];
            await stream.ReadExactlyAsync(payload);
            return (header[0], payload);
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + sizeof(byte) + sizeof(int)];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(sizeof(byte)), payload.Length + sizeof(int));
            payload.CopyTo(frame, sizeof(byte) + sizeof(int));
            await stream.WriteAsync(frame);
        }

        private static byte[] Int32(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] Int16(short value)
        {
            byte[] bytes = new byte[sizeof(short)];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] CStringBytes(string value)
        {
            return [.. Encoding.UTF8.GetBytes(value), 0];
        }
    }
}
