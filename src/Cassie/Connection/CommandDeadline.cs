namespace Cntryl.Cassie.Connection
{
    /// <summary>Combines a caller's cancellation token with an optional command timeout.</summary>
    internal sealed class CommandDeadline : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _timeout = new();
        private readonly CancellationTokenSource _linked;

        public CommandDeadline(int seconds, CancellationToken caller)
        {
            _caller = caller;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
            if (seconds > 0)
            {
                _timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            }
        }

        public CancellationToken Token => _linked.Token;

        public bool IsCancellationRequested => _linked.IsCancellationRequested;

        /// <summary>Gets whether the timeout, rather than the caller, ended the operation.</summary>
        public bool TimedOut => _timeout.IsCancellationRequested && !_caller.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }
}
