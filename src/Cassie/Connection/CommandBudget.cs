namespace Cntryl.Cassie.Connection;

/// <summary>One monotonic expiry shared by every wire phase of a command.</summary>
internal readonly struct CommandBudget(int seconds, TimeProvider clock)
{
    private readonly long _started = seconds > 0 ? clock.GetTimestamp() : 0;
    private TimeSpan Duration { get; } = TimeSpan.FromSeconds(seconds);

    public TimeProvider Clock { get; } = clock;

    public TimeSpan Remaining => Duration > TimeSpan.Zero
        ? TimeSpan.FromTicks(Math.Max((Duration - Clock.GetElapsedTime(_started)).Ticks, 0))
        : Timeout.InfiniteTimeSpan;
}
