using System.Buffers.Binary;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Cntryl.Cassie.Protocol;
using Cntryl.Cassie.Types.Handlers;

namespace Cntryl.Cassie.Types;

internal static class CassieParameterCodec
{
    internal static byte[] EncodeTextParameter(object value, int oid)
    {
        var text = value switch
        {
            byte[] bytes => "\\x" + Convert.ToHexString(bytes),
            CassieArray array => EncodeTextArray(array, oid),
            Array array when array.Rank != 1 || array.GetLowerBound(0) != 0 => EncodeTextArray(array, oid),
            Array array when oid == 1003 => EncodeNameArray(array),
            Array array when HasNonFiniteFloat(array) => EncodeTextArray(array, oid),
            Array array => EncodeArrayParameter(array, oid),
            CassieVector vector when oid == CassieTypeMap.VectorParameterOid => VectorHandler.EncodeText(vector),
            CassieTemporalValue temporal => FormatTemporalText(temporal, oid),
            string stringValue => stringValue,
            bool booleanValue => booleanValue ? "true" : "false",
            _ when CassieTypeHandlers.Find(oid)?.EncodeText(oid, value) is { } encoded => encoded,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"Parameter type '{value.GetType().FullName}' has no text codec.")
        };
        return Encoding.UTF8.GetBytes(text);
    }

    private static string FormatTemporalText(CassieTemporalValue temporal, int oid)
    {
        return temporal.PostgresTypeOid != oid
            ? throw new ArgumentException("The temporal value does not match the PostgreSQL type OID.",
                nameof(temporal))
            : !temporal.HasRawValue
                ? temporal.TextValue ??
                  throw new ArgumentException("The temporal text value is missing.", nameof(temporal))
                : temporal.IsInfinity
                    ? temporal.Value < 0 ? "-infinity" : "infinity"
                    : temporal.Kind switch
                    {
                        CassieTemporalKind.Date => FormatExtendedDate(checked((int)temporal.Value)),
                        CassieTemporalKind.Time => FormatExtendedTime(temporal.Value),
                        CassieTemporalKind.TimeWithTimeZone => FormatExtendedTimeWithTimeZone(temporal.Value,
                            temporal.OffsetSeconds),
                        CassieTemporalKind.Timestamp => FormatExtendedTimestamp(temporal.Value),
                        CassieTemporalKind.TimestampWithTimeZone => FormatExtendedTimestamp(temporal.Value) + "+00:00",
                        _ => throw new ArgumentException("The temporal kind is invalid.", nameof(temporal))
                    };
    }

    private static byte[]? EncodeBinaryTemporal(CassieTemporalValue temporal)
    {
        if (!temporal.HasRawValue)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[sizeof(long)];

        switch (temporal.Kind)
        {
            case CassieTemporalKind.Date:
                BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)temporal.Value));
                return bytes[..sizeof(int)].ToArray();
            case CassieTemporalKind.Time:
            case CassieTemporalKind.Timestamp:
            case CassieTemporalKind.TimestampWithTimeZone:
                BinaryPrimitives.WriteInt64BigEndian(bytes, temporal.Value);
                return bytes.ToArray();
            case CassieTemporalKind.TimeWithTimeZone:
                BinaryPrimitives.WriteInt64BigEndian(bytes, temporal.Value);
                using (MemoryStream stream = new(12))
                {
                    stream.Write(bytes);
                    PgWriter.WriteInt32(stream, checked(-temporal.OffsetSeconds));
                    return stream.ToArray();
                }
            default:
                throw new ArgumentException("The temporal kind is invalid.", nameof(temporal));
        }
    }

    private static string FormatExtendedDate(int daysSincePostgresEpoch)
    {
        var daysSinceUnixEpoch = (long)daysSincePostgresEpoch + 10_957;
        var shiftedDays = daysSinceUnixEpoch + 719_468;
        var era = FloorDivide(shiftedDays, 146_097);
        var dayOfEra = shiftedDays - (era * 146_097);
        var yearOfEra = (dayOfEra - (dayOfEra / 1_460) + (dayOfEra / 36_524) - (dayOfEra / 146_096)) / 365;
        var year = yearOfEra + (era * 400);
        var dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
        var monthPrime = ((5 * dayOfYear) + 2) / 153;
        var day = dayOfYear - (((153 * monthPrime) + 2) / 5) + 1;
        var month = monthPrime + (monthPrime < 10 ? 3 : -9);
        year += month <= 2 ? 1 : 0;
        var displayYear = year > 0 ? year : 1 - year;
        return string.Concat(
            displayYear.ToString("0000", CultureInfo.InvariantCulture), "-",
            month.ToString("00", CultureInfo.InvariantCulture), "-",
            day.ToString("00", CultureInfo.InvariantCulture),
            year <= 0 ? " BC" : string.Empty);
    }

    private static string FormatExtendedTimestamp(long microsecondsSincePostgresEpoch)
    {
        const long microsecondsPerDay = 86_400_000_000;
        var days = FloorDivide(microsecondsSincePostgresEpoch, microsecondsPerDay);
        var time = microsecondsSincePostgresEpoch - (days * microsecondsPerDay);
        return string.Concat(FormatExtendedDate(checked((int)days)), " ", FormatExtendedTime(time));
    }

    private static string FormatExtendedTime(long microsecondsSinceMidnight)
    {
        if (microsecondsSinceMidnight is < 0 or > 86_400_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(microsecondsSinceMidnight),
                "TIME must be from midnight through 24:00.");
        }

        if (microsecondsSinceMidnight == 86_400_000_000)
        {
            return "24:00:00";
        }

        var hours = microsecondsSinceMidnight / 3_600_000_000;
        var minutes = microsecondsSinceMidnight / 60_000_000 % 60;
        var wholeSeconds = microsecondsSinceMidnight / 1_000_000 % 60;
        var fractionalMicroseconds = microsecondsSinceMidnight % 1_000_000;
        var clock = string.Concat(
            hours.ToString("00", CultureInfo.InvariantCulture), ":",
            minutes.ToString("00", CultureInfo.InvariantCulture), ":",
            wholeSeconds.ToString("00", CultureInfo.InvariantCulture));
        return fractionalMicroseconds == 0
            ? clock
            : clock + "." + fractionalMicroseconds.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    private static string FormatExtendedTimeWithTimeZone(long microsecondsSinceMidnight, int offsetSeconds)
    {
        var absoluteOffset = Math.Abs((long)offsetSeconds);
        var zone = string.Concat(
            offsetSeconds < 0 ? "-" : "+",
            (absoluteOffset / 3600).ToString("00", CultureInfo.InvariantCulture), ":",
            (absoluteOffset / 60 % 60).ToString("00", CultureInfo.InvariantCulture),
            absoluteOffset % 60 == 0
                ? string.Empty
                : ":" + (absoluteOffset % 60).ToString("00", CultureInfo.InvariantCulture));
        return FormatExtendedTime(microsecondsSinceMidnight) + zone;
    }

    private static long FloorDivide(long value, long divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    internal static byte[]? GetBinaryParameter(object? value, int oid)
    {
        if (oid == CassieTypeMap.VectorParameterOid && value is CassieVector vector)
        {
            return VectorHandler.EncodeBinary(vector);
        }

        if (value is null or DBNull)
        {
            return CassieTypeMap.SupportsBinaryNullParameter(oid) ? [] : null;
        }

        Span<byte> bytes = stackalloc byte[16];

        switch (oid, value)
        {
            case (1003, Array names):
                return EncodeBinaryNameArray(names);
            case (int arrayOid, CassieArray values) when CassieTypeMap.GetArrayElementOid(arrayOid) is int elementOid:
                if (values.ElementTypeOid != elementOid)
                {
                    throw new ArgumentException(
                        "The CassieArray element OID does not match the bound PostgreSQL array type.", nameof(value));
                }

                return EncodeBinaryArray(values, elementOid);
            case (int arrayOid, Array values) when CassieTypeMap.GetArrayElementOid(arrayOid) is int elementOid:
                return EncodeBinaryArray(values, elementOid);
            case (int temporalOid, CassieTemporalValue temporal) when temporal.PostgresTypeOid == temporalOid:
                return EncodeBinaryTemporal(temporal);
            default:
                return CassieTypeHandlers.Find(oid)?.EncodeBinary(oid, value);
        }
    }

    private static byte[] EncodeBinaryNameArray(Array values)
    {
        if (values.Rank != 1 || values.GetLowerBound(0) != 0)
        {
            throw new NotSupportedException("NAME[] parameters must be one-dimensional and zero-based.");
        }

        using MemoryStream stream = new();
        var count = values.Length;
        var hasNull = values.Cast<object?>().Any(static value => value is null or DBNull);
        PgWriter.WriteInt32(stream, count == 0 ? 0 : 1);
        PgWriter.WriteInt32(stream, hasNull ? 1 : 0);
        PgWriter.WriteInt32(stream, 19);

        if (count == 0)
        {
            return stream.ToArray();
        }


        PgWriter.WriteInt32(stream, count);
        PgWriter.WriteInt32(stream, 1);

        foreach (var item in values)
        {
            if (item is null or DBNull)
            {
                PgWriter.WriteInt32(stream, -1);
                continue;
            }

            if (item is not string name)
            {
                throw new ArgumentException("NAME[] parameters must contain only strings or null values.",
                    nameof(values));
            }

            var bytes = Encoding.UTF8.GetBytes(name);

            if (bytes.Length > 63)
            {
                throw new ArgumentOutOfRangeException(nameof(values),
                    "Binary NAME[] elements cannot exceed 63 UTF-8 bytes.");
            }


            PgWriter.WriteInt32(stream, bytes.Length);
            stream.Write(bytes);
        }


        return stream.ToArray();
    }

    private static byte[]? EncodeBinaryArray(object values, int elementOid)
    {
        int[] dimensions;
        int[] lowerBounds;
        IEnumerable<object?> elements;
        int count;

        if (values is CassieArray cassieArray)
        {
            if (cassieArray.ElementTypeOid != elementOid)
            {
                throw new ArgumentException("The CassieArray element OID does not match the PostgreSQL array type.",
                    nameof(values));
            }

            dimensions = [.. cassieArray.Dimensions];
            lowerBounds = [.. cassieArray.LowerBounds];
            elements = cassieArray.Elements;
            count = cassieArray.Count;
        }
        else if (values is Array array)
        {
            dimensions = [.. Enumerable.Range(0, array.Rank).Select(array.GetLength)];
            lowerBounds =
            [
                .. Enumerable.Range(0, array.Rank).Select(dimension =>
                    array.GetLowerBound(dimension) == 0 ? 1 : array.GetLowerBound(dimension))
            ];
            elements = array.Cast<object?>();
            count = array.Length;
        }
        else
        {
            throw new ArgumentException("The value must be a CLR array or CassieArray.", nameof(values));
        }


        if (dimensions.Length != lowerBounds.Length || (count > 0 && dimensions.Length == 0))
        {
            throw new ArgumentException("The array shape is invalid.", nameof(values));
        }

        if (dimensions.Length > 6)
        {
            throw new NotSupportedException("Cassie supports arrays with at most six dimensions.");
        }

        var hasNull = elements.Any(static value => value is null or DBNull);
        using MemoryStream stream = new();
        PgWriter.WriteInt32(stream, count == 0 ? 0 : dimensions.Length);
        PgWriter.WriteInt32(stream, hasNull ? 1 : 0);
        PgWriter.WriteInt32(stream, elementOid);

        if (count == 0)
        {
            return stream.ToArray();
        }


        for (var dimension = 0; dimension < dimensions.Length; dimension++)
        {
            PgWriter.WriteInt32(stream, dimensions[dimension]);
            PgWriter.WriteInt32(stream, lowerBounds[dimension]);
        }

        foreach (var value in elements)
        {
            if (value is null or DBNull)
            {
                PgWriter.WriteInt32(stream, -1);
                continue;
            }

            var elementBytes = GetBinaryParameter(value, elementOid);

            if (elementBytes is null)
            {
                return null;
            }


            PgWriter.WriteInt32(stream, elementBytes.Length);
            stream.Write(elementBytes);
        }

        return stream.ToArray();
    }

    private static string EncodeTextArray(object values, int arrayOid)
    {
        var expectedElementOid = CassieTypeMap.GetArrayElementOid(arrayOid);

        if (expectedElementOid is not int elementOid)
        {
            throw new NotSupportedException($"Text encoding for PostgreSQL array OID {arrayOid} is not supported.");
        }


        int[] dimensions;
        int[] lowerBounds;
        List<object?> elements;

        if (values is CassieArray cassieArray)
        {
            if (cassieArray.ElementTypeOid != elementOid)
            {
                throw new ArgumentException("The CassieArray element OID does not match the PostgreSQL array type.",
                    nameof(values));
            }

            dimensions = [.. cassieArray.Dimensions];
            lowerBounds = [.. cassieArray.LowerBounds];
            elements = [.. cassieArray.Elements];
        }
        else if (values is Array array)
        {
            dimensions = [.. Enumerable.Range(0, array.Rank).Select(array.GetLength)];
            lowerBounds =
            [
                .. Enumerable.Range(0, array.Rank).Select(dimension =>
                    array.GetLowerBound(dimension) == 0 ? 1 : array.GetLowerBound(dimension))
            ];
            elements = [.. array.Cast<object?>()];
        }
        else
        {
            throw new ArgumentException("The value must be a CLR array or CassieArray.", nameof(values));
        }


        if (dimensions.Length > 6)
        {
            throw new NotSupportedException("Cassie supports arrays with at most six dimensions.");
        }

        if (elements.Count == 0)
        {
            return "{}";
        }

        if (dimensions.Length > 0 &&
            dimensions.Aggregate(1L, static (product, dimension) => checked(product * dimension)) != elements.Count)
        {
            throw new ArgumentException("The flattened element count does not match the array dimensions.",
                nameof(values));
        }

        StringBuilder result = new();

        if (lowerBounds.Where((lowerBound, index) => lowerBound != 1).Any())
        {
            for (var index = 0; index < dimensions.Length; index++)
            {
                var upperBound = checked((int)((long)lowerBounds[index] + dimensions[index] - 1));
                _ = result.Append('[').Append(lowerBounds[index].ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(upperBound.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            _ = result.Append('=');
        }


        var flatIndex = 0;
        AppendArrayLevel(result, elements, dimensions, 0, ref flatIndex, elementOid);
        return result.ToString();
    }

    private static void AppendArrayLevel(
        StringBuilder output,
        IReadOnlyList<object?> elements,
        IReadOnlyList<int> dimensions,
        int depth,
        ref int flatIndex,
        int elementOid
    )
    {
        _ = output.Append('{');

        for (var index = 0; index < dimensions[depth]; index++)
        {
            if (index > 0)
            {
                _ = output.Append(',');
            }

            if (depth + 1 < dimensions.Count)
            {
                AppendArrayLevel(output, elements, dimensions, depth + 1, ref flatIndex, elementOid);
                continue;
            }

            var value = elements[flatIndex++];

            if (value is null or DBNull)
            {
                _ = output.Append("NULL");
                continue;
            }


            var text = Encoding.UTF8.GetString(EncodeTextParameter(value, elementOid));
            var quote = text.Length == 0 || text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                                         || text.Any(static character =>
                                             char.IsWhiteSpace(character) ||
                                             character is '{' or '}' or ',' or '"' or '\\');

            if (quote)
            {
                _ = output.Append('"');

                foreach (var character in text)
                {
                    if (character is '"' or '\\')
                    {
                        _ = output.Append('\\');
                    }

                    _ = output.Append(character);
                }


                _ = output.Append('"');
            }
            else
            {
                _ = output.Append(text);
            }
        }


        _ = output.Append('}');
    }

    private static bool HasNonFiniteFloat(Array array)
    {
        return array switch
        {
            float[] floats => floats.Any(static value => !float.IsFinite(value)),
            double[] doubles => doubles.Any(static value => !double.IsFinite(value)),
            float?[] floats => floats.Any(static value => value is { } v && !float.IsFinite(v)),
            double?[] doubles => doubles.Any(static value => value is { } v && !double.IsFinite(v)),
            _ => false
        };
    }

    private static string EncodeNameArray(Array values)
    {
        if (values.Rank != 1 || values.GetLowerBound(0) != 0)
        {
            throw new NotSupportedException("NAME[] parameters must be one-dimensional and zero-based.");
        }

        StringBuilder result = new("{");

        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                _ = result.Append(',');
            }

            var item = values.GetValue(index);

            if (item is null or DBNull)
            {
                _ = result.Append("NULL");
                continue;
            }


            if (item is not string name)
            {
                throw new ArgumentException("NAME[] parameters must contain only strings or null values.",
                    nameof(values));
            }

            _ = result.Append('"');

            foreach (var character in name)
            {
                if (character is '"' or '\\')
                {
                    _ = result.Append('\\');
                }

                _ = result.Append(character);
            }


            _ = result.Append('"');
        }


        return result.Append('}').ToString();
    }

    private static string EncodeArrayParameter(Array array, int arrayOid)
    {
        var elementOid = CassieTypeMap.GetArrayElementOid(arrayOid)
                         ?? throw new NotSupportedException(
                             $"Text encoding for PostgreSQL array OID {arrayOid} is not supported.");
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();

            foreach (var item in array)
            {
                if (item is null)
                {
                    writer.WriteNullValue();
                }
                else if (item is string stringValue)
                {
                    writer.WriteStringValue(stringValue);
                }
                else if (item is char character)
                {
                    writer.WriteStringValue(character.ToString());
                }
                else if (item is Guid guid)
                {
                    writer.WriteStringValue(guid);
                }
                else if (item is DateOnly date)
                {
                    writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
                else if (item is TimeOnly time)
                {
                    writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                }
                else if (item is DateTime dateTime)
                {
                    writer.WriteStringValue(elementOid == 1114
                        ? TimestampHandler.FormatTextTimestamp(dateTime)
                        : dateTime.ToString("O", CultureInfo.InvariantCulture));
                }
                else if (item is DateTimeOffset dateTimeOffset)
                {
                    writer.WriteStringValue(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                }
                else if (item is CassieNetworkAddress networkAddress)
                {
                    writer.WriteStringValue(
                        NetworkAddressHandler.EncodeTextNetworkAddress(networkAddress, elementOid == 650));
                }
                else if (item is PhysicalAddress macAddress)
                {
                    writer.WriteStringValue(
                        MacAddressHandler.FormatTextMacAddress(macAddress, elementOid == 774 ? 8 : 6));
                }
                else if (item is CassieBitString bitString)
                {
                    writer.WriteStringValue(BitStringHandler.EncodeTextBitString(bitString, elementOid == 1562));
                }
                else if (item is CassieLsn lsn)
                {
                    writer.WriteStringValue(lsn.ToString());
                }
                else if (item is CassieTimeWithTimeZone timeWithTimeZone)
                {
                    writer.WriteStringValue(TimeTzHandler.FormatTextTimeWithTimeZone(timeWithTimeZone));
                }
                else if (item is CassieTemporalValue temporal)
                {
                    writer.WriteStringValue(FormatTemporalText(temporal, elementOid));
                }
                else if (item is CassieInterval interval)
                {
                    writer.WriteStringValue(IntervalHandler.FormatTextInterval(interval));
                }
                else if (item is bool boolean)
                {
                    writer.WriteBooleanValue(boolean);
                }
                else if (item is IFormattable formattable)
                {
                    writer.WriteRawValue(formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null");
                }
                else
                {
                    throw new NotSupportedException(
                        $"Array element type '{item.GetType().FullName}' has no text codec.");
                }
            }


            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
