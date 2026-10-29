using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class PreparedCommandLifecycleTests
    {
        [Fact]
        public async Task ShouldReturnPendingDisposalGivenServerWaitsForCloseWhenPreparedCommandIsDisposedAsync()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> closeReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServePreparedCommandAsync(listener, closeReceived, releaseClose), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.PrepareAsync(TestContext.Current.CancellationToken);

            // Act
            Task<ValueTask> disposeCall = Task.Run(command.DisposeAsync);
            _ = await closeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            ValueTask disposal;
            try
            {
                disposal = await disposeCall.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }
            catch
            {
                _ = releaseClose.TrySetResult(true);
                _ = await disposeCall.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                await connection.CloseAsync();
                await peer;
                throw;
            }

            _ = releaseClose.TrySetResult(true);
            bool disposalCompletedBeforeServerResponse = disposal.IsCompleted;
            await disposal;
            await connection.CloseAsync();

            // Assert
            Assert.False(disposalCompletedBeforeServerResponse);
            await peer;
        }

        private static async Task ServePreparedCommandAsync(TcpListener listener, TaskCompletionSource<bool> closeReceived, TaskCompletionSource<bool> releaseClose)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte parseTag, byte[] parsePayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'P', parseTag);
            int parseOffset = 0;
            string statementName = ReadCString(parsePayload, ref parseOffset);
            Assert.Equal("SELECT 1", ReadCString(parsePayload, ref parseOffset));
            Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(parsePayload.AsSpan(parseOffset)));

            (byte describeTag, byte[] describePayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'D', describeTag);
            Assert.Equal((byte)'S', describePayload[0]);
            int describeOffset = 1;
            Assert.Equal(statementName, ReadCString(describePayload, ref describeOffset));
            (byte syncTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'S', syncTag);
            await WriteFrameAsync(stream, (byte)'1', []);
            await WriteFrameAsync(stream, (byte)'t', [0, 0]);
            await WriteFrameAsync(stream, (byte)'n', []);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte closeTag, byte[] closePayload) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'C', closeTag);
            Assert.Equal((byte)'S', closePayload[0]);
            int closeOffset = 1;
            Assert.Equal(statementName, ReadCString(closePayload, ref closeOffset));
            (byte closeSyncTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'S', closeSyncTag);
            _ = closeReceived.TrySetResult(true);
            _ = await releaseClose.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WriteFrameAsync(stream, (byte)'3', []);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            (byte terminateTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'X', terminateTag);
        }

        private static async Task ReadStartupAsync(NetworkStream stream)
        {
            byte[] lengthBytes = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(lengthBytes);
            int messageLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            await stream.ReadExactlyAsync(new byte[messageLength - sizeof(int)]);
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

        private static byte[] Int32(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }
    }
}
