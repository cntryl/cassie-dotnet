using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Types;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Types
{
    public sealed class NumericContractTests
    {
        [Fact]
        public void ShouldPreserveArbitraryPrecisionGivenNumericTextWhenValueIsCreated()
        {
            // Arrange
            const string value = "123456789012345678901234567890.12345678901234567890";

            // Act
            CassieNumeric numeric = new(value);

            // Assert
            Assert.Equal(value, numeric.ToString());
        }

        [Fact]
        public void ShouldRejectMalformedValueGivenNumericTextWhenValueIsCreated()
        {
            // Arrange
            const string value = "1.2; DROP TABLE ledger";

            // Act
            FormatException exception = Assert.Throws<FormatException>(() => new CassieNumeric(value));

            // Assert
            Assert.Contains("numeric", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ShouldReturnLosslessNumericGivenHighPrecisionColumnWhenReaderReadsValue()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeNumericQueryAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 123456789012345678901234567890.12345678901234567890::numeric";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            CassieNumeric value = Assert.IsType<CassieNumeric>(reader.GetValue(0));

            // Assert
            Assert.Equal("123456789012345678901234567890.12345678901234567890", value.ToString());
            Assert.Equal(typeof(CassieNumeric), reader.GetFieldType(0));
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldReturnDecimalGivenNumericColumnWhenReaderReadsGenericDecimalValue()
        {
            // Arrange
            const string number = "123.45";
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeNumericQueryAsync(listener, number), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 123.45::numeric";

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            decimal value = await reader.GetFieldValueAsync<decimal>(0, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(123.45m, value);
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        private static async Task ServeNumericQueryAsync(TcpListener listener, string number = "123456789012345678901234567890.12345678901234567890")
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            int startupLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(startup, 0));
            await stream.ReadExactlyAsync(new byte[startupLength - startup.Length]);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            _ = await ReadFrontendFrameAsync(stream);
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "numeric_value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 1700);
            WriteInt16(description, -1);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            byte[] text = Encoding.UTF8.GetBytes(number);
            using MemoryStream row = new();
            WriteInt16(row, 1);
            WriteInt32(row, text.Length);
            row.Write(text);
            await WriteFrameAsync(stream, (byte)'D', row.ToArray());
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
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
