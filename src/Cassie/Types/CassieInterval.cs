namespace Cntryl.Cassie.Types
{
    /// <summary>A PostgreSQL interval retaining month, day, and microsecond components.</summary>
    /// <param name="Months">The calendar-month component.</param>
    /// <param name="Days">The calendar-day component.</param>
    /// <param name="Microseconds">The time component in microseconds.</param>
    public readonly record struct CassieInterval(int Months, int Days, long Microseconds);
}
