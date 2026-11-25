using System.Threading.Channels;

namespace Cntryl.Cassie.Tests.Connection;

/// <summary>A manually advanced clock for provider deadlines.</summary>
internal sealed class DeadlineClock : TimeProvider, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Channel<TimeSpan> _scheduled = Channel.CreateUnbounded<TimeSpan>();
    private readonly List<RecoveryTimer> _timers = [];
    private long _elapsedTicks;

    internal List<TimeSpan> InitialDueTimes { get; } = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _elapsedTicks;
        }
    }

    internal async Task WaitForTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        while (await _scheduled.Reader.ReadAsync(cancellationToken) != duration)
        {
        }
    }

    public void Dispose()
    {
        RecoveryTimer[] timers;

        lock (_gate)
        {
            timers = [.. _timers];
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        _ = _scheduled.Writer.TryComplete();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        RecoveryTimer timer = new(this, callback, state);
        lock (_gate)
        {
            InitialDueTimes.Add(dueTime);
            _timers.Add(timer);
            _ = timer.Change(dueTime, period);
        }

        _ = _scheduled.Writer.TryWrite(dueTime);
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

        foreach (var timer in timers)
        {
            timer.FireWhenDue();
        }
    }

    private sealed class RecoveryTimer(DeadlineClock owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        private long _dueTicks = long.MaxValue;

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
    }
}
