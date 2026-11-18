using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Data;

/// <summary>The borrowed session operations needed to consume and finish one query.</summary>
internal interface IReaderSession
{
    bool IsOpen { get; }
    CassieDateStyle? DateStyle { get; }
    int? VectorOid { get; }
    ILogger? Logger { get; }
    ValueTask<(byte Tag, byte[] Payload)> ReadFrameAsync(RowPayloadBuffer? rowBuffer, CancellationToken cancellationToken);
    ValueTask WritePortalPageAsync(ReadOnlyMemory<byte> frames, CancellationToken cancellationToken);
    Task WriteExtendedSyncAsync(CancellationToken cancellationToken);
    Task<bool> CancelCurrentQueryAsync();
    void AbortTransport();
    Task RejectCopyFromAsync();
    void HandleParameterStatusMessage(byte[] payload);
    void HandleReadyForQuery(ReadOnlySpan<byte> payload);
    void ObserveCommandComplete(CommandTag tag);
    Task ReaderClosedAsync();
    Task CloseAsync();

    /// <summary>Switches the session to blocking I/O; returns false when an enclosing synchronous call already did.</summary>
    bool EnterSynchronous();

    /// <summary>Switches the session back to asynchronous I/O.</summary>
    void ExitSynchronous();
}
