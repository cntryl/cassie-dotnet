using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class ConnectionOpenContractTests
{
    [Fact]
    public async Task ShouldAuthenticateGivenCleartextChallengeWhenConnectionOpens()
    {
        // Arrange
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(3)));
            var password = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'p', password[0]);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }, TestContext.Current.CancellationToken);
        using CassieConnection connection = new()
        {
            ConnectionString =
                $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
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
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var startup = await ReadStartupParametersAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            var terminate = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'X', terminate[0]);
            Assert.Equal(options, startup["options"]);
        }, TestContext.Current.CancellationToken);
        using CassieConnection connection = new()
        {
            ConnectionString =
                $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;SSL Mode=Disable;Timeout=3;Pooling=false;Options={options}"
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
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(3)));
            _ = await ReadFrontendFrameAsync(stream);
            byte[] error =
            [
                (byte)'S', .. Encoding.UTF8.GetBytes("FATAL"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("28P01"), 0,
                (byte)'M', .. Encoding.UTF8.GetBytes("authentication failed"), 0, 0
            ];
            await WriteFrameAsync(stream, (byte)'E', error);
        }, TestContext.Current.CancellationToken);
        using CassieConnection connection = new()
        {
            ConnectionString =
                $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=wrong;SSL Mode=Disable;Timeout=3;Pooling=false"
        };

        // Act
        var exception = await Assert.ThrowsAsync<CassieException>(connection.OpenAsync);

        // Assert
        Assert.Equal("28P01", exception.SqlState);
        await peer;
    }

    private static async Task<Dictionary<string, string>> ReadStartupParametersAsync(NetworkStream stream)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header);
        var packetLength = BinaryPrimitives.ReadInt32BigEndian(header);
        var packet = new byte[packetLength];
        header.CopyTo(packet, 0);
        await stream.ReadExactlyAsync(packet.AsMemory(header.Length));

        Dictionary<string, string> parameters = [with(StringComparer.Ordinal)];
        var offset = header.Length;

        while (packet[offset] != 0)
        {
            var key = ReadCString(packet, ref offset);
            var value = ReadCString(packet, ref offset);
            parameters.Add(key, value);
        }


        return parameters;
    }

    private static string ReadCString(byte[] source, ref int offset)
    {
        var terminator = Array.IndexOf(source, (byte)0, offset);

        if (terminator < 0)
        {
            throw new IOException("The startup packet contains an unterminated parameter.");
        }


        var value = Encoding.UTF8.GetString(source, offset, terminator - offset);
        offset = terminator + 1;
        return value;
    }

    private static async Task ReadStartupPacketAsync(NetworkStream stream, byte[] prefix)
    {
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
        var rest = new byte[length - prefix.Length];
        await stream.ReadExactlyAsync(rest);
    }

    private static async Task<byte[]> ReadFrontendFrameAsync(NetworkStream stream)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 1));
        var payload = new byte[length - 4];
        await stream.ReadExactlyAsync(payload);
        return [header[0], .. payload];
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = tag;
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length + 4)).CopyTo(frame, 1);
        payload.CopyTo(frame, 5);
        await stream.WriteAsync(frame);
    }
}
