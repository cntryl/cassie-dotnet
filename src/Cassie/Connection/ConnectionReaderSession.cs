using System.Data;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Protocol;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Connection;

/// <summary>Adapts a connection's borrowed query stream to the reader's session contract.</summary>
internal sealed class ConnectionReaderSession(CassieConnection connection, PgSession session) : IReaderSession
{
    private readonly Guid _lease = connection.SessionIdentity;
    private readonly long _operation = connection.OperationSerial;

    public bool IsOpen => connection.State == ConnectionState.Open && connection.SessionIdentity == _lease;
    private bool OwnsOperation => IsOpen && connection.OperationSerial == _operation;

    public CassieDateStyle? DateStyle => session.DateStyle.Current;
    public int? VectorOid => session.VectorOid;
    public ILogger? Logger => connection.Logger;

    public ValueTask<(byte Tag, byte[] Payload)> ReadFrameAsync(RowPayloadBuffer? rowBuffer, CancellationToken cancellationToken)
    {
        return PgFraming.ReadBackendFrameAsync(session.Stream, rowBuffer, cancellationToken);
    }

    public Task<bool> CancelCurrentQueryAsync()
    {
        return connection.CancelCurrentQueryAsync(expectedOperation: _operation);
    }

    public void AbortTransport()
    {
        connection.AbortTransport(expectedOperation: _operation);
    }

    public ValueTask WritePortalPageAsync(ReadOnlyMemory<byte> frames, CancellationToken cancellationToken)
    {
        return session.WriteRawAsync(frames, cancellationToken);
    }

    public Task WriteExtendedSyncAsync(CancellationToken cancellationToken)
    {
        return session.WriteFrameAsync(FrontendMessage.Sync, [], cancellationToken);
    }

    public Task RejectCopyFromAsync()
    {
        return connection.RejectCopyFromAsync();
    }

    public void HandleParameterStatusMessage(byte[] payload)
    {
        if (OwnsOperation)
        {
            connection.HandleParameterStatusMessage(payload);
        }
    }

    public void HandleReadyForQuery(ReadOnlySpan<byte> payload)
    {
        if (!OwnsOperation)
        {
            throw new IOException("The reader no longer owns the connection.");
        }

        connection.HandleReadyForQuery(payload);
    }

    public void ObserveCommandComplete(CommandTag tag)
    {
        if (OwnsOperation)
        {
            connection.ObserveCommandComplete(tag);
        }
    }

    public Task ReaderClosedAsync()
    {
        return OwnsOperation ? connection.ReaderClosedAsync() : Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        return IsOpen ? connection.CloseAsync() : Task.CompletedTask;
    }

    public bool EnterSynchronous()
    {
        return IsOpen && connection.EnterSynchronous();
    }

    public void ExitSynchronous()
    {
        if (connection.SessionIdentity == _lease)
        {
            connection.ExitSynchronous();
        }
    }
}
