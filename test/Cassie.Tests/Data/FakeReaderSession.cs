using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Protocol;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Tests.Data
{
    internal sealed class FakeReaderSession : IReaderSession
    {
        private readonly Queue<(byte Tag, byte[] Payload)> _frames = new();
        private int _cancelRequests;
        private int _aborts;

        public bool IsOpen { get; private set; } = true;
        public CassieDateStyle? DateStyle => null;
        public ILogger? Logger => null;
        public int CancelRequests => Volatile.Read(ref _cancelRequests);
        public int Aborts => Volatile.Read(ref _aborts);
        public int ReaderClosures { get; private set; }
        public int ConnectionClosures { get; private set; }
        public List<byte[]> ReadyMessages { get; } = [];
        public List<byte[]> ParameterStatuses { get; } = [];
        public List<CommandTag> CompletedCommands { get; } = [];
        public List<CancellationToken> ReadTokens { get; } = [];
        public Func<Task<bool>>? CancelHandler { get; set; }
        public Func<CancellationToken, Task<(byte Tag, byte[] Payload)>>? ReadHandler { get; set; }

        public void Enqueue(byte tag, byte[] payload)
        {
            _frames.Enqueue((tag, payload));
        }

        public Task<(byte Tag, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken)
        {
            ReadTokens.Add(cancellationToken);
            return ReadHandler is null
                ? Task.FromResult(_frames.Dequeue())
                : ReadHandler(cancellationToken);
        }

        public Task<bool> CancelCurrentQueryAsync()
        {
            _ = Interlocked.Increment(ref _cancelRequests);
            return CancelHandler?.Invoke() ?? Task.FromResult(true);
        }

        public void AbortTransport()
        {
            _ = Interlocked.Increment(ref _aborts);
            IsOpen = false;
        }

        public Task RejectCopyFromAsync()
        {
            return Task.CompletedTask;
        }

        public void HandleParameterStatusMessage(byte[] payload)
        {
            ParameterStatuses.Add(payload);
        }

        public void HandleReadyForQuery(ReadOnlySpan<byte> payload)
        {
            ReadyMessages.Add(payload.ToArray());
        }

        public void ObserveCommandComplete(CommandTag tag)
        {
            CompletedCommands.Add(tag);
        }

        public Task ReaderClosedAsync()
        {
            ReaderClosures++;
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            ConnectionClosures++;
            IsOpen = false;
            return Task.CompletedTask;
        }
    }
}
