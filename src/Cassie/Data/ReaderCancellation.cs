using System.Net.Sockets;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Data;

/// <summary>Coordinates reader cancellation, command deadlines, and complete-frame reads.</summary>
internal sealed class ReaderCancellation : IAsyncDisposable
{
    private readonly Lock _cancelRequestLock = new();
    private readonly Timer? _commandTimer;
    private readonly IReaderSession _session;
    private readonly RowPayloadBuffer? _rowBuffer;
    private Task<bool>? _cancelRequestTask;
    private volatile bool _complete;
    private volatile bool _timedOut;
    private bool _writing;
    private bool _timeoutWhileWriting;

    public CommandBudget Budget { get; }

    public ReaderCancellation(IReaderSession session, int commandTimeout) : this(session, new CommandBudget(commandTimeout))
    {
    }

    public ReaderCancellation(IReaderSession session, CommandBudget budget, RowPayloadBuffer? rowBuffer = null)
    {
        _session = session;
        _rowBuffer = rowBuffer;
        Budget = budget;

        if (budget.HasTimeout)
        {
            _commandTimer = new Timer(static state => ((ReaderCancellation)state!).CancelForTimeout(), this,
                budget.Remaining, Timeout.InfiniteTimeSpan);
        }
    }

    public bool TimedOut => _timedOut || (!_complete && Budget.Remaining == TimeSpan.Zero);

    public bool TryBeginWrite()
    {
        lock (_cancelRequestLock)
        {
            if (TimedOut)
            {
                return false;
            }

            _writing = true;
            return true;
        }
    }

    public void EndWrite(bool dispatched)
    {
        lock (_cancelRequestLock)
        {
            _writing = false;

            if (!dispatched)
            {
                _complete = true;
            }
        }

        if (dispatched && (_timeoutWhileWriting || TimedOut))
        {
            CancelForTimeout();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_cancelRequestLock)
        {
            _timedOut = TimedOut;
            _complete = true;
        }

        if (_commandTimer is not null)
        {
            await _commandTimer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public bool IsTimeoutTransportFailure(Exception exception)
    {
        return TimedOut && exception is IOException or SocketException or ObjectDisposedException;
    }

    public static TimeoutException CreateTimeoutException(Exception inner)
    {
        return new TimeoutException("The command exceeded CommandTimeout and its cancellation could not be confirmed.",
            inner);
    }

    public ValueTask<(byte Tag, byte[] Payload)> ReadFrameAsync(
        CancellationToken cancellationToken,
        bool allowAbortOnCancellation = false
    )
    {
        if (allowAbortOnCancellation || !cancellationToken.CanBeCanceled)
        {
            return _session.ReadFrameAsync(_rowBuffer, cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Already canceled: register first so the cancel request starts before any frame is returned.
            return ReadQueryFrameWithCancellationAsync(null, cancellationToken);
        }

        // A frame that is already buffered needs no cancellation machinery: nothing can interrupt it.
        var pending = _session.ReadFrameAsync(_rowBuffer, CancellationToken.None);
        return pending.IsCompletedSuccessfully
            ? pending
            : ReadQueryFrameWithCancellationAsync(pending, cancellationToken);
    }

    private async ValueTask<(byte Tag, byte[] Payload)> ReadQueryFrameWithCancellationAsync(
        ValueTask<(byte Tag, byte[] Payload)>? pending,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource abortDelay = new();
        using var cancellationRegistration = cancellationToken.UnsafeRegister(static state =>
        {
            var (reader, delay) = ((ReaderCancellation Reader, CancellationTokenSource AbortDelay))state!;
            reader.StartCancelRequest();
            _ = reader.AbortAfterCancellationGracePeriodAsync(delay.Token);
        }, (this, abortDelay));

        try
        {
            // Finish a frame once reading starts so cancellation cannot leave the stream mid-frame.
            return pending is { } started
                ? await started.ConfigureAwait(false)
                : await _session.ReadFrameAsync(_rowBuffer, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when ((cancellationToken.IsCancellationRequested || _timedOut) &&
                                          exception is IOException or SocketException or ObjectDisposedException)
        {
            _session.AbortTransport();

            if (_timedOut && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The command exceeded CommandTimeout and its cancellation could not be confirmed.", exception);
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
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException
                                              or ObjectDisposedException)
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
        lock (_cancelRequestLock)
        {
            if (_complete || _timedOut)
            {
                return;
            }

            if (_writing)
            {
                _timeoutWhileWriting = true;
                return;
            }

            _timedOut = true;
        }

        _ = ObserveCancelRequestAsync();
        _ = AbortAfterCancellationGracePeriodAsync(CancellationToken.None);
    }
}
