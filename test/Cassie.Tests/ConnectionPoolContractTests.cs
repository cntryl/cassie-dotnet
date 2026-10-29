using System.Buffers.Binary;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class ConnectionPoolContractTests
    {
        [Fact]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "The test stops the listener and awaits the peer in finally before disposing the listener.")]
        public async Task ShouldClearPooledSessionGivenDefaultDatabaseWhenPoolIsCleared()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string connectionString = $"Host=127.0.0.1;Port={port};Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=true;Maximum Pool Size=1";
            Task<bool> peer = ServePoolClearAsync(listener, "app", "secret", "app", "secret", "app");
            using CassieConnection first = new() { ConnectionString = connectionString };
            using CassieConnection second = new() { ConnectionString = connectionString };

            try
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
                Assert.Equal("app", first.Database);
                await first.CloseAsync();
                CassieConnection.ClearPool(first);

                // Act
                await second.OpenAsync(TestContext.Current.CancellationToken);
                using DbCommand command = second.CreateCommand();
                command.CommandText = "SELECT 1";
                object? result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                await second.CloseAsync();
                bool reusedClearedSession = await peer.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

                // Assert
                Assert.Equal(1, result);
                Assert.False(reusedClearedSession);
            }
            finally
            {
                CassieConnection.ClearAllPools();
                listener.Stop();
                try
                {
                    _ = await peer.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                }
                catch (SocketException)
                {
                    // The peer stops accepting when the test exits early.
                }
                catch (TimeoutException)
                {
                    // The scripted peer has a bounded lifetime even when the connection fails.
                }
            }
        }

        [Fact]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "The test stops the listener and awaits the peer in finally before disposing the listener.")]
        public async Task ShouldSeparatePooledSessionsGivenNewlinesInCredentialsWhenConnectionsOpen()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            CassieConnectionStringBuilder firstSettings = new()
            {
                Host = "127.0.0.1",
                Port = port,
                Database = "ledger",
                Username = "app\nsecret",
                Password = "p",
                SslMode = CassieSslMode.Disable,
                Timeout = 3,
                Pooling = true,
                MaxPoolSize = 1
            };
            CassieConnectionStringBuilder secondSettings = new()
            {
                Host = "127.0.0.1",
                Port = port,
                Database = "ledger",
                Username = "app",
                Password = "secret\np",
                SslMode = CassieSslMode.Disable,
                Timeout = 3,
                Pooling = true,
                MaxPoolSize = 1
            };
            Task<bool> peer = ServePoolClearAsync(listener, "app\nsecret", "p", "app", "secret\np", "ledger");
            using CassieConnection first = new() { ConnectionString = firstSettings.ConnectionString };
            using CassieConnection second = new() { ConnectionString = secondSettings.ConnectionString };

            try
            {
                await first.OpenAsync(TestContext.Current.CancellationToken);
                await first.CloseAsync();

                // Act
                await second.OpenAsync(TestContext.Current.CancellationToken);
                using DbCommand command = second.CreateCommand();
                command.CommandText = "SELECT 1";
                object? result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                await second.CloseAsync();
                bool reusedCredentialSession = await peer.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

                // Assert
                Assert.Equal(1, result);
                Assert.False(reusedCredentialSession);
            }
            finally
            {
                CassieConnection.ClearAllPools();
                listener.Stop();
                try
                {
                    _ = await peer.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                }
                catch (SocketException)
                {
                    // The peer stops accepting when the test exits early.
                }
                catch (TimeoutException)
                {
                    // The scripted peer has a bounded lifetime even when the connection fails.
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "The caller always awaits this peer task before disposing the listener.")]
        private static async Task<bool> ServePoolClearAsync(
            TcpListener listener,
            string firstUsername,
            string firstPassword,
            string secondUsername,
            string secondPassword,
            string database)
        {
            using TcpClient firstClient = await listener.AcceptTcpClientAsync();
            NetworkStream firstStream = firstClient.GetStream();
            Dictionary<string, string> firstStartup = await ReadStartupParametersAsync(firstStream);
            Assert.Equal(firstUsername, firstStartup["user"]);
            Assert.Equal(database, firstStartup["database"]);
            await CompleteStartupAsync(firstStream, firstPassword);
            await ReadDiscardAndCompleteAsync(firstStream);

            Task<byte[]> previousSessionActivity = ReadFrontendFrameAsync(firstStream);
            Task<TcpClient> nextClient = listener.AcceptTcpClientAsync();
            Task completed = await Task.WhenAny(previousSessionActivity, nextClient).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (completed == nextClient)
            {
                using TcpClient secondClient = await nextClient;
                NetworkStream secondStream = secondClient.GetStream();
                Dictionary<string, string> secondStartup = await ReadStartupParametersAsync(secondStream);
                Assert.Equal(secondUsername, secondStartup["user"]);
                Assert.Equal(database, secondStartup["database"]);
                await CompleteStartupAsync(secondStream, secondPassword);
                await ReadSelectOneAndCompleteAsync(secondStream);
                return false;
            }

            byte[]? nextFrame;
            try
            {
                nextFrame = await previousSessionActivity;
            }
            catch (EndOfStreamException)
            {
                // The idle physical session was cleared before the next logical open.
                using TcpClient secondClient = await nextClient.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                NetworkStream secondStream = secondClient.GetStream();
                Dictionary<string, string> secondStartup = await ReadStartupParametersAsync(secondStream);
                Assert.Equal(secondUsername, secondStartup["user"]);
                Assert.Equal(database, secondStartup["database"]);
                await CompleteStartupAsync(secondStream, secondPassword);
                await ReadSelectOneAndCompleteAsync(secondStream);
                return false;
            }

            Assert.Equal((byte)'Q', nextFrame[0]);
            await WriteSelectOneResultAsync(firstStream);
            await ReadDiscardAndCompleteAsync(firstStream);
            return true;
        }

        private static async Task ReadSelectOneAndCompleteAsync(NetworkStream stream)
        {
            byte[] query = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'Q', query[0]);
            Assert.Equal("SELECT 1\0", Encoding.UTF8.GetString(query, 1, query.Length - 1));
            await WriteSelectOneResultAsync(stream);
            await ReadDiscardAndCompleteAsync(stream);
        }

        private static async Task<Dictionary<string, string>> ReadStartupParametersAsync(NetworkStream stream)
        {
            byte[] header = new byte[8];
            await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            byte[] packet = new byte[length];
            header.CopyTo(packet, 0);
            await stream.ReadExactlyAsync(packet.AsMemory(header.Length), TestContext.Current.CancellationToken);

            Dictionary<string, string> parameters = [];
            int offset = 8;
            while (offset < packet.Length && packet[offset] != 0)
            {
                string key = ReadCString(packet, ref offset);
                string value = ReadCString(packet, ref offset);
                parameters.Add(key, value);
            }

            return parameters;
        }

        private static async Task CompleteStartupAsync(NetworkStream stream, string expectedPassword)
        {
            await WriteBackendFrameAsync(stream, (byte)'R', [0, 0, 0, 3]);
            byte[] password = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'p', password[0]);
            Assert.Equal(expectedPassword + "\0", Encoding.UTF8.GetString(password, 1, password.Length - 1));
            await WriteBackendFrameAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WriteBackendFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task ReadDiscardAndCompleteAsync(NetworkStream stream)
        {
            byte[] query = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'Q', query[0]);
            Assert.Equal("DISCARD ALL\0", Encoding.UTF8.GetString(query, 1, query.Length - 1));
            await WriteBackendFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("DISCARD ALL"), 0]);
            await WriteBackendFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task WriteSelectOneResultAsync(NetworkStream stream)
        {
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "value");
            WriteInt32(description, 0);
            WriteInt16(description, 0);
            WriteInt32(description, 23);
            WriteInt16(description, 4);
            WriteInt32(description, -1);
            WriteInt16(description, 0);
            await WriteBackendFrameAsync(stream, (byte)'T', description.ToArray());

            using MemoryStream row = new();
            WriteInt16(row, 1);
            WriteInt32(row, 1);
            row.WriteByte((byte)'1');
            await WriteBackendFrameAsync(stream, (byte)'D', row.ToArray());
            await WriteBackendFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            await WriteBackendFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static async Task<byte[]> ReadFrontendFrameAsync(NetworkStream stream)
        {
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            byte[] frame = new byte[length - 3];
            frame[0] = header[0];
            await stream.ReadExactlyAsync(frame.AsMemory(1, length - 4), TestContext.Current.CancellationToken);
            return frame;
        }

        private static async Task WriteBackendFrameAsync(NetworkStream stream, byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame, TestContext.Current.CancellationToken);
        }

        private static string ReadCString(byte[] packet, ref int offset)
        {
            int end = Array.IndexOf(packet, (byte)0, offset);
            if (end < 0)
            {
                throw new IOException("Invalid startup packet.");
            }

            string value = Encoding.UTF8.GetString(packet, offset, end - offset);
            offset = end + 1;
            return value;
        }

        private static void WriteCString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.WriteByte(0);
        }

        private static void WriteInt16(Stream stream, short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
