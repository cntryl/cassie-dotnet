namespace Cntryl.Cassie.Tests.Connection
{
    /// <summary>A manually advanced clock for the fixed COPY recovery deadline.</summary>
    internal sealed class CopyRecoveryClock : TimeProvider, IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<RecoveryTimer> _timers = [];
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _elapsedTicks;

        internal Task TimerCreated => _timerCreated.Task;
        internal List<TimeSpan> InitialDueTimes { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RecoveryTimer timer = new(this, callback, state);
            lock (_gate)
            {
                InitialDueTimes.Add(dueTime);
                _timers.Add(timer);
                _ = timer.Change(dueTime, period);
            }

            _ = _timerCreated.TrySetResult();
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            RecoveryTimer[] timers;
            lock (_gate)
            {
                _elapsedTicks += elapsed.Ticks;
                timers = [.. _timers];
            }

            foreach (RecoveryTimer timer in timers)
            {
                timer.FireWhenDue();
            }
        }

        public void Dispose()
        {
            foreach (RecoveryTimer timer in _timers)
            {
                timer.Dispose();
            }
        }

        private sealed class RecoveryTimer(CopyRecoveryClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long _dueTicks = long.MaxValue;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new NotSupportedException("The recovery clock expects one-shot timers.");
                }

                lock (owner._gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._elapsedTicks + dueTime.Ticks;
                    return true;
                }
            }

            internal void FireWhenDue()
            {
                lock (owner._gate)
                {
                    if (_disposed || _dueTicks > owner._elapsedTicks)
                    {
                        return;
                    }

                    _dueTicks = long.MaxValue;
                }

                callback(state);
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
