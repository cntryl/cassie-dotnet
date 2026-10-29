using Cntryl.Cassie.Data;
using Cntryl.Cassie.Protocol;
using Microsoft.Extensions.Logging;
using System.Data;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Adapts a connection's borrowed query stream to the reader's session contract.</summary>
    internal sealed class ConnectionReaderSession(CassieConnection connection, Stream stream) : IReaderSession
    {
        public bool IsOpen => connection.State == ConnectionState.Open;
        public CassieDateStyle? DateStyle => connection.DateStyle;
        public ILogger? Logger => connection.Logger;

        public Task<(byte Tag, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken)
        {
            return PgFraming.ReadBackendFrameAsync(stream, cancellationToken);
        }

        public Task<bool> CancelCurrentQueryAsync()
        {
            return connection.CancelCurrentQueryAsync();
        }

        public void AbortTransport()
        {
            connection.AbortTransport();
        }

        public Task RejectCopyFromAsync()
        {
            return connection.RejectCopyFromAsync();
        }

        public void HandleParameterStatusMessage(byte[] payload)
        {
            connection.HandleParameterStatusMessage(payload);
        }

        public void HandleReadyForQuery(ReadOnlySpan<byte> payload)
        {
            connection.HandleReadyForQuery(payload);
        }

        public void ObserveCommandComplete(CommandTag tag)
        {
            connection.ObserveCommandComplete(tag);
        }

        public Task ReaderClosedAsync()
        {
            return connection.ReaderClosedAsync();
        }

        public Task CloseAsync()
        {
            return connection.CloseAsync();
        }
    }
}
