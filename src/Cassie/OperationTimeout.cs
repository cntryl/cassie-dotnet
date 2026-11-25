namespace Cntryl.Cassie;

/// <summary>Combines an operation's optional clock-based timeout with caller cancellation.</summary>
internal readonly struct OperationTimeout : IDisposable
{
    private readonly CancellationTokenSource? _timeout;
    private readonly CancellationTokenSource? _linked;

    public OperationTimeout(TimeSpan duration, TimeProvider clock, CancellationToken caller)
    {
        if (duration == Timeout.InfiniteTimeSpan)
        {
            Token = caller;
            return;
        }

        _timeout = duration == TimeSpan.Zero ? new CancellationTokenSource() : new CancellationTokenSource(duration, clock);

        if (duration == TimeSpan.Zero)
        {
            _timeout.Cancel();
        }

        _linked = caller.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token) : null;
        Token = _linked?.Token ?? _timeout.Token;
    }

    public CancellationToken Token { get; }

    public bool Elapsed => _timeout?.IsCancellationRequested == true;

    public void Dispose()
    {
        _linked?.Dispose();
        _timeout?.Dispose();
    }
}
