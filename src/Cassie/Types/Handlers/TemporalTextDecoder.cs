using System.Globalization;
using Cntryl.Cassie.Connection;

namespace Cntryl.Cassie.Types.Handlers;

/// <summary>
///     Parses the text form of date, time, and timestamp values, including the session date style and extended
///     calendar values.
/// </summary>
internal static class TemporalTextDecoder
{
    internal static object DecodeTextDate(string value, CassieDateStyle? dateStyle)
    {
        var trimmed = value.Trim();

        if (TryDecodeTemporalInfinity(trimmed, CassieTemporalKind.Date, out var infinity))
        {
            return infinity;
        }


        var style = dateStyle ?? CassieDateStyle.Default;
        return style.TryParseDate(trimmed, out var date)
            ? date
            : LooksLikeExtendedCalendarValue(trimmed)
              || (dateStyle is null && LooksLikeNonIsoTemporalText(trimmed))
                ? CassieTemporalValue.FromText(CassieTemporalKind.Date, value)
                : throw new FormatException("Cassie returned an invalid text date value.");
    }

    internal static object DecodeTextTime(string value)
    {
        var trimmed = value.Trim();
        return IsTwentyFourHourTime(trimmed)
            ? CassieTemporalValue.FromTimeMicroseconds(86_400_000_000)
            : TimeOnly.Parse(value, CultureInfo.InvariantCulture);
    }

    internal static object DecodeTextTimestamp(string value, CassieTemporalKind kind, CassieDateStyle? dateStyle)
    {
        var trimmed = value.Trim();

        if (TryDecodeTemporalInfinity(trimmed, kind, out var infinity))
        {
            return infinity;
        }


        var style = dateStyle ?? CassieDateStyle.Default;
        return TryParseTextTimestamp(trimmed, style, out var timestamp)
            ? timestamp
            : LooksLikeExtendedCalendarValue(trimmed)
              || (dateStyle is null && LooksLikeNonIsoTemporalText(trimmed))
                ? CassieTemporalValue.FromText(kind, value)
                : throw new FormatException("Cassie returned an invalid text timestamp value.");
    }

    internal static object DecodeTextTimestampTz(string value, CassieDateStyle? dateStyle)
    {
        var trimmed = value.Trim();

        if (TryDecodeTemporalInfinity(trimmed, CassieTemporalKind.TimestampWithTimeZone, out var infinity))
        {
            return infinity;
        }


        var style = dateStyle ?? CassieDateStyle.Default;
        return TryParseTextTimestampWithTimeZone(trimmed, style, out var timestamp)
            ? timestamp
            : LooksLikeExtendedCalendarValue(trimmed)
              || (dateStyle is null && LooksLikeNonIsoTemporalText(trimmed))
              || HasNamedTimeZone(trimmed, style)
                ? CassieTemporalValue.FromText(CassieTemporalKind.TimestampWithTimeZone, value)
                : throw new FormatException("Cassie returned an invalid text timestamp-with-time-zone value.");
    }

    private static bool TryParseTextTimestamp(string value, CassieDateStyle dateStyle, out DateTime timestamp)
    {
        if (dateStyle.Output == CassieDateOutput.Postgres)
        {
            return DateTime.TryParseExact(value, dateStyle.TimestampFormats(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out timestamp);
        }

        var separator = value.IndexOf(' ', StringComparison.Ordinal);

        if (separator <= 0 || !dateStyle.TryParseDate(value[..separator], out var date)
                           || !TimeOnly.TryParse(value[(separator + 1)..], CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out var time))
        {
            timestamp = default;
            return false;
        }


        timestamp = date.ToDateTime(time, DateTimeKind.Unspecified);
        return true;
    }

    private static bool TryParseTextTimestampWithTimeZone(
        string value,
        CassieDateStyle dateStyle,
        out DateTimeOffset timestamp
    )
    {
        var timestampText = value;
        int offsetSeconds;

        if (dateStyle.Output == CassieDateOutput.Postgres)
        {
            var zoneSeparator = timestampText.LastIndexOf(' ', StringComparison.Ordinal);

            if (zoneSeparator <= 0 ||
                !TryParseTextOffsetSeconds(timestampText.AsSpan(zoneSeparator + 1), out offsetSeconds))
            {
                timestamp = default;
                return false;
            }


            timestampText = timestampText[..zoneSeparator];

            if (!DateTime.TryParseExact(timestampText, dateStyle.TimestampFormats(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var postgresTimestamp))
            {
                timestamp = default;
                return false;
            }


            return TryCreateDateTimeOffset(postgresTimestamp, offsetSeconds, out timestamp);
        }


        var dateSeparator = timestampText.IndexOf(' ', StringComparison.Ordinal);

        if (dateSeparator <= 0 || !dateStyle.TryParseDate(timestampText[..dateSeparator], out var date))
        {
            timestamp = default;
            return false;
        }


        var clockAndOffset = timestampText.AsSpan(dateSeparator + 1);
        var offsetStart = -1;

        for (var index = 1; index < clockAndOffset.Length; index++)
        {
            if (clockAndOffset[index] is '+' or '-')
            {
                offsetStart = index;
            }
        }


        if (offsetStart <= 0 || !TryParseTextOffsetSeconds(clockAndOffset[offsetStart..], out offsetSeconds)
                             || !TimeOnly.TryParse(clockAndOffset[..offsetStart], CultureInfo.InvariantCulture,
                                 DateTimeStyles.None, out var time))
        {
            timestamp = default;
            return false;
        }

        return TryCreateDateTimeOffset(date.ToDateTime(time, DateTimeKind.Unspecified), offsetSeconds, out timestamp);
    }

    private static bool TryCreateDateTimeOffset(DateTime timestamp, int offsetSeconds, out DateTimeOffset result)
    {
        if (offsetSeconds % 60 != 0)
        {
            result = default;
            return false;
        }

        try
        {
            result = new DateTimeOffset(timestamp, TimeSpan.FromSeconds(offsetSeconds));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }

    private static bool HasNamedTimeZone(string value, CassieDateStyle dateStyle)
    {
        if (dateStyle.Output == CassieDateOutput.Postgres)
        {
            var separator = value.LastIndexOf(' ', StringComparison.Ordinal);
            return separator > 0 && separator < value.Length - 1 && char.IsLetter(value[separator + 1]);
        }

        var timeStart = value.IndexOf(' ', StringComparison.Ordinal);

        if (timeStart < 0)
        {
            return false;
        }


        var timeAndZone = value.AsSpan(timeStart + 1);
        var zoneStart = timeAndZone.IndexOf(' ');

        if (zoneStart < 0)
        {
            return false;
        }


        var zone = timeAndZone[(zoneStart + 1)..].TrimStart();
        return !zone.IsEmpty && char.IsLetter(zone[0]);
    }

    private static bool LooksLikeNonIsoTemporalText(string value)
    {
        var separator = value.IndexOf(' ', StringComparison.Ordinal);
        var date = separator < 0 ? value.AsSpan() : value.AsSpan(0, separator);
        return date.Contains('/') || date.Contains('.') || (!date.IsEmpty && char.IsLetter(date[0]));
    }

    private static bool TryParseTextOffsetSeconds(ReadOnlySpan<char> value, out int offsetSeconds)
    {
        try
        {
            var parsed = ParseTextOffsetSeconds(value);

            if (parsed is < int.MinValue or > int.MaxValue)
            {
                offsetSeconds = 0;
                return false;
            }


            offsetSeconds = (int)parsed;
            return true;
        }
        catch (FormatException)
        {
            offsetSeconds = 0;
            return false;
        }
        catch (OverflowException)
        {
            offsetSeconds = 0;
            return false;
        }
    }

    internal static object DecodeTextTimeWithTimeZone(string value)
    {
        var trimmed = value.Trim();
        var zoneStart = Math.Max(trimmed.LastIndexOf('+', StringComparison.Ordinal),
            trimmed.LastIndexOf('-', StringComparison.Ordinal));

        if (zoneStart <= 0)
        {
            throw new FormatException("Cassie returned a time-with-time-zone value without an offset.");
        }


        var offsetSeconds = ParseTextOffsetSeconds(trimmed.AsSpan(zoneStart));
        var timeText = trimmed[..zoneStart];
        var isTwentyFour = IsTwentyFourHourTime(timeText);

        if (isTwentyFour)
        {
            return CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(86_400_000_000, checked((int)offsetSeconds));
        }


        var time = TimeOnly.Parse(timeText, CultureInfo.InvariantCulture);
        return new CassieTimeWithTimeZone(time, checked((int)offsetSeconds));
    }

    private static long ParseTextOffsetSeconds(ReadOnlySpan<char> value)
    {
        if (value.Length < 3 || value[0] is not ('+' or '-'))
        {
            throw new FormatException("Cassie returned an invalid time-zone offset.");
        }

        var sign = value[0] == '-' ? -1 : 1;
        var components = value[1..];
        var firstColon = components.IndexOf(':');
        var hoursText = firstColon < 0 ? components : components[..firstColon];

        if (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
        {
            throw new FormatException("Cassie returned an invalid time-zone offset.");
        }


        var minutes = 0;
        var seconds = 0;

        if (firstColon >= 0)
        {
            var remaining = components[(firstColon + 1)..];
            var secondColon = remaining.IndexOf(':');
            var minutesText = secondColon < 0 ? remaining : remaining[..secondColon];

            if (!int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            {
                throw new FormatException("Cassie returned an invalid time-zone offset.");
            }


            if (secondColon >= 0 && !int.TryParse(remaining[(secondColon + 1)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out seconds))
            {
                throw new FormatException("Cassie returned an invalid time-zone offset.");
            }
        }


        return checked(sign * (((long)hours * 3600) + ((long)minutes * 60) + seconds));
    }

    private static bool TryDecodeTemporalInfinity(
        string value,
        CassieTemporalKind kind,
        out CassieTemporalValue temporal
    )
    {
        long? sentinel = value switch
        {
            "infinity" or "+infinity" or "Infinity" or "+Infinity" => kind == CassieTemporalKind.Date
                ? int.MaxValue
                : long.MaxValue,
            "-infinity" or "-Infinity" => kind == CassieTemporalKind.Date ? int.MinValue : long.MinValue,
            _ => null
        };

        if (sentinel is not long rawValue || kind is not (CassieTemporalKind.Date or CassieTemporalKind.Timestamp
                or CassieTemporalKind.TimestampWithTimeZone))
        {
            temporal = default;
            return false;
        }


        temporal = kind switch
        {
            CassieTemporalKind.Date => CassieTemporalValue.FromDateDays(checked((int)rawValue)),
            CassieTemporalKind.Timestamp => CassieTemporalValue.FromTimestampMicroseconds(rawValue),
            _ => CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(rawValue)
        };
        return true;
    }

    private static bool LooksLikeExtendedCalendarValue(string value)
    {
        if (value.Contains(" BC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var consecutiveDigits = 0;

        foreach (var character in value)
        {
            consecutiveDigits = char.IsAsciiDigit(character) ? consecutiveDigits + 1 : 0;

            if (consecutiveDigits >= 5)
            {
                return true;
            }
        }


        return false;
    }

    private static bool IsTwentyFourHourTime(string value)
    {
        if (!value.StartsWith("24:00:00", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = value.AsSpan("24:00:00".Length);

        if (suffix.IsEmpty)
        {
            return true;
        }


        if (suffix[0] != '.')
        {
            return false;
        }

        foreach (var digit in suffix[1..])
        {
            if (digit != '0')
            {
                return false;
            }
        }

        return true;
    }
}
