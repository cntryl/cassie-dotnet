using Cntryl.Cassie.Protocol;
using System.Net.Sockets;

namespace Cntryl.Cassie.Connection
{
    /// <summary>
    /// One physical connection to the server: the socket and stream, the backend identity needed for
    /// cancellation, and the per-session state that must survive pooling.
    /// </summary>
    internal sealed class PgSession(DateStyleTracker dateStyle, long createdTimestamp) : IAsyncDisposable, IDisposable
    {
        internal TcpClient Client { get; } = new() { NoDelay = true };
        internal Stream Stream { get; private set; } = Stream.Null;
        internal string ServerVersion { get; private set; } = string.Empty;
        internal int BackendProcessId { get; private set; }
        internal int BackendSecretKey { get; private set; }
        internal DateStyleTracker DateStyle { get; } = dateStyle;
        internal long CreatedTimestamp { get; } = createdTimestamp;
        internal long ReturnedTimestamp { get; set; }

        /// <summary>Opens the TCP connection and exposes its stream.</summary>
        internal async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            await Client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            Stream = Client.GetStream();
        }

        /// <summary>Replaces the transport stream, for example after a TLS handshake wraps the socket.</summary>
        internal void UseStream(Stream stream)
        {
            Stream = stream;
        }

        /// <summary>Records what the server reported during startup.</summary>
        internal void Identify(string serverVersion, int backendProcessId, int backendSecretKey)
        {
            ServerVersion = serverVersion;
            BackendProcessId = backendProcessId;
            BackendSecretKey = backendSecretKey;
        }

        /// <summary>Gets whether the server can be asked to cancel the running statement.</summary>
        internal bool CanCancel => BackendProcessId != 0 && BackendSecretKey != 0;

        internal Task<(byte Tag, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken)
        {
            return PgFraming.ReadBackendFrameAsync(Stream, cancellationToken);
        }

        internal Task WriteFrameAsync(byte tag, byte[] payload, CancellationToken cancellationToken)
        {
            return PgFraming.WriteFrontendFrameAsync(Stream, tag, payload, cancellationToken);
        }

        internal async Task WriteFrameAsync(byte tag, MemoryStream payload, CancellationToken cancellationToken)
        {
            using (payload)
            {
                await WriteFrameAsync(tag, payload.ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }

        internal Task WriteBatchAsync(MemoryStream batch, CancellationToken cancellationToken)
        {
            return PgFraming.WriteBatchAsync(Stream, batch, cancellationToken);
        }

        internal ValueTask WriteRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            return Stream.WriteAsync(bytes, cancellationToken);
        }

        /// <summary>Returns false when the peer has closed the socket while the session sat idle.</summary>
        internal bool IsTransportAlive()
        {
            try
            {
                Socket socket = Client.Client;
                return socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            Stream.Dispose();
            Client.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Client.Dispose();
            }
        }
    }
}
