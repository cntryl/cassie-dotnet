using System.Buffers.Binary;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Cntryl.Cassie
{
    internal static class CassieParameterCodec
    {
        internal static byte[] EncodeTextParameter(object value, int oid)
        {
            string text = value switch
            {
                byte[] bytes => "\\x" + Convert.ToHexString(bytes),
                CassieArray array => EncodeTextArray(array, oid),
                Array array when array.Rank != 1 || array.GetLowerBound(0) != 0 => EncodeTextArray(array, oid),
                Array array when oid == 1003 => EncodeNameArray(array),
                Array array when HasNonFiniteFloat(array) => EncodeTextArray(array, oid),
                Array array => EncodeArrayParameter(array, oid),
                CassieTemporalValue temporal => FormatTemporalText(temporal, oid),
                CassieNetworkAddress networkAddress when oid is 650 or 869 => EncodeTextNetworkAddress(networkAddress, oid == 650),
                PhysicalAddress macAddress when oid is 774 or 829 => FormatTextMacAddress(macAddress, oid == 774 ? 8 : 6),
                CassieBitString bitString when oid is 1560 or 1562 => EncodeTextBitString(bitString, oid == 1562),
                CassieLsn lsn when oid == 3220 => lsn.ToString(),
                CassieTimeWithTimeZone timeWithTimeZone when oid == 1266 => FormatTextTimeWithTimeZone(timeWithTimeZone),
                CassieInterval interval when oid == 1186 => FormatTextInterval(interval),
                CassieVector vector when CassieTypeMap.IsVectorOid(oid) => FormatTextVector(vector, oid),
                string stringValue => stringValue,
                bool booleanValue => booleanValue ? "true" : "false",
                DateTime dateTime when oid == 1082 => DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime dateTime when oid == 1114 => FormatTextTimestamp(dateTime),
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeSpan timeSpan when oid == 1083 => FormatTimeSpan(timeSpan),
                TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => throw new NotSupportedException($"Parameter type '{value.GetType().FullName}' has no text codec.")
            };
            return Encoding.UTF8.GetBytes(text);
        }

        private static string EncodeTextNetworkAddress(CassieNetworkAddress address, bool expectedCidr)
        {
            return address.Address is null || address.IsCidr != expectedCidr
                || (address.Address.AddressFamily == AddressFamily.InterNetworkV6 && address.Address.ScopeId != 0)
                ? throw new ArgumentException("The network address value does not match the PostgreSQL type OID.", nameof(address))
                : address.ToString();
        }

        private static string FormatTextMacAddress(PhysicalAddress address, int expectedLength)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == expectedLength
                ? string.Join(':', bytes.Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)))
                : throw new ArgumentException("The MAC address length does not match the PostgreSQL type OID.", nameof(address));
        }

        private static string EncodeTextBitString(CassieBitString bitString, bool expectedVarying)
        {
            return bitString.Value is null || bitString.IsVarying != expectedVarying
                ? throw new ArgumentException("The bit string value does not match the PostgreSQL type OID.", nameof(bitString))
                : bitString.Value;
        }

        private static string FormatTextTimeWithTimeZone(CassieTimeWithTimeZone value)
        {
            string time = new TimeOnly(value.Time.Ticks / 10 * 10)
                .ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                .TrimEnd('0')
                .TrimEnd('.');
            long offset = value.OffsetSeconds;
            long absoluteOffset = Math.Abs(offset);
            string offsetText = string.Concat(
                offset < 0 ? "-" : "+",
                (absoluteOffset / 3600).ToString("00", CultureInfo.InvariantCulture),
                ":",
                (absoluteOffset / 60 % 60).ToString("00", CultureInfo.InvariantCulture),
                absoluteOffset % 60 == 0 ? string.Empty : ":" + (absoluteOffset % 60).ToString("00", CultureInfo.InvariantCulture));
            return time + offsetText;
        }

        private static string FormatTimeSpan(TimeSpan value)
        {
            long microseconds = GetTimeMicroseconds(value);
            return new TimeOnly(microseconds * 10)
                .ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                .TrimEnd('0')
                .TrimEnd('.');
        }

        private static string FormatTextTimestamp(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }

        private static string FormatTemporalText(CassieTemporalValue temporal, int oid)
        {
            return temporal.PostgresTypeOid != oid
                ? throw new ArgumentException("The temporal value does not match the PostgreSQL type OID.", nameof(temporal))
                : !temporal.HasRawValue
                    ? temporal.TextValue ?? throw new ArgumentException("The temporal text value is missing.", nameof(temporal))
                    : temporal.IsInfinity
                        ? temporal.Value < 0 ? "-infinity" : "infinity"
                        : temporal.Kind switch
                        {
                            CassieTemporalKind.Date => FormatExtendedDate(checked((int)temporal.Value)),
                            CassieTemporalKind.Time => FormatExtendedTime(temporal.Value),
                            CassieTemporalKind.TimeWithTimeZone => FormatExtendedTimeWithTimeZone(temporal.Value, temporal.OffsetSeconds),
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
                        WriteInt32(stream, checked(-temporal.OffsetSeconds));
                        return stream.ToArray();
                    }
                default:
                    throw new ArgumentException("The temporal kind is invalid.", nameof(temporal));
            }
        }

        private static string FormatExtendedDate(int daysSincePostgresEpoch)
        {
            long daysSinceUnixEpoch = (long)daysSincePostgresEpoch + 10_957;
            long shiftedDays = daysSinceUnixEpoch + 719_468;
            long era = FloorDivide(shiftedDays, 146_097);
            long dayOfEra = shiftedDays - (era * 146_097);
            long yearOfEra = (dayOfEra - (dayOfEra / 1_460) + (dayOfEra / 36_524) - (dayOfEra / 146_096)) / 365;
            long year = yearOfEra + (era * 400);
            long dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
            long monthPrime = ((5 * dayOfYear) + 2) / 153;
            long day = dayOfYear - (((153 * monthPrime) + 2) / 5) + 1;
            long month = monthPrime + (monthPrime < 10 ? 3 : -9);
            year += month <= 2 ? 1 : 0;
            long displayYear = year > 0 ? year : 1 - year;
            return string.Concat(
                displayYear.ToString("0000", CultureInfo.InvariantCulture), "-",
                month.ToString("00", CultureInfo.InvariantCulture), "-",
                day.ToString("00", CultureInfo.InvariantCulture),
                year <= 0 ? " BC" : string.Empty);
        }

        private static string FormatExtendedTimestamp(long microsecondsSincePostgresEpoch)
        {
            const long microsecondsPerDay = 86_400_000_000;
            long days = FloorDivide(microsecondsSincePostgresEpoch, microsecondsPerDay);
            long time = microsecondsSincePostgresEpoch - (days * microsecondsPerDay);
            return string.Concat(FormatExtendedDate(checked((int)days)), " ", FormatExtendedTime(time));
        }

        private static string FormatExtendedTime(long microsecondsSinceMidnight)
        {
            if (microsecondsSinceMidnight is < 0 or > 86_400_000_000)
            {
                throw new ArgumentOutOfRangeException(nameof(microsecondsSinceMidnight), "TIME must be from midnight through 24:00.");
            }

            if (microsecondsSinceMidnight == 86_400_000_000)
            {
                return "24:00:00";
            }

            long hours = microsecondsSinceMidnight / 3_600_000_000;
            long minutes = microsecondsSinceMidnight / 60_000_000 % 60;
            long wholeSeconds = microsecondsSinceMidnight / 1_000_000 % 60;
            long fractionalMicroseconds = microsecondsSinceMidnight % 1_000_000;
            string clock = string.Concat(
                hours.ToString("00", CultureInfo.InvariantCulture), ":",
                minutes.ToString("00", CultureInfo.InvariantCulture), ":",
                wholeSeconds.ToString("00", CultureInfo.InvariantCulture));
            return fractionalMicroseconds == 0
                ? clock
                : clock + "." + fractionalMicroseconds.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        private static string FormatExtendedTimeWithTimeZone(long microsecondsSinceMidnight, int offsetSeconds)
        {
            long absoluteOffset = Math.Abs((long)offsetSeconds);
            string zone = string.Concat(
                offsetSeconds < 0 ? "-" : "+",
                (absoluteOffset / 3600).ToString("00", CultureInfo.InvariantCulture), ":",
                (absoluteOffset / 60 % 60).ToString("00", CultureInfo.InvariantCulture),
                absoluteOffset % 60 == 0 ? string.Empty : ":" + (absoluteOffset % 60).ToString("00", CultureInfo.InvariantCulture));
            return FormatExtendedTime(microsecondsSinceMidnight) + zone;
        }

        private static long FloorDivide(long value, long divisor)
        {
            long quotient = value / divisor;
            return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
        }

        private static long GetTimeMicroseconds(TimeSpan value)
        {
            return value.Ticks is >= 0 and < TimeSpan.TicksPerDay
                ? value.Ticks / 10
                : throw new ArgumentOutOfRangeException(nameof(value), "TIME parameters must be from midnight through the final microsecond before 24:00.");
        }

        private static string FormatTextInterval(CassieInterval value)
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

        private static string FormatTextVector(CassieVector vector, int oid)
        {
            return CassieTypeMap.GetVectorDimension(oid) == vector.Dimension
                ? "[" + string.Join(",", vector.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))) + "]"
                : throw new ArgumentException("The vector dimension does not match its PostgreSQL type OID.", nameof(vector));
        }

        internal static byte[]? GetBinaryParameter(object? value, int oid)
        {
            if (value is null or DBNull)
            {
                return CassieTypeMap.IsArrayOid(oid) || CassieTypeMap.IsObjectIdentifierOid(oid) ? [] : oid switch
                {
                    16 or 17 or 19 or 20 or 21 or 23 or 700 or 701 or 1082 or 1083 or 1114 or 1184 or 1186 or 1266 or 1700 or 2950 or 3802 or 4072 or 1003 => [],
                    _ => null
                };
            }

            Span<byte> bytes = stackalloc byte[16];
            switch (oid, value)
            {
                case (19, string name):
                    byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                    return nameBytes.Length <= 63
                        ? nameBytes
                        : throw new ArgumentOutOfRangeException(nameof(value), "Binary NAME parameters cannot exceed 63 UTF-8 bytes.");
                case (1003, Array names):
                    return EncodeBinaryNameArray(names);
                case (int arrayOid, CassieArray values) when CassieTypeMap.GetArrayElementOid(arrayOid) is int elementOid:
                    if (values.ElementTypeOid != elementOid)
                    {
                        throw new ArgumentException("The CassieArray element OID does not match the bound PostgreSQL array type.", nameof(value));
                    }

                    return EncodeBinaryArray(values, elementOid);
                case (int arrayOid, Array values) when CassieTypeMap.GetArrayElementOid(arrayOid) is int elementOid:
                    return EncodeBinaryArray(values, elementOid);
                case (int temporalOid, CassieTemporalValue temporal) when temporal.PostgresTypeOid == temporalOid:
                    return EncodeBinaryTemporal(temporal);
                case (18 or 19 or 25 or 1042 or 1043, string text):
                    return Encoding.UTF8.GetBytes(text);
                case (18 or 19 or 25 or 1042 or 1043, char character):
                    return Encoding.UTF8.GetBytes(character.ToString());
                case (114, string jsonText):
                    return Encoding.UTF8.GetBytes(jsonText);
                case (16, bool boolean):
                    return [boolean ? (byte)1 : (byte)0];
                case (17, byte[] bytea):
                    return bytea;
                case (20, long int8):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, int8);
                    return bytes[..8].ToArray();
                case (21, short int2):
                    BinaryPrimitives.WriteInt16BigEndian(bytes, int2);
                    return bytes[..2].ToArray();
                case (21, byte int2):
                    BinaryPrimitives.WriteInt16BigEndian(bytes, int2);
                    return bytes[..2].ToArray();
                case (21, sbyte int2):
                    BinaryPrimitives.WriteInt16BigEndian(bytes, int2);
                    return bytes[..2].ToArray();
                case (23, int int4):
                    BinaryPrimitives.WriteInt32BigEndian(bytes, int4);
                    return bytes[..4].ToArray();
                case (23, ushort int4):
                    BinaryPrimitives.WriteInt32BigEndian(bytes, int4);
                    return bytes[..4].ToArray();
                case (int oidType, uint oidValue) when CassieTypeMap.IsObjectIdentifierOid(oidType):
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, oidValue);
                    return bytes[..4].ToArray();
                case (700, float float4):
                    BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(float4));
                    return bytes[..4].ToArray();
                case (701, double float8):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(float8));
                    return bytes[..8].ToArray();
                case (1700, CassieNumeric numeric):
                    return EncodeBinaryNumeric(numeric.Value);
                case (1700, decimal numeric):
                    return EncodeBinaryNumeric(numeric.ToString(CultureInfo.InvariantCulture));
                case (1700, ulong numeric):
                    return EncodeBinaryNumeric(numeric.ToString(CultureInfo.InvariantCulture));
                case (650 or 869, CassieNetworkAddress networkAddress):
                    return EncodeBinaryNetwork(networkAddress, oid == 650);
                case (774 or 829, PhysicalAddress macAddress):
                    return EncodeBinaryMacAddress(macAddress, oid == 774 ? 8 : 6);
                case (1560 or 1562, CassieBitString bitString) when bitString.Value is not null && bitString.IsVarying == (oid == 1562):
                    return EncodeBinaryBitString(bitString);
                case (1560 or 1562, CassieBitString):
                    throw new ArgumentException("The bit string value does not match the PostgreSQL type OID.", nameof(value));
                case (3220, CassieLsn lsn):
                    BinaryPrimitives.WriteUInt64BigEndian(bytes, lsn.Value);
                    return bytes[..8].ToArray();
                case (int vectorOid, CassieVector vector) when CassieTypeMap.IsVectorOid(vectorOid):
                    return EncodeBinaryVector(vector, vectorOid);
                case (1186, CassieInterval interval):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, interval.Microseconds);
                    using (MemoryStream intervalBytes = new(16))
                    {
                        intervalBytes.Write(bytes[..8]);
                        WriteInt32(intervalBytes, interval.Days);
                        WriteInt32(intervalBytes, interval.Months);
                        return intervalBytes.ToArray();
                    }
                case (1266, CassieTimeWithTimeZone timeTz):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, timeTz.Time.Ticks / 10);
                    using (MemoryStream timeTzBytes = new(12))
                    {
                        timeTzBytes.Write(bytes[..8]);
                        WriteInt32(timeTzBytes, checked(-timeTz.OffsetSeconds));
                        return timeTzBytes.ToArray();
                    }
                case (1082, DateOnly date):
                    BinaryPrimitives.WriteInt32BigEndian(bytes, date.DayNumber - new DateOnly(2000, 1, 1).DayNumber);
                    return bytes[..4].ToArray();
                case (1082, DateTime date):
                    BinaryPrimitives.WriteInt32BigEndian(bytes, DateOnly.FromDateTime(date).DayNumber - new DateOnly(2000, 1, 1).DayNumber);
                    return bytes[..4].ToArray();
                case (1083, TimeOnly time):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, time.Ticks / 10);
                    return bytes[..8].ToArray();
                case (1083, TimeSpan time):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, GetTimeMicroseconds(time));
                    return bytes[..8].ToArray();
                case (1114, DateTime timestamp):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, (timestamp.Ticks - new DateTime(2000, 1, 1).Ticks) / 10);
                    return bytes[..8].ToArray();
                case (1184, DateTimeOffset timestampTz):
                    BinaryPrimitives.WriteInt64BigEndian(bytes, (timestampTz.UtcTicks - new DateTime(2000, 1, 1).Ticks) / 10);
                    return bytes[..8].ToArray();
                case (2950, Guid uuid):
                    _ = uuid.TryWriteBytes(bytes, bigEndian: true, out _);
                    return bytes.ToArray();
                case (3802, string jsonb):
                    byte[] json = Encoding.UTF8.GetBytes(jsonb);
                    return [1, .. json];
                case (4072, string jsonPath):
                    return [1, .. Encoding.UTF8.GetBytes(jsonPath)];
                default:
                    return null;
            }
        }

        private static byte[] EncodeBinaryNameArray(Array values)
        {
            if (values.Rank != 1 || values.GetLowerBound(0) != 0)
            {
                throw new NotSupportedException("NAME[] parameters must be one-dimensional and zero-based.");
            }

            using MemoryStream stream = new();
            int count = values.Length;
            bool hasNull = values.Cast<object?>().Any(static value => value is null or DBNull);
            WriteInt32(stream, count == 0 ? 0 : 1);
            WriteInt32(stream, hasNull ? 1 : 0);
            WriteInt32(stream, 19);
            if (count == 0)
            {
                return stream.ToArray();
            }

            WriteInt32(stream, count);
            WriteInt32(stream, 1);
            foreach (object? item in values)
            {
                if (item is null or DBNull)
                {
                    WriteInt32(stream, -1);
                    continue;
                }

                if (item is not string name)
                {
                    throw new ArgumentException("NAME[] parameters must contain only strings or null values.", nameof(values));
                }

                byte[] bytes = Encoding.UTF8.GetBytes(name);
                if (bytes.Length > 63)
                {
                    throw new ArgumentOutOfRangeException(nameof(values), "Binary NAME[] elements cannot exceed 63 UTF-8 bytes.");
                }

                WriteInt32(stream, bytes.Length);
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
                    throw new ArgumentException("The CassieArray element OID does not match the PostgreSQL array type.", nameof(values));
                }

                dimensions = [.. cassieArray.Dimensions];
                lowerBounds = [.. cassieArray.LowerBounds];
                elements = cassieArray.Elements;
                count = cassieArray.Count;
            }
            else if (values is Array array)
            {
                dimensions = [.. Enumerable.Range(0, array.Rank).Select(array.GetLength)];
                lowerBounds = [.. Enumerable.Range(0, array.Rank).Select(dimension => array.GetLowerBound(dimension) == 0 ? 1 : array.GetLowerBound(dimension))];
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

            bool hasNull = elements.Any(static value => value is null or DBNull);
            using MemoryStream stream = new();
            WriteInt32(stream, count == 0 ? 0 : dimensions.Length);
            WriteInt32(stream, hasNull ? 1 : 0);
            WriteInt32(stream, elementOid);
            if (count == 0)
            {
                return stream.ToArray();
            }

            for (int dimension = 0; dimension < dimensions.Length; dimension++)
            {
                WriteInt32(stream, dimensions[dimension]);
                WriteInt32(stream, lowerBounds[dimension]);
            }

            foreach (object? value in elements)
            {
                if (value is null or DBNull)
                {
                    WriteInt32(stream, -1);
                    continue;
                }

                byte[]? elementBytes = GetBinaryParameter(value, elementOid);
                if (elementBytes is null)
                {
                    return null;
                }

                WriteInt32(stream, elementBytes.Length);
                stream.Write(elementBytes);
            }

            return stream.ToArray();
        }

        private static string EncodeTextArray(object values, int arrayOid)
        {
            int? expectedElementOid = CassieTypeMap.GetArrayElementOid(arrayOid);
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
                    throw new ArgumentException("The CassieArray element OID does not match the PostgreSQL array type.", nameof(values));
                }

                dimensions = [.. cassieArray.Dimensions];
                lowerBounds = [.. cassieArray.LowerBounds];
                elements = [.. cassieArray.Elements];
            }
            else if (values is Array array)
            {
                dimensions = [.. Enumerable.Range(0, array.Rank).Select(array.GetLength)];
                lowerBounds = [.. Enumerable.Range(0, array.Rank).Select(dimension => array.GetLowerBound(dimension) == 0 ? 1 : array.GetLowerBound(dimension))];
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

            if (dimensions.Length > 0 && dimensions.Aggregate(1L, static (product, dimension) => checked(product * dimension)) != elements.Count)
            {
                throw new ArgumentException("The flattened element count does not match the array dimensions.", nameof(values));
            }

            StringBuilder result = new();
            if (lowerBounds.Where((lowerBound, index) => lowerBound != 1).Any())
            {
                for (int index = 0; index < dimensions.Length; index++)
                {
                    int upperBound = checked((int)((long)lowerBounds[index] + dimensions[index] - 1));
                    _ = result.Append('[').Append(lowerBounds[index].ToString(CultureInfo.InvariantCulture)).Append(':')
                        .Append(upperBound.ToString(CultureInfo.InvariantCulture)).Append(']');
                }

                _ = result.Append('=');
            }

            int flatIndex = 0;
            AppendArrayLevel(result, elements, dimensions, 0, ref flatIndex, elementOid);
            return result.ToString();
        }

        private static void AppendArrayLevel(StringBuilder output, IReadOnlyList<object?> elements, IReadOnlyList<int> dimensions, int depth, ref int flatIndex, int elementOid)
        {
            _ = output.Append('{');
            for (int index = 0; index < dimensions[depth]; index++)
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

                object? value = elements[flatIndex++];
                if (value is null or DBNull)
                {
                    _ = output.Append("NULL");
                    continue;
                }

                string text = Encoding.UTF8.GetString(EncodeTextParameter(value, elementOid));
                bool quote = text.Length == 0 || text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                    || text.Any(static character => char.IsWhiteSpace(character) || character is '{' or '}' or ',' or '"' or '\\');
                if (quote)
                {
                    _ = output.Append('"');
                    foreach (char character in text)
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
            for (int index = 0; index < values.Length; index++)
            {
                if (index > 0)
                {
                    _ = result.Append(',');
                }

                object? item = values.GetValue(index);
                if (item is null or DBNull)
                {
                    _ = result.Append("NULL");
                    continue;
                }

                if (item is not string name)
                {
                    throw new ArgumentException("NAME[] parameters must contain only strings or null values.", nameof(values));
                }

                _ = result.Append('"');
                foreach (char character in name)
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

        private static byte[] EncodeBinaryNumeric(string value)
        {
            ushort sign = 0x0000;
            ushort scale;
            if (value == "NaN")
            {
                sign = 0xC000;
                scale = 0;
                return EncodeNumericHeader([], 0, sign, scale);
            }

            if (value is "Infinity" or "+Infinity" or "-Infinity")
            {
                sign = value == "-Infinity" ? (ushort)0xF000 : (ushort)0xD000;
                return EncodeNumericHeader([], 0, sign, 32);
            }

            int index = 0;
            bool negative = value.Length > 0 && value[0] == '-';
            if (negative || (value.Length > 0 && value[0] == '+'))
            {
                index++;
            }

            int exponent = 0;
            int exponentIndex = value.IndexOfAny(['e', 'E'], index);
            ReadOnlySpan<char> mantissa = exponentIndex < 0 ? value.AsSpan(index) : value.AsSpan(index, exponentIndex - index);
            if (exponentIndex >= 0 && !int.TryParse(value.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                throw new FormatException("CassieNumeric exponent is outside the supported range.");
            }

            int point = mantissa.IndexOf('.');
            ReadOnlySpan<char> integerPart = point < 0 ? mantissa : mantissa[..point];
            ReadOnlySpan<char> fractionalPart = point < 0 ? [] : mantissa[(point + 1)..];
            int decimalScale = checked(fractionalPart.Length - exponent);
            if (decimalScale > 16383 || integerPart.Length + fractionalPart.Length + Math.Max(0, -decimalScale) > 131072)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The numeric value exceeds Cassie's binary NUMERIC limits.");
            }

            string coefficient = integerPart.ToString() + fractionalPart.ToString();
            if (coefficient.Length == 0)
            {
                coefficient = "0";
            }

            if (decimalScale < 0)
            {
                coefficient += new string('0', -decimalScale);
                decimalScale = 0;
            }

            if (coefficient.Length <= decimalScale)
            {
                coefficient = new string('0', decimalScale + 1 - coefficient.Length) + coefficient;
            }

            int integerDigitCount = coefficient.Length - decimalScale;
            string integerDigits = coefficient[..integerDigitCount];
            string fractionDigits = coefficient[integerDigitCount..];
            scale = checked((ushort)decimalScale);

            int integerPadding = (4 - (integerDigits.Length % 4)) % 4;
            int fractionPadding = (4 - (fractionDigits.Length % 4)) % 4;
            integerDigits = new string('0', integerPadding) + integerDigits;
            fractionDigits += new string('0', fractionPadding);
            List<ushort> digits = [];
            for (int start = 0; start < integerDigits.Length; start += 4)
            {
                digits.Add(ushort.Parse(integerDigits.AsSpan(start, 4), CultureInfo.InvariantCulture));
            }

            for (int start = 0; start < fractionDigits.Length; start += 4)
            {
                digits.Add(ushort.Parse(fractionDigits.AsSpan(start, 4), CultureInfo.InvariantCulture));
            }

            int weight = (integerDigits.Length / 4) - 1;
            int leading = 0;
            while (leading < digits.Count && digits[leading] == 0)
            {
                leading++;
            }

            if (leading > 0)
            {
                digits.RemoveRange(0, leading);
                weight -= leading;
            }

            while (digits.Count > 0 && digits[^1] == 0)
            {
                digits.RemoveAt(digits.Count - 1);
            }

            if (digits.Count == 0)
            {
                weight = 0;
            }

            if (negative && digits.Count > 0)
            {
                sign = 0x4000;
            }

            return EncodeNumericHeader(digits, checked((short)weight), sign, scale);
        }

        private static byte[] EncodeBinaryNetwork(CassieNetworkAddress address, bool expectedCidr)
        {
            if (address.Address is null || address.IsCidr != expectedCidr
                || (address.Address.AddressFamily == AddressFamily.InterNetworkV6 && address.Address.ScopeId != 0))
            {
                throw new ArgumentException("The network address value does not match the PostgreSQL type OID.", nameof(address));
            }

            byte[] addressBytes = address.Address.GetAddressBytes();
            byte family = address.Address.AddressFamily switch
            {
                AddressFamily.InterNetwork => 2,
                AddressFamily.InterNetworkV6 => 3,
                _ => throw new ArgumentException("Only IPv4 and IPv6 network addresses are supported.", nameof(address))
            };
            int width = addressBytes.Length * 8;
            if ((uint)address.PrefixLength > (uint)width)
            {
                throw new ArgumentException("The network prefix length is outside the address family range.", nameof(address));
            }

            if (address.IsCidr)
            {
                for (int bit = address.PrefixLength; bit < width; bit++)
                {
                    if ((addressBytes[bit / 8] & (1 << (7 - (bit % 8)))) != 0)
                    {
                        throw new ArgumentException("CIDR parameters cannot contain host bits.", nameof(address));
                    }
                }
            }

            return [family, checked((byte)address.PrefixLength), address.IsCidr ? (byte)1 : (byte)0, checked((byte)addressBytes.Length), .. addressBytes];
        }

        private static byte[] EncodeBinaryMacAddress(PhysicalAddress address, int expectedLength)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == expectedLength
                ? bytes
                : throw new ArgumentException("The MAC address length does not match the PostgreSQL type OID.", nameof(address));
        }

        private static byte[] EncodeBinaryBitString(CassieBitString bitString)
        {
            if (bitString.Value is null)
            {
                throw new ArgumentException("A default bit-string value is invalid.", nameof(bitString));
            }

            int payloadLength = (bitString.Value.Length + 7) / 8;
            byte[] bytes = new byte[4 + payloadLength];
            BinaryPrimitives.WriteInt32BigEndian(bytes, bitString.Value.Length);
            for (int bit = 0; bit < bitString.Value.Length; bit++)
            {
                if (bitString.Value[bit] == '1')
                {
                    bytes[4 + (bit / 8)] |= (byte)(1 << (7 - (bit % 8)));
                }
            }

            return bytes;
        }

        private static byte[] EncodeBinaryVector(CassieVector vector, int oid)
        {
            if (CassieTypeMap.GetVectorDimension(oid) != vector.Dimension)
            {
                throw new ArgumentException("The vector dimension does not match its PostgreSQL type OID.", nameof(vector));
            }

            byte[] bytes = new byte[4 + (vector.Count * sizeof(float))];
            BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)vector.Dimension));
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(2), 0);
            for (int index = 0; index < vector.Count; index++)
            {
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4 + (index * sizeof(float))), BitConverter.SingleToInt32Bits(vector[index]));
            }

            return bytes;
        }

        private static byte[] EncodeNumericHeader(List<ushort> digits, short weight, ushort sign, ushort scale)
        {
            using MemoryStream payload = new(8 + (digits.Count * sizeof(ushort)));
            WriteInt16(payload, checked((short)digits.Count));
            WriteInt16(payload, weight);
            WriteUInt16(payload, sign);
            WriteUInt16(payload, scale);
            foreach (ushort digit in digits)
            {
                WriteUInt16(payload, digit);
            }

            return payload.ToArray();
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static string EncodeArrayParameter(Array array, int arrayOid)
        {
            int elementOid = CassieTypeMap.GetArrayElementOid(arrayOid)
                ?? throw new NotSupportedException($"Text encoding for PostgreSQL array OID {arrayOid} is not supported.");
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartArray();
                foreach (object? item in array)
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
                            ? FormatTextTimestamp(dateTime)
                            : dateTime.ToString("O", CultureInfo.InvariantCulture));
                    }
                    else if (item is DateTimeOffset dateTimeOffset)
                    {
                        writer.WriteStringValue(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                    }
                    else if (item is CassieNetworkAddress networkAddress)
                    {
                        writer.WriteStringValue(EncodeTextNetworkAddress(networkAddress, elementOid == 650));
                    }
                    else if (item is PhysicalAddress macAddress)
                    {
                        writer.WriteStringValue(FormatTextMacAddress(macAddress, elementOid == 774 ? 8 : 6));
                    }
                    else if (item is CassieBitString bitString)
                    {
                        writer.WriteStringValue(EncodeTextBitString(bitString, elementOid == 1562));
                    }
                    else if (item is CassieLsn lsn)
                    {
                        writer.WriteStringValue(lsn.ToString());
                    }
                    else if (item is CassieTimeWithTimeZone timeWithTimeZone)
                    {
                        writer.WriteStringValue(FormatTextTimeWithTimeZone(timeWithTimeZone));
                    }
                    else if (item is CassieTemporalValue temporal)
                    {
                        writer.WriteStringValue(FormatTemporalText(temporal, elementOid));
                    }
                    else if (item is CassieInterval interval)
                    {
                        writer.WriteStringValue(FormatTextInterval(interval));
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
                        throw new NotSupportedException($"Array element type '{item.GetType().FullName}' has no text codec.");
                    }
                }

                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteInt16(Stream stream, short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
