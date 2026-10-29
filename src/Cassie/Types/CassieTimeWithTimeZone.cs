namespace Cntryl.Cassie.Types
{
    /// <summary>A PostgreSQL time-with-time-zone value without a date component.</summary>
    /// <param name="Time">The wall-clock time.</param>
    /// <param name="OffsetSeconds">The UTC offset in seconds east of UTC.</param>
    public readonly record struct CassieTimeWithTimeZone(TimeOnly Time, int OffsetSeconds);
}
