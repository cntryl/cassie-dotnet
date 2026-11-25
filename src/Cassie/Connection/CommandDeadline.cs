namespace Cntryl.Cassie.Connection;

/// <summary>Combines a caller's cancellation token with the remaining command budget for interruptible I/O.</summary>
internal readonly struct CommandDeadline(CommandBudget budget, CancellationToken caller) : IDisposable
{
    private readonly OperationTimeout _timeout = new(budget.Remaining, budget.Clock, caller);

    public CancellationToken Token => _timeout.Token;

    public bool IsCancellationRequested => Token.IsCancellationRequested;

    public bool TimedOut => !caller.IsCancellationRequested &&
        (_timeout.Elapsed || budget.Remaining == TimeSpan.Zero);

    public void ThrowIfExpired()
    {
        caller.ThrowIfCancellationRequested();

        if (TimedOut)
        {
            throw new TimeoutException("The command exceeded CommandTimeout.");
        }
    }

    public void Dispose()
    {
        _timeout.Dispose();
    }
}
