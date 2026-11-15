using Cntryl.Cassie.Connection;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    /// <summary>
    /// TLS is the provider's default mode. These tests run the real SSLRequest exchange and handshake against a
    /// scripted peer that presents a certificate issued for <c>localhost</c>.
    /// </summary>
    public sealed class TlsContractTests
    {
        [Fact]
        public async Task ShouldEncryptSessionGivenTrustedCertificateWhenConnectionOpensAsynchronously()
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: true);
            await using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("localhost", "SSL Mode=Require", trustPeer: true) };

            // Act
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 7";
            object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(7, value);
            Assert.Equal(1, peer.EncryptedSessions);
            Assert.Equal(0, peer.CleartextSessions);
        }

        [Fact]
        public void ShouldEncryptSessionOnCallingThreadGivenTrustedCertificateWhenConnectionOpensSynchronously()
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: true);
            using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("localhost", "SSL Mode=Require", trustPeer: true) };
            int before = SynchronousCall.PendingWaitsOnThisThread;

            // Act
            connection.Open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 7";
            object? value = command.ExecuteScalar();
            connection.Close();
            int pendingWaits = SynchronousCall.PendingWaitsOnThisThread - before;

            // Assert
            Assert.Equal(7, value);
            Assert.Equal(1, peer.EncryptedSessions);
            Assert.Equal(0, pendingWaits);
        }

        [Fact]
        public async Task ShouldRejectCertificateGivenHostNameNotInCertificateWhenConnectionOpens()
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: true);
            await using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("127.0.0.1", "SSL Mode=Require", trustPeer: true) };

            // Act
            Task Open()
            {
                return connection.OpenAsync(TestContext.Current.CancellationToken);
            }

            // Assert
            _ = await Assert.ThrowsAsync<AuthenticationException>(Open);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, peer.EncryptedSessions);
        }

        [Fact]
        public void ShouldRejectCertificateGivenHostNameNotInCertificateWhenConnectionOpensSynchronously()
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: true);
            using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("127.0.0.1", "SSL Mode=Require", trustPeer: true) };

            // Act
            void Open()
            {
                connection.Open();
            }

            // Assert
            _ = Assert.Throws<AuthenticationException>(Open);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Fact]
        public async Task ShouldRejectCertificateGivenIssuerNotTrustedWhenConnectionOpens()
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: true);
            await using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("localhost", "SSL Mode=Require", trustPeer: false) };

            // Act
            Task Open()
            {
                return connection.OpenAsync(TestContext.Current.CancellationToken);
            }

            // Assert
            _ = await Assert.ThrowsAsync<AuthenticationException>(Open);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldRefuseCleartextGivenServerDeclinesTlsWhenModeIsRequire(bool synchronous)
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: false);
            await using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("localhost", "SSL Mode=Require", trustPeer: true) };

            // Act
            Task Open()
            {
                return synchronous
                    ? Task.Run(connection.Open, TestContext.Current.CancellationToken)
                    : connection.OpenAsync(TestContext.Current.CancellationToken);
            }

            // Assert
            _ = await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, peer.CleartextSessions);
        }

        [Fact]
        public async Task ShouldFallBackToCleartextGivenServerDeclinesTlsWhenModeIsPrefer()
        {
            // Arrange
            using TlsPeer peer = new(acceptTls: false);
            await using CassieConnection connection = new() { ConnectionString = peer.ConnectionString("localhost", "SSL Mode=Prefer", trustPeer: true) };

            // Act
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 7";
            object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(7, value);
            Assert.Equal(1, peer.CleartextSessions);
            Assert.Equal(0, peer.EncryptedSessions);
        }

        /// <summary>A pgwire peer that answers SSLRequest, optionally completes a TLS handshake, and serves one-row queries.</summary>
        private sealed class TlsPeer : IDisposable
        {
            private const int SslRequestCode = 80877103;
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly X509Certificate2 _certificate;
            private readonly string _trustBundlePath;
            private readonly bool _acceptTls;
            private int _encryptedSessions;
            private int _cleartextSessions;

            internal TlsPeer(bool acceptTls)
            {
                _acceptTls = acceptTls;
                _certificate = CreateCertificate();
                _trustBundlePath = Path.Combine(Path.GetTempPath(), "cassie-tls-" + Guid.NewGuid().ToString("N") + ".pem");
                File.WriteAllText(_trustBundlePath, _certificate.ExportCertificatePem());
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            /// <summary>Gets the number of sessions that completed startup over TLS.</summary>
            internal int EncryptedSessions => Volatile.Read(ref _encryptedSessions);

            /// <summary>Gets the number of sessions that completed startup without TLS.</summary>
            internal int CleartextSessions => Volatile.Read(ref _cleartextSessions);

            internal string ConnectionString(string host, string mode, bool trustPeer)
            {
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                string trust = trustPeer ? $"SSL Root Certificate={_trustBundlePath};" : string.Empty;
                return $"Host={host};Port={port};Database=ledger;Username=app;Password=secret;{mode};{trust}Timeout=10;Pooling=false";
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Dispose();
                _certificate.Dispose();
                File.Delete(_trustBundlePath);
            }

            private static X509Certificate2 CreateCertificate()
            {
                using RSA key = RSA.Create(2048);
                CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                SubjectAlternativeNameBuilder names = new();
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, critical: true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
                using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

                // Re-import so the private key is usable by the platform TLS stack on every operating system.
                return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
            }

            private async Task AcceptLoopAsync()
            {
                try
                {
                    while (true)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => ServeAsync(client));
                    }
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // The listener was stopped by the test.
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        Stream stream = client.GetStream();
                        byte[] request = new byte[8];
                        await stream.ReadExactlyAsync(request);
                        if (BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4)) != SslRequestCode)
                        {
                            return;
                        }

                        await stream.WriteAsync(new[] { _acceptTls ? (byte)'S' : (byte)'N' });
                        bool encrypted = false;
                        if (_acceptTls)
                        {
                            SslStream tls = new(stream, leaveInnerStreamOpen: false);
                            await tls.AuthenticateAsServerAsync(_certificate);
                            stream = tls;
                            encrypted = true;
                        }

                        await using (stream)
                        {
                            byte[] length = new byte[4];
                            await stream.ReadExactlyAsync(length);
                            await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4]);
                            await stream.WriteAsync(Frame('R', [0, 0, 0, 0]));
                            await stream.WriteAsync(Frame('K', [0, 0, 0, 42, 0, 0, 0, 7]));

                            // Count the session before the client can observe that startup finished.
                            _ = Interlocked.Increment(ref encrypted ? ref _encryptedSessions : ref _cleartextSessions);
                            await stream.WriteAsync(Frame('Z', [(byte)'I']));
                            await ServeQueriesAsync(stream);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or AuthenticationException or EndOfStreamException)
                    {
                        // The provider rejected the handshake or closed the connection.
                    }
                }
            }

            private static async Task ServeQueriesAsync(Stream stream)
            {
                byte[] header = new byte[5];
                while (true)
                {
                    await stream.ReadExactlyAsync(header);
                    byte[] payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4];
                    await stream.ReadExactlyAsync(payload);
                    if (header[0] == (byte)'X')
                    {
                        return;
                    }

                    if (header[0] == (byte)'Q')
                    {
                        byte[] description = [0, 1, .. "value\0"u8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 23, 0, 4, 255, 255, 255, 255, 0, 0];
                        await stream.WriteAsync(Frame('T', description));
                        await stream.WriteAsync(Frame('D', [0, 1, 0, 0, 0, 1, (byte)'7']));
                        await stream.WriteAsync(Frame('C', Encoding.ASCII.GetBytes("SELECT 1\0")));
                        await stream.WriteAsync(Frame('Z', [(byte)'I']));
                    }
                }
            }

            private static byte[] Frame(char tag, byte[] payload)
            {
                byte[] frame = new byte[payload.Length + 5];
                frame[0] = (byte)tag;
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
                payload.CopyTo(frame, 5);
                return frame;
            }
        }
    }
}
