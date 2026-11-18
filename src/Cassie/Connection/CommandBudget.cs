using System.Diagnostics;

namespace Cntryl.Cassie.Connection;

/// <summary>One monotonic expiry shared by every wire phase of a command.</summary>
internal readonly struct CommandBudget(int seconds)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _duration = TimeSpan.FromSeconds(seconds);

    public bool HasTimeout => _duration > TimeSpan.Zero;

    public TimeSpan Remaining => HasTimeout
        ? TimeSpan.FromTicks(Math.Max((_duration - Stopwatch.GetElapsedTime(_started)).Ticks, 0))
        : Timeout.InfiniteTimeSpan;
}
