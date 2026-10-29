using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Data
{
    /// <summary>The borrowed session operations needed to consume and finish one query.</summary>
    internal interface IReaderSession
    {
        bool IsOpen { get; }
        CassieDateStyle? DateStyle { get; }
        ILogger? Logger { get; }
        Task<(byte Tag, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken);
        Task<bool> CancelCurrentQueryAsync();
        void AbortTransport();
        Task RejectCopyFromAsync();
        void HandleParameterStatusMessage(byte[] payload);
        void HandleReadyForQuery(ReadOnlySpan<byte> payload);
        void ObserveCommandComplete(CommandTag tag);
        Task ReaderClosedAsync();
        Task CloseAsync();
    }
}
