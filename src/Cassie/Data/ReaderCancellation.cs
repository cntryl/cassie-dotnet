using System.Net.Sockets;

namespace Cntryl.Cassie.Data
{
    /// <summary>Coordinates reader cancellation, command deadlines, and complete-frame reads.</summary>
    internal sealed class ReaderCancellation : IAsyncDisposable
    {
        private readonly IReaderSession _session;
        private readonly Timer? _commandTimer;
        private readonly Lock _cancelRequestLock = new();
        private Task<bool>? _cancelRequestTask;
        private volatile bool _complete;
        private volatile bool _timedOut;

        public ReaderCancellation(IReaderSession session, int commandTimeout)
        {
            _session = session;
            if (commandTimeout > 0)
            {
                _commandTimer = new Timer(static state => ((ReaderCancellation)state!).CancelForTimeout(), this, TimeSpan.FromSeconds(commandTimeout), Timeout.InfiniteTimeSpan);
            }
        }

        public bool TimedOut => _timedOut;

        public bool IsTimeoutTransportFailure(Exception exception)
        {
            return _timedOut && exception is IOException or SocketException or ObjectDisposedException;
        }

        public static TimeoutException CreateTimeoutException(Exception inner)
        {
            return new TimeoutException("The command exceeded CommandTimeout and its cancellation could not be confirmed.", inner);
        }

        public async ValueTask DisposeAsync()
        {
            lock (_cancelRequestLock)
            {
                _complete = true;
            }

            if (_commandTimer is not null)
            {
                await _commandTimer.DisposeAsync().ConfigureAwait(false);
            }
        }

        public ValueTask<(byte Tag, byte[] Payload)> ReadFrameAsync(CancellationToken cancellationToken, bool allowAbortOnCancellation = false)
        {
            if (allowAbortOnCancellation || !cancellationToken.CanBeCanceled)
            {
                return _session.ReadFrameAsync(cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Already canceled: register first so the cancel request starts before any frame is returned.
                return ReadQueryFrameWithCancellationAsync(null, cancellationToken);
            }

            // A frame that is already buffered needs no cancellation machinery: nothing can interrupt it.
            ValueTask<(byte Tag, byte[] Payload)> pending = _session.ReadFrameAsync(CancellationToken.None);
            return pending.IsCompletedSuccessfully
                ? pending
                : ReadQueryFrameWithCancellationAsync(pending, cancellationToken);
        }

        private async ValueTask<(byte Tag, byte[] Payload)> ReadQueryFrameWithCancellationAsync(ValueTask<(byte Tag, byte[] Payload)>? pending, CancellationToken cancellationToken)
        {
            using CancellationTokenSource abortDelay = new();
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.UnsafeRegister(static state =>
            {
                (ReaderCancellation reader, CancellationTokenSource delay) = ((ReaderCancellation Reader, CancellationTokenSource AbortDelay))state!;
                reader.StartCancelRequest();
                _ = reader.AbortAfterCancellationGracePeriodAsync(delay.Token);
            }, (this, abortDelay));

            try
            {
                // Finish a frame once reading starts so cancellation cannot leave the stream mid-frame.
                return pending is { } started
                    ? await started.ConfigureAwait(false)
                    : await _session.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when ((cancellationToken.IsCancellationRequested || _timedOut) && (exception is IOException or SocketException or ObjectDisposedException))
            {
                _session.AbortTransport();
                if (_timedOut && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The command exceeded CommandTimeout and its cancellation could not be confirmed.", exception);
                }

                throw new OperationCanceledException("The query was canceled.", exception, cancellationToken);
            }
            finally
            {
                await abortDelay.CancelAsync().ConfigureAwait(false);
            }
        }

        public Task<bool> RequestCancelAsync()
        {
            lock (_cancelRequestLock)
            {
                return _complete ? Task.FromResult(true) : _cancelRequestTask ??= _session.CancelCurrentQueryAsync();
            }
        }

        private void StartCancelRequest()
        {
            if (!_complete)
            {
                _ = ObserveCancelRequestAsync();
            }
        }

        private async Task ObserveCancelRequestAsync()
        {
            try
            {
                if (!await RequestCancelAsync().ConfigureAwait(false) && !_complete)
                {
                    _session.AbortTransport();
                }
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_complete)
                {
                    _session.AbortTransport();
                }
            }
        }

        private async Task AbortAfterCancellationGracePeriodAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (!_complete)
                {
                    _session.AbortTransport();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void CancelForTimeout()
        {
            if (_complete)
            {
                return;
            }

            _timedOut = true;
            _ = ObserveCancelRequestAsync();
            _ = AbortAfterCancellationGracePeriodAsync(CancellationToken.None);
        }
    }
}
