using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class CopyContractTests
{
    [Fact]
    public async Task ShouldStreamCopyInputGivenCsvCopyFromStdinWhenCommandRuns()
    {
        // Arrange
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var input = Encoding.UTF8.GetBytes("1,first\n2,second\n");
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            var (queryTag, query) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', queryTag);
            Assert.Equal("COPY ledger FROM STDIN WITH (FORMAT csv)", CString(query));
            await WriteFrameAsync(stream, (byte)'G', [0, 0, 1, 0, 0]);

            using MemoryStream received = new();

            while (true)
            {
                var (tag, payload) = await ReadFrameAsync(stream);

                if (tag == (byte)'c')
                {
                    break;
                }


                Assert.Equal((byte)'d', tag);
                received.Write(payload);
            }


            Assert.Equal(input, received.ToArray());
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("COPY 2"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }, TestContext.Current.CancellationToken);

        await using var connection = CreateConnection(port);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "COPY ledger FROM STDIN WITH (FORMAT csv)";

        await using MemoryStream source = new(input);

        // Act
        var rows = await command.CopyFromAsync(source, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, rows);
        await peer;
    }

    [Fact]
    public async Task ShouldStreamCopyOutputGivenBackupToStdoutWhenCommandRuns()
    {
        // Arrange
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] output = [0, 1, 2, 3, 255];
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            var (queryTag, query) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', queryTag);
            Assert.Equal("BACKUP DATABASE ledger TO STDOUT", CString(query));
            await WriteFrameAsync(stream, (byte)'H', [0, 0, 1, 0, 0]);
            await WriteFrameAsync(stream, (byte)'d', output[..2]);
            await WriteFrameAsync(stream, (byte)'d', output[2..]);
            await WriteFrameAsync(stream, (byte)'c', []);
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("BACKUP"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }, TestContext.Current.CancellationToken);

        await using var connection = CreateConnection(port);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "BACKUP DATABASE ledger TO STDOUT";
        using MemoryStream destination = new();

        // Act
        var rows = await command.CopyToAsync(destination, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, rows);
        Assert.Equal(output, destination.ToArray());
        await peer;
    }

    [Fact]
    public async Task ShouldReuseConnectionGivenCopyFailWhenInputStreamFails()
    {
        // Arrange
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteFrameAsync(stream, (byte)'R', Int32(0));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            var (queryTag, _) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', queryTag);
            await WriteFrameAsync(stream, (byte)'G', [0, 0, 0]);
            var (failTag, failure) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'f', failTag);
            Assert.Equal("COPY input stream failed.", CString(failure));
            await WriteFrameAsync(stream, (byte)'E', Error("57014", "COPY failed"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            var (nextQueryTag, nextQuery) = await ReadFrameAsync(stream);
            Assert.Equal((byte)'Q', nextQueryTag);
            Assert.Equal("UPDATE ledger SET value = 'ok'", CString(nextQuery));
            await WriteFrameAsync(stream, (byte)'C', CStringBytes("UPDATE 1"));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }, TestContext.Current.CancellationToken);

        await using var connection = CreateConnection(port);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var copyCommand = connection.CreateCommand();
        copyCommand.CommandText = "COPY ledger FROM STDIN WITH (FORMAT csv)";
        using FailingMemoryStream source = new();

        // Act
        var error = await Assert.ThrowsAsync<IOException>(() =>
            copyCommand.CopyFromAsync(source, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("input failed", error.Message);
        await using var nextCommand = connection.CreateCommand();
        nextCommand.CommandText = "UPDATE ledger SET value = 'ok'";
        var affected = await nextCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);
        await peer;
    }

    private static CassieConnection CreateConnection(int port)
    {
        return new CassieConnection
        {
            ConnectionString =
                $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
        };
    }

    private static async Task ReadStartupAsync(NetworkStream stream)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length);
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(length);
        await stream.ReadExactlyAsync(new byte[messageLength - length.Length]);
    }

    private static async Task<(byte Tag, byte[] Payload)> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var payload = new byte[length - 4];
        await stream.ReadExactlyAsync(payload);
        return (header[0], payload);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame, 5);
        await stream.WriteAsync(frame);
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] CStringBytes(string value)
    {
        return [.. Encoding.UTF8.GetBytes(value), 0];
    }

    private static string CString(byte[] bytes)
    {
        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
    }

    private static byte[] Error(string sqlState, string message)
    {
        return
        [
            (byte)'S', .. Encoding.UTF8.GetBytes("ERROR"), 0,
            (byte)'C', .. Encoding.UTF8.GetBytes(sqlState), 0,
            (byte)'M', .. Encoding.UTF8.GetBytes(message), 0,
            0
        ];
    }

    private sealed class FailingMemoryStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(new IOException("input failed"));
        }
    }
}
