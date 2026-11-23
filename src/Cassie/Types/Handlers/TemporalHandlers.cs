using System.Buffers.Binary;
using System.Globalization;
using Cntryl.Cassie.Connection;

namespace Cntryl.Cassie.Types.Handlers;

internal sealed class DateHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return TemporalTextDecoder.DecodeTextDate(value, dateStyle);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(int) ? DecodeBinaryDate(value) : null;
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is DateTime dateTime
            ? DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        DateOnly? date = value switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => null
        };

        if (date is not { } found)
        {
            return null;
        }


        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, found.DayNumber - new DateOnly(2000, 1, 1).DayNumber);
        return bytes;
    }

    private static object DecodeBinaryDate(ReadOnlySpan<byte> bytes)
    {
        return TryReadBinaryDate(bytes, out var date)
            ? date
            : CassieTemporalValue.FromDateDays(BinaryPrimitives.ReadInt32BigEndian(bytes));
    }

    /// <summary>Reads a binary date as a <see cref="DateOnly" /> when it lies within the CLR range.</summary>
    internal static bool TryReadBinaryDate(ReadOnlySpan<byte> bytes, out DateOnly value)
    {
        var days = BinaryPrimitives.ReadInt32BigEndian(bytes);
        var dayNumber = (long)new DateOnly(2000, 1, 1).DayNumber + days;

        if (days is int.MinValue or int.MaxValue || dayNumber is < 0 or > 3_652_058)
        {
            value = default;
            return false;
        }


        value = DateOnly.FromDayNumber((int)dayNumber);
        return true;
    }
}

internal sealed class TimeHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return TemporalTextDecoder.DecodeTextTime(value);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(long) ? DecodeBinaryTime(value) : null;
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is TimeSpan timeSpan ? FormatTimeSpan(timeSpan) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        long? microseconds = value switch
        {
            TimeOnly time => time.Ticks / 10,
            TimeSpan time => GetTimeMicroseconds(time),
            _ => null
        };

        if (microseconds is not { } found)
        {
            return null;
        }


        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, found);
        return bytes;
    }

    private static object DecodeBinaryTime(ReadOnlySpan<byte> bytes)
    {
        var microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
        return microseconds is < 0 or > 86_400_000_000
            ? throw new FormatException("Cassie returned an invalid binary time value.")
            : microseconds == 86_400_000_000
                ? CassieTemporalValue.FromTimeMicroseconds(microseconds)
                : new TimeOnly(microseconds * 10);
    }

    private static string FormatTimeSpan(TimeSpan value)
    {
        var microseconds = GetTimeMicroseconds(value);
        return new TimeOnly(microseconds * 10)
            .ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.');
    }

    private static long GetTimeMicroseconds(TimeSpan value)
    {
        return value.Ticks is >= 0 and < TimeSpan.TicksPerDay
            ? value.Ticks / 10
            : throw new ArgumentOutOfRangeException(nameof(value),
                "TIME parameters must be from midnight through the final microsecond before 24:00.");
    }
}

internal sealed class TimestampHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return TemporalTextDecoder.DecodeTextTimestamp(value, CassieTemporalKind.Timestamp, dateStyle);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(long) ? DecodeBinaryTimestamp(value) : null;
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is DateTime dateTime ? FormatTextTimestamp(dateTime) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not DateTime timestamp)
        {
            return null;
        }

        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, (timestamp.Ticks - new DateTime(2000, 1, 1).Ticks) / 10);
        return bytes;
    }

    private static object DecodeBinaryTimestamp(ReadOnlySpan<byte> bytes)
    {
        return TryReadBinaryTimestamp(bytes, out var timestamp)
            ? timestamp
            : CassieTemporalValue.FromTimestampMicroseconds(BinaryPrimitives.ReadInt64BigEndian(bytes));
    }

    internal static bool TryReadBinaryTimestamp(ReadOnlySpan<byte> bytes, out DateTime value)
    {
        var microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var epochTicks = new DateTime(2000, 1, 1).Ticks;
        var minimumMicroseconds = (DateTime.MinValue.Ticks - epochTicks) / 10;
        var maximumMicroseconds = (DateTime.MaxValue.Ticks - epochTicks) / 10;

        if (microseconds is long.MinValue or long.MaxValue || microseconds < minimumMicroseconds ||
            microseconds > maximumMicroseconds)
        {
            value = default;
            return false;
        }


        value = new DateTime(epochTicks + (microseconds * 10), DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Reads a binary timestamptz as a UTC <see cref="DateTime" /> when it lies within the CLR range.</summary>
    internal static string FormatTextTimestamp(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }
}

internal sealed class TimestampTzHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return TemporalTextDecoder.DecodeTextTimestampTz(value, dateStyle);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(long) ? DecodeBinaryTimestampTz(value) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not DateTimeOffset timestampTz)
        {
            return null;
        }

        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, (timestampTz.UtcTicks - new DateTime(2000, 1, 1).Ticks) / 10);
        return bytes;
    }

    private static object DecodeBinaryTimestampTz(ReadOnlySpan<byte> bytes)
    {
        return TryReadBinaryTimestampTz(bytes, out var timestamp)
            ? new DateTimeOffset(timestamp)
            : CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(BinaryPrimitives.ReadInt64BigEndian(bytes));
    }

    /// <summary>Reads a binary timestamp as an unspecified-kind <see cref="DateTime" /> when it lies within the CLR range.</summary>
    internal static bool TryReadBinaryTimestampTz(ReadOnlySpan<byte> bytes, out DateTime value)
    {
        var microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var epochTicks = new DateTime(2000, 1, 1).Ticks;
        var minimumMicroseconds = (DateTimeOffset.MinValue.UtcTicks - epochTicks) / 10;
        var maximumMicroseconds = (DateTimeOffset.MaxValue.UtcTicks - epochTicks) / 10;

        if (microseconds is long.MinValue or long.MaxValue || microseconds < minimumMicroseconds ||
            microseconds > maximumMicroseconds)
        {
            value = default;
            return false;
        }


        value = new DateTime(epochTicks + (microseconds * 10), DateTimeKind.Utc);
        return true;
    }
}

internal sealed class TimeTzHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return TemporalTextDecoder.DecodeTextTimeWithTimeZone(value);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == 12 ? DecodeBinaryTimeTz(value) : null;
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is CassieTimeWithTimeZone timeTz ? FormatTextTimeWithTimeZone(timeTz) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not CassieTimeWithTimeZone timeTz)
        {
            return null;
        }

        var bytes = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(bytes, timeTz.Time.Ticks / 10);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), checked(-timeTz.OffsetSeconds));
        return bytes;
    }

    private static object DecodeBinaryTimeTz(ReadOnlySpan<byte> bytes)
    {
        var microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes[..8]);
        var zoneWest = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
        var offsetSeconds = zoneWest == int.MinValue
            ? throw new FormatException("Cassie returned an invalid binary time-zone offset.")
            : -zoneWest;
        return microseconds is < 0 or > 86_400_000_000
            ? throw new FormatException("Cassie returned an invalid binary time-with-time-zone value.")
            : microseconds == 86_400_000_000
                ? CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(microseconds, offsetSeconds)
                : new CassieTimeWithTimeZone(new TimeOnly(microseconds * 10), offsetSeconds);
    }

    internal static string FormatTextTimeWithTimeZone(CassieTimeWithTimeZone value)
    {
        var time = new TimeOnly(value.Time.Ticks / 10 * 10)
            .ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            .TrimEnd('0')
            .TrimEnd('.');
        long offset = value.OffsetSeconds;
        var absoluteOffset = Math.Abs(offset);
        var offsetText = string.Concat(
            offset < 0 ? "-" : "+",
            (absoluteOffset / 3600).ToString("00", CultureInfo.InvariantCulture),
            ":",
            (absoluteOffset / 60 % 60).ToString("00", CultureInfo.InvariantCulture),
            absoluteOffset % 60 == 0
                ? string.Empty
                : ":" + (absoluteOffset % 60).ToString("00", CultureInfo.InvariantCulture));
        return time + offsetText;
    }
}

internal sealed class IntervalHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == 16 ? DecodeBinaryInterval(value) : null;
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is CassieInterval interval ? FormatTextInterval(interval) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not CassieInterval interval)
        {
            return null;
        }

        var bytes = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, interval.Microseconds);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), interval.Days);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), interval.Months);
        return bytes;
    }

    private static CassieInterval DecodeBinaryInterval(ReadOnlySpan<byte> bytes)
    {
        var microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var days = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
        var months = BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
        return new CassieInterval(months, days, microseconds);
    }

    internal static string FormatTextInterval(CassieInterval value)
    {
        return (value.Months, value.Days, value.Microseconds) switch
        {
            (int.MaxValue, int.MaxValue, long.MaxValue) => "infinity",
            (int.MinValue, int.MinValue, long.MinValue) => "-infinity",
            _ => string.Concat(
                value.Months.ToString(CultureInfo.InvariantCulture), " months ",
                value.Days.ToString(CultureInfo.InvariantCulture), " days ",
                value.Microseconds.ToString(CultureInfo.InvariantCulture), " microseconds")
        };
    }
}
