namespace Cntryl.Cassie.Types
{
    /// <summary>The Cassie temporal family represented by a <see cref="CassieTemporalValue"/>.</summary>
    public enum CassieTemporalKind
    {
        /// <summary>A date represented as signed days since 2000-01-01.</summary>
        Date,
        /// <summary>A time represented as signed microseconds since midnight.</summary>
        Time,
        /// <summary>A time with time zone represented as microseconds and an offset in seconds east of UTC.</summary>
        TimeWithTimeZone,
        /// <summary>A timestamp without time zone represented as signed microseconds since 2000-01-01.</summary>
        Timestamp,
        /// <summary>A timestamp with time zone represented as signed microseconds since 2000-01-01 UTC.</summary>
        TimestampWithTimeZone
    }
}
