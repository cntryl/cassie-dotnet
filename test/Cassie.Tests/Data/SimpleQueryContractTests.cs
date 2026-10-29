using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Data
{
    public sealed class SimpleQueryContractTests
    {
        [Fact]
        public async Task ShouldStreamTextRowsGivenSimpleQueryWhenReaderAdvances()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeOneTextQueryAsync(listener), TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 'hello'";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            bool readerHasRows = reader.HasRows;
            bool hasRow = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.True(readerHasRows);
            Assert.True(hasRow);
            Assert.Equal("value", reader.GetName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.Equal("text", reader.GetDataTypeName(0));
            Assert.Equal("hello", reader.GetString(0));
            char[] chars = new char[5];
            Assert.Equal(5, reader.GetChars(0, 0, chars, 0, chars.Length));
            Assert.Equal("hello", new string(chars));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(-1, reader.RecordsAffected);
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldRejectBackwardsColumnReadGivenSequentialAccessWhenReaderAdvances()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeTwoColumnTextQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1, 2";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            int secondValue = reader.GetInt32(1);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => reader.GetInt32(0));
            bool secondRowExists = await reader.ReadAsync(TestContext.Current.CancellationToken);
            int firstValueOnSecondRow = reader.GetInt32(0);
            bool thirdRowExists = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, secondValue);
            Assert.Contains("column order", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(secondRowExists);
            Assert.Equal(3, firstValueOnSecondRow);
            Assert.False(thirdRowExists);
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldRetainConnectionGivenQueryErrorWhenReadyFrameArrives()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeErrorThenEmptyQueryAsync(listener), TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand failedCommand = connection.CreateCommand();
            failedCommand.CommandText = "SELECT 1 / 0";

            // Act
            CassieException exception = await Assert.ThrowsAsync<CassieException>(failedCommand.ExecuteReaderAsync);
            using DbCommand nextCommand = connection.CreateCommand();
            nextCommand.CommandText = "SELECT 1";
            await using DbDataReader nextReader = await nextCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            bool nextHasRows = nextReader.HasRows;
            bool hasNextRow = await nextReader.ReadAsync(TestContext.Current.CancellationToken);
            bool hasAnotherResult = await nextReader.NextResultAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("22012", exception.SqlState);
            Assert.Equal("division detail", exception.Detail);
            Assert.Equal("check the denominator", exception.Hint);
            Assert.Equal("ERROR", exception.Severity);
            Assert.Equal("executor.c", exception.File);
            Assert.Equal("42", exception.Line);
            Assert.Equal("ExecProcNode", exception.Routine);
            Assert.False(nextHasRows);
            Assert.False(hasNextRow);
            Assert.False(hasAnotherResult);
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldAdvanceResultSetsGivenMultipleSelectStatementsWhenNextResultIsCalled()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeTwoTextResultsAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 'first'; SELECT 'second'";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            bool firstRead = await reader.ReadAsync(TestContext.Current.CancellationToken);
            string first = reader.GetString(0);
            bool firstResultEnded = await reader.ReadAsync(TestContext.Current.CancellationToken);
            bool hasSecondResult = await reader.NextResultAsync(TestContext.Current.CancellationToken);
            bool secondRead = await reader.ReadAsync(TestContext.Current.CancellationToken);
            string second = reader.GetString(0);
            bool secondResultEnded = await reader.ReadAsync(TestContext.Current.CancellationToken);
            bool hasThirdResult = await reader.NextResultAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.True(firstRead);
            Assert.Equal("first", first);
            Assert.False(firstResultEnded);
            Assert.True(hasSecondResult);
            Assert.True(secondRead);
            Assert.Equal("second", second);
            Assert.False(secondResultEnded);
            Assert.False(hasThirdResult);
            await connection.CloseAsync();
            await peer;
        }

        private static async Task ServeTwoTextResultsAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            _ = await ReadFrontendFrameAsync(stream);
            await WriteResultAsync(stream, "first");
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            await WriteResultAsync(stream, "second");
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task WriteResultAsync(NetworkStream stream, string value)
        {
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 25);
            WriteInt16(description, -1);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            using MemoryStream row = new();
            WriteInt16(row, 1);
            WriteInt32(row, Encoding.UTF8.GetByteCount(value));
            row.Write(Encoding.UTF8.GetBytes(value));
            await WriteFrameAsync(stream, (byte)'D', row.ToArray());
        }

        private static async Task ServeErrorThenEmptyQueryAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            _ = await ReadFrontendFrameAsync(stream);
            byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("ERROR"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("22012"), 0,
                (byte)'M', .. Encoding.UTF8.GetBytes("division by zero"), 0, (byte)'D', .. Encoding.UTF8.GetBytes("division detail"), 0,
                (byte)'H', .. Encoding.UTF8.GetBytes("check the denominator"), 0, (byte)'F', .. Encoding.UTF8.GetBytes("executor.c"), 0,
                (byte)'L', .. Encoding.UTF8.GetBytes("42"), 0, (byte)'R', .. Encoding.UTF8.GetBytes("ExecProcNode"), 0, 0];
            await WriteFrameAsync(stream, (byte)'E', error);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            _ = await ReadFrontendFrameAsync(stream);
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
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 0"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task ServeOneTextQueryAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] query = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'Q', query[0]);
            Assert.Equal("SELECT 'hello'\0", Encoding.UTF8.GetString(query, 1, query.Length - 1));

            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 25);
            WriteInt16(description, -1);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            using MemoryStream row = new();
            WriteInt16(row, 1);
            WriteInt32(row, 5);
            row.Write(Encoding.UTF8.GetBytes("hello"));
            await WriteFrameAsync(stream, (byte)'D', row.ToArray());
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task ServeTwoColumnTextQueryAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] query = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'Q', query[0]);
            Assert.Equal("SELECT 1, 2\0", Encoding.UTF8.GetString(query, 1, query.Length - 1));

            using MemoryStream description = new();
            WriteInt16(description, 2);
            WriteCString(description, "first_value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 23);
            WriteInt16(description, 4);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            WriteCString(description, "second_value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 23);
            WriteInt16(description, 4);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());

            using MemoryStream row = new();
            WriteInt16(row, 2);
            WriteInt32(row, 1);
            row.WriteByte((byte)'1');
            WriteInt32(row, 1);
            row.WriteByte((byte)'2');
            await WriteFrameAsync(stream, (byte)'D', row.ToArray());
            using MemoryStream nextRow = new();
            WriteInt16(nextRow, 2);
            WriteInt32(nextRow, 1);
            nextRow.WriteByte((byte)'3');
            WriteInt32(nextRow, 1);
            nextRow.WriteByte((byte)'4');
            await WriteFrameAsync(stream, (byte)'D', nextRow.ToArray());
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] terminateFrame = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'X', terminateFrame[0]);
        }

        private static async Task ReadStartupPacketAsync(NetworkStream stream, byte[] prefix)
        {
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
            await stream.ReadExactlyAsync(new byte[length - prefix.Length]);
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
