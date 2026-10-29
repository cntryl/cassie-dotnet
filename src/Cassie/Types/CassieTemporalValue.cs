using System.Globalization;

namespace Cntryl.Cassie.Types
{
    /// <summary>A temporal value that does not fit the corresponding .NET date or time type.</summary>
    /// <remarks>
    /// Cassie temporal infinity sentinels and finite values outside the .NET calendar range use this tagged
    /// representation. Ordinary values continue to use <see cref="DateOnly"/>, <see cref="TimeOnly"/>,
    /// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, and <see cref="CassieTimeWithTimeZone"/>.
    /// For text results that are valid Cassie output but cannot be represented as a raw .NET-compatible value,
    /// <see cref="TextValue"/> preserves the original server text exactly.
    /// </remarks>
    public readonly record struct CassieTemporalValue
    {
        private CassieTemporalValue(CassieTemporalKind kind, long value, int offsetSeconds, string? textValue, bool hasRawValue)
        {
            Kind = kind;
            Value = value;
            OffsetSeconds = offsetSeconds;
            TextValue = textValue;
            HasRawValue = hasRawValue;
        }

        /// <summary>Gets the represented temporal family.</summary>
        public CassieTemporalKind Kind { get; }

        /// <summary>Gets the signed raw day or microsecond value when <see cref="HasRawValue"/> is true.</summary>
        public long Value { get; }

        /// <summary>Gets the offset in seconds east of UTC for time-with-time-zone values.</summary>
        public int OffsetSeconds { get; }

        /// <summary>Gets the original text for an out-of-range value returned in text format.</summary>
        public string? TextValue { get; }

        /// <summary>Gets whether <see cref="Value"/> contains the raw numeric representation.</summary>
        public bool HasRawValue { get; }

        /// <summary>Gets the PostgreSQL wire type OID for this value.</summary>
        public int PostgresTypeOid => Kind switch
        {
            CassieTemporalKind.Date => 1082,
            CassieTemporalKind.Time => 1083,
            CassieTemporalKind.TimeWithTimeZone => 1266,
            CassieTemporalKind.Timestamp => 1114,
            CassieTemporalKind.TimestampWithTimeZone => 1184,
            _ => throw new InvalidOperationException("The temporal kind is invalid.")
        };

        /// <summary>Gets whether this raw date or timestamp value is an infinity sentinel.</summary>
        public bool IsInfinity => HasRawValue && Kind switch
        {
            CassieTemporalKind.Date => Value is int.MinValue or int.MaxValue,
            CassieTemporalKind.Timestamp or CassieTemporalKind.TimestampWithTimeZone => Value is long.MinValue or long.MaxValue,
            _ => false
        };

        /// <summary>Creates a date from its signed day count since 2000-01-01.</summary>
        public static CassieTemporalValue FromDateDays(int daysSincePostgresEpoch)
        {
            return new CassieTemporalValue(CassieTemporalKind.Date, daysSincePostgresEpoch, 0, null, hasRawValue: true);
        }

        /// <summary>Creates a time from its signed microsecond count since midnight.</summary>
        public static CassieTemporalValue FromTimeMicroseconds(long microsecondsSinceMidnight)
        {
            return new CassieTemporalValue(CassieTemporalKind.Time, microsecondsSinceMidnight, 0, null, hasRawValue: true);
        }

        /// <summary>Creates a time-with-time-zone from microseconds since midnight and an offset east of UTC.</summary>
        public static CassieTemporalValue FromTimeWithTimeZoneMicroseconds(long microsecondsSinceMidnight, int offsetSeconds)
        {
            return new CassieTemporalValue(CassieTemporalKind.TimeWithTimeZone, microsecondsSinceMidnight, offsetSeconds, null, hasRawValue: true);
        }

        /// <summary>Creates a timestamp without time zone from microseconds since 2000-01-01.</summary>
        public static CassieTemporalValue FromTimestampMicroseconds(long microsecondsSincePostgresEpoch)
        {
            return new CassieTemporalValue(CassieTemporalKind.Timestamp, microsecondsSincePostgresEpoch, 0, null, hasRawValue: true);
        }

        /// <summary>Creates a timestamp with time zone from UTC microseconds since 2000-01-01.</summary>
        public static CassieTemporalValue FromTimestampWithTimeZoneMicroseconds(long microsecondsSincePostgresEpoch)
        {
            return new CassieTemporalValue(CassieTemporalKind.TimestampWithTimeZone, microsecondsSincePostgresEpoch, 0, null, hasRawValue: true);
        }

        /// <summary>Creates a text-preserving tagged temporal value for an explicit PostgreSQL type.</summary>
        public static CassieTemporalValue FromText(CassieTemporalKind kind, string textValue)
        {
            ArgumentNullException.ThrowIfNull(textValue);
            return Enum.IsDefined(kind)
                ? new CassieTemporalValue(kind, 0, 0, textValue, hasRawValue: false)
                : throw new ArgumentOutOfRangeException(nameof(kind));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return TextValue ?? (IsInfinity
                ? Value < 0 ? "-infinity" : "infinity"
                : string.Concat(Kind.ToString(), "(", Value.ToString(CultureInfo.InvariantCulture), ")"));
        }
    }
}
