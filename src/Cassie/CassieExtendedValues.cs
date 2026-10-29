using System.Globalization;
using System.Collections.ObjectModel;
using System.Net;

namespace Cntryl.Cassie
{
    /// <summary>A Cassie inet or cidr value with its original prefix length.</summary>
    public readonly record struct CassieNetworkAddress
    {
        /// <summary>Creates a network address value.</summary>
        public CassieNetworkAddress(IPAddress address, int prefixLength, bool isCidr)
        {
            ArgumentNullException.ThrowIfNull(address);
            int width = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32
                : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128
                : throw new ArgumentException("Only IPv4 and IPv6 addresses are supported.", nameof(address));
            if ((uint)prefixLength > (uint)width)
            {
                throw new ArgumentOutOfRangeException(nameof(prefixLength));
            }

            Address = address;
            PrefixLength = prefixLength;
            IsCidr = isCidr;
        }

        /// <summary>Gets the IPv4 or IPv6 address.</summary>
        public IPAddress Address { get; }

        /// <summary>Gets the prefix length.</summary>
        public int PrefixLength { get; }

        /// <summary>Gets whether the value has the PostgreSQL cidr identity.</summary>
        public bool IsCidr { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Address}/{PrefixLength}";
        }
    }

    /// <summary>A PostgreSQL bit or varbit value, retaining its exact bit length.</summary>
    public readonly record struct CassieBitString
    {
        /// <summary>Creates a bit string.</summary>
        public CassieBitString(string value, bool isVarying)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.All(static bit => bit is '0' or '1'))
            {
                throw new ArgumentException("A bit string can contain only '0' and '1'.", nameof(value));
            }

            Value = value;
            IsVarying = isVarying;
        }

        /// <summary>Gets the bits in network order.</summary>
        public string Value { get; }

        /// <summary>Gets whether the source type is VARBIT rather than BIT.</summary>
        public bool IsVarying { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Value ?? string.Empty;
        }
    }

    /// <summary>A PostgreSQL write-ahead log position.</summary>
    public readonly record struct CassieLsn(ulong Value)
    {
        /// <summary>Gets the upper 32-bit hexadecimal word.</summary>
        public uint High => (uint)(Value >> 32);

        /// <summary>Gets the lower 32-bit hexadecimal word.</summary>
        public uint Low => (uint)Value;

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Concat(High.ToString("X", CultureInfo.InvariantCulture), "/", Low.ToString("X", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A PostgreSQL time-with-time-zone value without a date component.</summary>
    /// <param name="Time">The wall-clock time.</param>
    /// <param name="OffsetSeconds">The UTC offset in seconds east of UTC.</param>
    public readonly record struct CassieTimeWithTimeZone(TimeOnly Time, int OffsetSeconds);

    /// <summary>A PostgreSQL interval retaining month, day, and microsecond components.</summary>
    /// <param name="Months">The calendar-month component.</param>
    /// <param name="Days">The calendar-day component.</param>
    /// <param name="Microseconds">The time component in microseconds.</param>
    public readonly record struct CassieInterval(int Months, int Days, long Microseconds);

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

    /// <summary>A Cassie vector with a fixed dimension and finite FLOAT4 components.</summary>
    public sealed class CassieVector : IReadOnlyList<float>
    {
        private readonly float[] _values;
        private readonly ReadOnlyCollection<float> _readOnlyValues;

        /// <summary>Creates a vector from its components.</summary>
        /// <param name="values">One through 32767 finite FLOAT4 values.</param>
        public CassieVector(params float[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Length is < 1 or > short.MaxValue || values.Any(static value => !float.IsFinite(value)))
            {
                throw new ArgumentException("A Cassie vector requires one through 32767 finite FLOAT4 components.", nameof(values));
            }

            _values = [.. values];
            _readOnlyValues = Array.AsReadOnly(_values);
        }

        /// <summary>Gets the vector dimension.</summary>
        public int Dimension => _values.Length;

        /// <inheritdoc />
        public int Count => _values.Length;

        /// <inheritdoc />
        public float this[int index] => _values[index];

        /// <summary>Gets the components in order.</summary>
        public IReadOnlyList<float> Values => _readOnlyValues;

        /// <summary>Creates a copy of the components.</summary>
        public float[] ToArray()
        {
            return [.. _values];
        }

        /// <inheritdoc />
        public IEnumerator<float> GetEnumerator()
        {
            return ((IEnumerable<float>)_readOnlyValues).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
