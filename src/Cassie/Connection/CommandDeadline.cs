namespace Cntryl.Cassie.Connection;

/// <summary>Combines a caller's cancellation token with the remaining command budget for interruptible I/O.</summary>
internal readonly struct CommandDeadline : IDisposable
{
    private readonly CancellationToken _caller;
    private readonly CommandBudget _budget;
    private readonly CancellationTokenSource? _timeout;

    public CommandDeadline(int seconds, CancellationToken caller) : this(new CommandBudget(seconds), caller)
    {
    }

    public CommandDeadline(CommandBudget budget, CancellationToken caller)
    {
        _caller = caller;
        _budget = budget;

        if (budget.HasTimeout)
        {
            _timeout = caller.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(caller) : new();
            var remaining = budget.Remaining;

            if (remaining == TimeSpan.Zero)
            {
                _timeout.Cancel();
            }
            else
            {
                _timeout.CancelAfter(remaining);
            }
        }
    }

    public CancellationToken Token => _timeout?.Token ?? _caller;

    public bool IsCancellationRequested => Token.IsCancellationRequested;

    public bool TimedOut => !_caller.IsCancellationRequested &&
        (_timeout?.IsCancellationRequested == true || _budget.Remaining == TimeSpan.Zero);

    public void ThrowIfExpired()
    {
        _caller.ThrowIfCancellationRequested();

        if (TimedOut)
        {
            throw new TimeoutException("The command exceeded CommandTimeout.");
        }
    }

    public void Dispose()
    {
        _timeout?.Dispose();
    }
}
