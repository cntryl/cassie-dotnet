using System.Net.Sockets;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

/// <summary>
///     One physical connection to the server: the socket and stream, the backend identity needed for
///     cancellation, and the per-session state that must survive pooling.
/// </summary>
internal sealed class PgSession(DateStyleTracker dateStyle, long createdTimestamp) : IAsyncDisposable, IDisposable
{
    internal TcpClient Client { get; } = new() { NoDelay = true };
    internal Stream Stream { get; private set; } = Stream.Null;
    internal string ServerVersion { get; private set; } = string.Empty;
    internal int BackendProcessId { get; private set; }
    internal int BackendSecretKey { get; private set; }
    internal DateStyleTracker DateStyle { get; } = dateStyle;
    internal HashSet<string>? StatementsToClose { get; set; }
    internal int? VectorOid { get; set; }
    internal long CreatedTimestamp { get; } = createdTimestamp;
    internal long ReturnedTimestamp { get; set; }

    /// <summary>
    ///     Gets or sets whether session I/O blocks on the calling thread. Set while a synchronous ADO.NET call
    ///     owns the session; it has no effect before the buffered transport is installed.
    /// </summary>
    internal bool SynchronousIO
    {
        get => Stream is ReadBufferedStream { Synchronous: true };
        set
        {
            if (Stream is ReadBufferedStream transport)
            {
                transport.Synchronous = value;
            }
        }
    }

    /// <summary>Gets whether the server can be asked to cancel the running statement.</summary>
    internal bool CanCancel => BackendProcessId != 0 && BackendSecretKey != 0;

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

    public void Dispose()
    {
        Stream.Dispose();
        Client.Dispose();
    }

    /// <summary>Opens the TCP connection and exposes its stream.</summary>
    internal Task ConnectAsync(string host, int port, bool synchronous, CancellationToken cancellationToken)
    {
        if (!synchronous)
        {
            return ConnectCoreAsync(host, port, cancellationToken);
        }

        SynchronousCall.RunBlocking(Client, (Client, host, port),
            static target => target.Client.Connect(target.host, target.port), cancellationToken);
        Stream = Client.GetStream();
        return Task.CompletedTask;
    }

    private async Task ConnectCoreAsync(string host, int port, CancellationToken cancellationToken)
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

    internal ValueTask<(byte Tag, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken)
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
            var socket = Client.Client;
            return socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }
}
