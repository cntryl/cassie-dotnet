using System.Data;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class ConnectionOpenContractTests
    {
        [Fact]
        public async Task ShouldAuthenticateGivenCleartextChallengeWhenConnectionOpens()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                byte[] startup = new byte[8];
                await stream.ReadExactlyAsync(startup);
                await ReadStartupPacketAsync(stream, startup);
                await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(3)));
                byte[] password = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'p', password[0]);
                await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(0));
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            }, TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };

            // Act
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(ConnectionState.Open, connection.State);
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldSendOptionsStartupParameterGivenConnectionStringWhenConnectionOpens()
        {
            // Arrange
            const string options = "-c TimeZone=UTC -c search_path=public";
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                Dictionary<string, string> startup = await ReadStartupParametersAsync(stream);
                await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] terminate = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'X', terminate[0]);
                Assert.Equal(options, startup["options"]);
            }, TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;SSL Mode=Disable;Timeout=3;Pooling=false;Options={options}"
            };

            // Act
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await connection.CloseAsync();

            // Assert
            await peer;
        }

        [Fact]
        public async Task ShouldPreserveSqlStateGivenRejectedPasswordWhenConnectionOpens()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                byte[] startup = new byte[8];
                await stream.ReadExactlyAsync(startup);
                await ReadStartupPacketAsync(stream, startup);
                await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(3)));
                _ = await ReadFrontendFrameAsync(stream);
                byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("FATAL"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("28P01"), 0, (byte)'M', .. Encoding.UTF8.GetBytes("authentication failed"), 0, 0];
                await WriteFrameAsync(stream, (byte)'E', error);
            }, TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=wrong;SSL Mode=Disable;Timeout=3;Pooling=false"
            };

            // Act
            CassieException exception = await Assert.ThrowsAsync<CassieException>(connection.OpenAsync);

            // Assert
            Assert.Equal("28P01", exception.SqlState);
            await peer;
        }

        private static async Task<Dictionary<string, string>> ReadStartupParametersAsync(NetworkStream stream)
        {
            byte[] header = new byte[8];
            await stream.ReadExactlyAsync(header);
            int packetLength = BinaryPrimitives.ReadInt32BigEndian(header);
            byte[] packet = new byte[packetLength];
            header.CopyTo(packet, 0);
            await stream.ReadExactlyAsync(packet.AsMemory(header.Length));

            Dictionary<string, string> parameters = new(StringComparer.Ordinal);
            int offset = header.Length;
            while (packet[offset] != 0)
            {
                string key = ReadCString(packet, ref offset);
                string value = ReadCString(packet, ref offset);
                parameters.Add(key, value);
            }

            return parameters;
        }

        private static string ReadCString(byte[] source, ref int offset)
        {
            int terminator = Array.IndexOf(source, (byte)0, offset);
            if (terminator < 0)
            {
                throw new IOException("The startup packet contains an unterminated parameter.");
            }

            string value = Encoding.UTF8.GetString(source, offset, terminator - offset);
            offset = terminator + 1;
            return value;
        }

        private static async Task ReadStartupPacketAsync(NetworkStream stream, byte[] prefix)
        {
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
            byte[] rest = new byte[length - prefix.Length];
            await stream.ReadExactlyAsync(rest);
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
            byte[] frame = new byte[5 + payload.Length];
            frame[0] = tag;
            BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length + 4)).CopyTo(frame, 1);
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame);
        }

    }
}
