using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class PortalStreamingContractTests
    {
        [Fact]
        public async Task ShouldResumeBoundedPortalPagesGivenParameterizedQueryWhenReaderReadsRows()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<PortalTrace> peer = Task.Run(async () => await ServePagedQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value::int AS value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7 });

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            List<int> values = [];
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                values.Add(reader.GetInt32(0));
            }

            bool hasNextResult = await reader.NextResultAsync(TestContext.Current.CancellationToken);
            await connection.CloseAsync();
            PortalTrace trace = await peer.WaitAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal([1, 2, 3], values);
            Assert.False(hasNextResult);
            Assert.Equal(128, trace.InitialFetchRows);
            Assert.Equal((byte)'H', trace.InitialBoundaryTag);
            Assert.True(trace.ResumedPortal);
            Assert.Equal((byte)'S', trace.FinalBoundaryTag);
        }

        [Fact]
        public async Task ShouldDrainSuspendedPortalGivenReaderClosesEarlyWhenConnectionRemainsOpen()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<PortalTrace> peer = Task.Run(async () => await ServePagedQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value::int AS value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7 });
            DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // Act
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, reader.GetInt32(0));
            await reader.CloseAsync();
            ConnectionState state = connection.State;
            await connection.CloseAsync();
            PortalTrace trace = await peer.WaitAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(ConnectionState.Open, state);
            Assert.True(trace.ResumedPortal);
            Assert.Equal((byte)'S', trace.FinalBoundaryTag);
        }

        [Fact]
        public async Task ShouldSyncAndRecoverGivenCanceledPortalResumeWhenReaderReads()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> resumeReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServeCancelablePagedQueryAsync(listener, resumeReceived), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value::int AS value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7 });
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, reader.GetInt32(0));
            using CancellationTokenSource cancellation = new();

            // Act
            Task<bool> pendingRead = reader.ReadAsync(cancellation.Token);
            _ = await resumeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            ConnectionState state = connection.State;
            using DbCommand followup = connection.CreateCommand();
            followup.CommandText = "SELECT 2";
            object? followupValue = await followup.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            await connection.CloseAsync();
            await peer;

            // Assert
            Assert.Equal(ConnectionState.Open, state);
            Assert.Equal(2, followupValue);
        }

        private static async Task<PortalTrace> ServePagedQueryAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte parseTag, _) = await ReadFrameAsync(stream);
            (byte bindTag, _) = await ReadFrameAsync(stream);
            (byte describeTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'P', parseTag);
            Assert.Equal((byte)'B', bindTag);
            Assert.Equal((byte)'D', describeTag);
            (byte executeTag, byte[] executePayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'E', executeTag);
            int executeOffset = 0;
            _ = ReadCString(executePayload, ref executeOffset);
            int initialFetchRows = BinaryPrimitives.ReadInt32BigEndian(executePayload.AsSpan(executeOffset));
            (byte initialBoundaryTag, _) = await ReadFrameAsync(stream);

            await WriteFrameAsync(stream, (byte)'1', []);
            await WriteFrameAsync(stream, (byte)'2', []);
            await WriteFrameAsync(stream, (byte)'T', RowDescription());
            await WriteFrameAsync(stream, (byte)'D', DataRow(1));
            await WriteFrameAsync(stream, (byte)'D', DataRow(2));

            bool resumedPortal = false;
            if (initialBoundaryTag == (byte)'H')
            {
                await WriteFrameAsync(stream, (byte)'s', []);
                using CancellationTokenSource continuationTimeout = new(TimeSpan.FromSeconds(2));
                try
                {
                    (byte continuationTag, byte[] continuationPayload) = await ReadFrameAsync(stream, continuationTimeout.Token);
                    if (continuationTag == (byte)'E')
                    {
                        int continuationOffset = 0;
                        _ = ReadCString(continuationPayload, ref continuationOffset);
                        _ = BinaryPrimitives.ReadInt32BigEndian(continuationPayload.AsSpan(continuationOffset));
                        (byte flushTag, _) = await ReadFrameAsync(stream, continuationTimeout.Token);
                        resumedPortal = flushTag == (byte)'H';
                    }
                }
                catch (OperationCanceledException) when (continuationTimeout.IsCancellationRequested)
                {
                }
            }

            await WriteFrameAsync(stream, (byte)'D', DataRow(3));
            await WriteFrameAsync(stream, (byte)'C', CString("SELECT 3"));
            byte finalBoundaryTag = initialBoundaryTag == (byte)'S'
                ? initialBoundaryTag
                : (await ReadFrameAsync(stream)).Tag;
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            (byte terminateTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'X', terminateTag);
            return new PortalTrace(initialFetchRows, initialBoundaryTag, resumedPortal, finalBoundaryTag);
        }

        private static async Task ServeCancelablePagedQueryAsync(TcpListener listener, TaskCompletionSource<bool> resumeReceived)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            byte[] backendKey = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(backendKey, 1234);
            BinaryPrimitives.WriteInt32BigEndian(backendKey.AsSpan(sizeof(int)), 5678);
            await WriteFrameAsync(stream, (byte)'K', backendKey);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            _ = await ReadFrameAsync(stream);
            _ = await ReadFrameAsync(stream);
            _ = await ReadFrameAsync(stream);
            (byte executeTag, _) = await ReadFrameAsync(stream);
            (byte flushTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'E', executeTag);
            Assert.Equal((byte)'H', flushTag);

            await WriteFrameAsync(stream, (byte)'1', []);
            await WriteFrameAsync(stream, (byte)'2', []);
            await WriteFrameAsync(stream, (byte)'T', RowDescription());
            await WriteFrameAsync(stream, (byte)'D', DataRow(1));
            await WriteFrameAsync(stream, (byte)'s', []);
            (byte continuationTag, _) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            (byte continuationFlushTag, _) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'E', continuationTag);
            Assert.Equal((byte)'H', continuationFlushTag);
            _ = resumeReceived.TrySetResult(true);

            using TcpClient cancelClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            byte[] cancel = new byte[16];
            await cancelClient.GetStream().ReadExactlyAsync(cancel);
            Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(cancel));
            Assert.Equal(80877102, BinaryPrimitives.ReadInt32BigEndian(cancel.AsSpan(sizeof(int))));
            Assert.Equal(1234, BinaryPrimitives.ReadInt32BigEndian(cancel.AsSpan(sizeof(int) * 2)));
            Assert.Equal(5678, BinaryPrimitives.ReadInt32BigEndian(cancel.AsSpan(sizeof(int) * 3)));
            byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("ERROR"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("57014"), 0, (byte)'M', .. Encoding.UTF8.GetBytes("canceling statement due to user request"), 0, 0];
            await WriteFrameAsync(stream, (byte)'E', error);
            (byte syncTag, _) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'S', syncTag);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte followupTag, byte[] followup) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'Q', followupTag);
            Assert.Equal("SELECT 2\0", Encoding.UTF8.GetString(followup));
            await WriteFrameAsync(stream, (byte)'T', RowDescription());
            await WriteFrameAsync(stream, (byte)'D', DataRow(2));
            await WriteFrameAsync(stream, (byte)'C', CString("SELECT 1"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            (byte terminateTag, _) = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)'X', terminateTag);
        }

        private static async Task ReadStartupAsync(NetworkStream stream)
        {
            byte[] lengthBytes = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(lengthBytes);
            int messageLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            await stream.ReadExactlyAsync(new byte[messageLength - sizeof(int)]);
        }

        private static async Task<(byte Tag, byte[] Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken = default)
        {
            byte[] header = new byte[sizeof(byte) + sizeof(int)];
            await stream.ReadExactlyAsync(header, cancellationToken);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(sizeof(byte)));
            byte[] payload = new byte[length - sizeof(int)];
            await stream.ReadExactlyAsync(payload, cancellationToken);
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

        private static byte[] RowDescription()
        {
            using MemoryStream payload = new();
            WriteInt16(payload, 1);
            WriteCString(payload, "value");
            WriteInt32(payload, 0);
            WriteInt16(payload, 0);
            WriteInt32(payload, 23);
            WriteInt16(payload, 4);
            WriteInt32(payload, -1);
            WriteInt16(payload, 0);
            return payload.ToArray();
        }

        private static byte[] DataRow(int value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using MemoryStream payload = new();
            WriteInt16(payload, 1);
            WriteInt32(payload, text.Length);
            payload.Write(text);
            return payload.ToArray();
        }

        private static byte[] Int32(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] CString(string value)
        {
            return [.. Encoding.UTF8.GetBytes(value), 0];
        }

        private static void WriteCString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.WriteByte(0);
        }

        private static void WriteInt16(Stream stream, short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static string ReadCString(byte[] payload, ref int offset)
        {
            int end = Array.IndexOf(payload, (byte)0, offset);
            if (end < 0)
            {
                throw new IOException("The pgwire frame contains an unterminated string.");
            }

            string value = Encoding.UTF8.GetString(payload, offset, end - offset);
            offset = end + 1;
            return value;
        }

        private sealed record PortalTrace(int InitialFetchRows, byte InitialBoundaryTag, bool ResumedPortal, byte FinalBoundaryTag);
    }
}
