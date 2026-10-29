using Cntryl.Cassie.Connection;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace Cntryl.Cassie.Types
{
    internal static class CassieResultCodec
    {
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type GetClrType(int oid)
        {
            return CassieTypeMap.GetClrType(oid);
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type GetClrType(int oid, short formatCode)
        {
            return CassieTypeMap.GetClrType(oid, formatCode);
        }

        internal static object ConvertValue(string value, int oid)
        {
            return ConvertValue(value, oid, CassieDateStyle.Default);
        }

        internal static object ConvertValue(string value, int oid, CassieDateStyle? dateStyle)
        {
            return oid switch
            {
                16 => value switch
                {
                    "t" or "true" or "TRUE" => true,
                    "f" or "false" or "FALSE" => false,
                    _ => throw new FormatException("Cassie returned an invalid text boolean value.")
                },
                17 => DecodeBytea(value),
                20 => long.Parse(value, CultureInfo.InvariantCulture),
                21 => short.Parse(value, CultureInfo.InvariantCulture),
                23 => int.Parse(value, CultureInfo.InvariantCulture),
                26 => uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture),
                700 => float.Parse(value, CultureInfo.InvariantCulture),
                701 => double.Parse(value, CultureInfo.InvariantCulture),
                1700 => new CassieNumeric(value),
                2950 => Guid.Parse(value),
                1082 => DecodeTextDate(value, dateStyle),
                1083 => DecodeTextTime(value),
                1114 => DecodeTextTimestamp(value, CassieTemporalKind.Timestamp, dateStyle),
                1184 => DecodeTextTimestampTz(value, dateStyle),
                1266 => DecodeTextTimeWithTimeZone(value),
                _ => value
            };
        }

        internal static object ConvertBinaryValue(byte[] value, int oid)
        {
            if (!CassieTypeMap.SupportsBinaryResult(oid))
            {
                throw new NotSupportedException($"Binary results for PostgreSQL OID {oid} are not supported by this provider.");
            }

            if (CassieTypeMap.IsVectorOid(oid))
            {
                return DecodeBinaryVector(value, CassieTypeMap.GetVectorDimension(oid));
            }

            if (CassieTypeMap.GetArrayElementOid(oid) is int elementTypeOid)
            {
                return DecodeBinaryArray(value, elementTypeOid);
            }

            ReadOnlySpan<byte> bytes = value;
            return oid switch
            {
                16 when bytes.Length == 1 && bytes[0] <= 1 => bytes[0] == 1,
                17 => value,
                18 or 19 or 25 or 1042 or 1043 => Encoding.UTF8.GetString(bytes),
                20 when bytes.Length == 8 => BinaryPrimitives.ReadInt64BigEndian(bytes),
                21 when bytes.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(bytes),
                23 when bytes.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(bytes),
                _ when CassieTypeMap.IsObjectIdentifierOid(oid) => bytes.Length == 4
                    ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
                    : throw new FormatException($"Cassie returned a binary object identifier with length {bytes.Length} instead of 4."),
                700 when bytes.Length == 4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
                701 when bytes.Length == 8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)),
                650 or 869 => DecodeBinaryNetwork(value, oid == 650),
                774 => DecodeBinaryMacAddress(value, 8),
                829 => DecodeBinaryMacAddress(value, 6),
                1700 => DecodeBinaryNumeric(bytes),
                1560 or 1562 => DecodeBinaryBitString(bytes, oid == 1562),
                3220 when bytes.Length == 8 => new CassieLsn(BinaryPrimitives.ReadUInt64BigEndian(bytes)),
                1082 when bytes.Length == 4 => DecodeBinaryDate(bytes),
                1083 when bytes.Length == 8 => DecodeBinaryTime(bytes),
                1186 when bytes.Length == 16 => DecodeBinaryInterval(bytes),
                1114 when bytes.Length == 8 => DecodeBinaryTimestamp(bytes),
                1184 when bytes.Length == 8 => DecodeBinaryTimestampTz(bytes),
                1266 when bytes.Length == 12 => DecodeBinaryTimeTz(bytes),
                114 => Encoding.UTF8.GetString(bytes),
                2278 when bytes.IsEmpty => string.Empty,
                2278 => throw new FormatException("Cassie returned binary void with a non-empty payload."),
                2950 when bytes.Length == 16 => new Guid(bytes, bigEndian: true),
                3802 when bytes.Length > 0 && bytes[0] == 1 => Encoding.UTF8.GetString(bytes[1..]),
                3802 => throw new FormatException("Cassie returned binary JSONB with an unsupported version."),
                4072 => DecodeBinaryJsonPath(bytes),
                _ => throw new NotSupportedException($"Binary results for PostgreSQL OID {oid} are not supported by this provider.")
            };
        }

        private static string DecodeBinaryJsonPath(ReadOnlySpan<byte> bytes)
        {
            return bytes.Length == 0 || bytes[0] != 1
                ? throw new FormatException("Cassie returned binary JSONPath with an unsupported version.")
                : Encoding.UTF8.GetString(bytes[1..]);
        }

        private static CassieArray DecodeBinaryArray(byte[] value, int expectedElementTypeOid)
        {
            ReadOnlySpan<byte> bytes = value;
            int offset = 0;
            int rank = ReadBinaryInt32(bytes, ref offset);
            int hasNull = ReadBinaryInt32(bytes, ref offset);
            int elementTypeOid = ReadBinaryInt32(bytes, ref offset);
            if (rank is < 0 or > 6 || hasNull is not 0 and not 1 || elementTypeOid != expectedElementTypeOid)
            {
                throw new FormatException("Cassie returned an invalid binary array header.");
            }

            if (rank == 0 && hasNull != 0)
            {
                throw new FormatException("Cassie returned an empty binary array with a null flag.");
            }

            int[] dimensions = new int[rank];
            int[] lowerBounds = new int[rank];
            long elementCount = rank == 0 ? 0 : 1;
            for (int dimension = 0; dimension < rank; dimension++)
            {
                int length = ReadBinaryInt32(bytes, ref offset);
                int lowerBound = ReadBinaryInt32(bytes, ref offset);
                if (length <= 0)
                {
                    throw new FormatException("Cassie returned a nonpositive binary array dimension.");
                }

                if ((long)lowerBound + length - 1 > int.MaxValue)
                {
                    throw new FormatException("Cassie returned binary array bounds outside the signed 32-bit range.");
                }

                dimensions[dimension] = length;
                lowerBounds[dimension] = lowerBound;
                elementCount = checked(elementCount * length);
                if (elementCount > (bytes.Length / sizeof(int)))
                {
                    throw new FormatException("Cassie returned binary array dimensions larger than the available payload.");
                }
            }

            object?[] elements = new object?[checked((int)elementCount)];
            bool foundNull = false;
            for (int index = 0; index < elements.Length; index++)
            {
                int length = ReadBinaryInt32(bytes, ref offset);
                if (length == -1)
                {
                    foundNull = true;
                    continue;
                }

                if (length < 0 || offset > bytes.Length - length)
                {
                    throw new FormatException("Cassie returned a truncated binary array element.");
                }

                elements[index] = ConvertBinaryValue(bytes.Slice(offset, length).ToArray(), elementTypeOid);
                offset += length;
            }

            _ = offset != bytes.Length || (hasNull == 0 && foundNull)
                ? throw new FormatException("Cassie returned inconsistent binary array framing.")
                : true;

            return new CassieArray(elementTypeOid, dimensions, lowerBounds, elements);
        }

        private static CassieNumeric DecodeBinaryNumeric(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 8)
            {
                throw new FormatException("Cassie returned a truncated binary numeric header.");
            }

            int digitCount = BinaryPrimitives.ReadInt16BigEndian(bytes);
            int weight = BinaryPrimitives.ReadInt16BigEndian(bytes[2..]);
            ushort sign = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
            int scale = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
            if (digitCount < 0 || bytes.Length != 8 + (digitCount * sizeof(ushort)))
            {
                throw new FormatException("Cassie returned invalid binary numeric framing.");
            }

            if (sign is 0xC000 or 0xD000 or 0xF000)
            {
                string special = sign switch
                {
                    0xC000 => "NaN",
                    0xD000 => "Infinity",
                    _ => "-Infinity"
                };
                return digitCount == 0 ? new CassieNumeric(special) : throw new FormatException("Cassie returned digits for a special numeric value.");
            }

            if (sign is not 0x0000 and not 0x4000 || scale > 16383)
            {
                throw new FormatException("Cassie returned an invalid binary numeric sign or scale.");
            }

            int[] digits = new int[digitCount];
            bool nonzero = false;
            for (int index = 0; index < digitCount; index++)
            {
                int digit = BinaryPrimitives.ReadUInt16BigEndian(bytes[(8 + (index * sizeof(ushort)))..]);
                if (digit > 9999)
                {
                    throw new FormatException("Cassie returned an out-of-range binary numeric digit.");
                }

                digits[index] = digit;
                nonzero |= digit != 0;
            }

            int fractionalGroups = (scale + 3) / 4;
            int integerGroups = Math.Max(0, weight + 1);
            StringBuilder text = new(checked(Math.Max(1, integerGroups * 4) + (scale == 0 ? 0 : scale + 1) + 1));
            if (sign == 0x4000 && nonzero)
            {
                _ = text.Append('-');
            }

            int firstInteger = ReadNumericBase10000Digit(digits, weight, weight);
            if (integerGroups == 0)
            {
                _ = text.Append('0');
            }
            else
            {
                _ = text.Append(firstInteger.ToString(CultureInfo.InvariantCulture));
                for (int group = 1; group < integerGroups; group++)
                {
                    int position = weight - group;
                    _ = text.Append(ReadNumericBase10000Digit(digits, weight, position).ToString("D4", CultureInfo.InvariantCulture));
                }
            }

            if (scale > 0)
            {
                _ = text.Append('.');
                for (int group = 1; group <= fractionalGroups; group++)
                {
                    int position = -group;
                    _ = text.Append(ReadNumericBase10000Digit(digits, weight, position).ToString("D4", CultureInfo.InvariantCulture));
                }

                text.Length -= (fractionalGroups * 4) - scale;
            }

            return new CassieNumeric(text.ToString());
        }

        private static CassieNetworkAddress DecodeBinaryNetwork(byte[] value, bool expectedCidr)
        {
            if (value.Length < 4)
            {
                throw new FormatException("Cassie returned a truncated binary network address.");
            }

            byte family = value[0];
            int width = family switch
            {
                2 => 32,
                3 => 128,
                _ => throw new FormatException("Cassie returned an invalid binary network address family.")
            };
            int addressLength = family == 2 ? 4 : 16;
            int prefixLength = value[1];
            bool isCidr = value[2] switch
            {
                0 => false,
                1 => true,
                _ => throw new FormatException("Cassie returned an invalid binary network address type flag.")
            };
            if (prefixLength > width || isCidr != expectedCidr || value[3] != addressLength || value.Length != 4 + addressLength)
            {
                throw new FormatException("Cassie returned an invalid binary network address frame.");
            }

            ReadOnlySpan<byte> addressBytes = value.AsSpan(4);
            if (isCidr)
            {
                for (int bit = prefixLength; bit < width; bit++)
                {
                    if ((addressBytes[bit / 8] & (1 << (7 - (bit % 8)))) != 0)
                    {
                        throw new FormatException("Cassie returned a CIDR address with host bits set.");
                    }
                }
            }

            return new CassieNetworkAddress(new IPAddress(addressBytes), prefixLength, isCidr);
        }

        private static PhysicalAddress DecodeBinaryMacAddress(byte[] value, int expectedLength)
        {
            return value.Length == expectedLength
                ? new PhysicalAddress(value)
                : throw new FormatException("Cassie returned an invalid binary MAC address frame.");
        }

        private static CassieBitString DecodeBinaryBitString(ReadOnlySpan<byte> bytes, bool isVarying)
        {
            int offset = 0;
            int bitLength = ReadBinaryInt32(bytes, ref offset);
            if (bitLength < 0 || bitLength > (bytes.Length - 4) * 8)
            {
                throw new FormatException("Cassie returned an invalid binary bit-string length.");
            }

            int byteLength = (bitLength + 7) / 8;
            if (bytes.Length != 4 + byteLength)
            {
                throw new FormatException("Cassie returned an invalid binary bit-string frame.");
            }

            char[] bits = new char[bitLength];
            for (int bit = 0; bit < bitLength; bit++)
            {
                bits[bit] = (bytes[4 + (bit / 8)] & (1 << (7 - (bit % 8)))) == 0 ? '0' : '1';
            }

            return new CassieBitString(new string(bits), isVarying);
        }

        private static CassieVector DecodeBinaryVector(byte[] value, int expectedDimension)
        {
            ReadOnlySpan<byte> bytes = value;
            if (bytes.Length < 4)
            {
                throw new FormatException("Cassie returned a truncated binary vector header.");
            }

            int dimension = BinaryPrimitives.ReadInt16BigEndian(bytes);
            short reserved = BinaryPrimitives.ReadInt16BigEndian(bytes[2..]);
            if (dimension < 1 || dimension != expectedDimension || reserved != 0 || bytes.Length != 4 + (dimension * sizeof(float)))
            {
                throw new FormatException("Cassie returned an invalid binary vector frame.");
            }

            float[] components = new float[dimension];
            for (int index = 0; index < dimension; index++)
            {
                components[index] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes[(4 + (index * sizeof(float)))..]));
                if (!float.IsFinite(components[index]))
                {
                    throw new FormatException("Cassie returned a non-finite binary vector component.");
                }
            }

            return new CassieVector(components);
        }

        private static int ReadNumericBase10000Digit(ReadOnlySpan<int> digits, int weight, int position)
        {
            int index = weight - position;
            return (uint)index < (uint)digits.Length ? digits[index] : 0;
        }

        private static int ReadBinaryInt32(ReadOnlySpan<byte> bytes, ref int offset)
        {
            if (offset > bytes.Length - sizeof(int))
            {
                throw new FormatException("Cassie returned a truncated binary array header.");
            }

            int value = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
            offset += sizeof(int);
            return value;
        }

        private static object DecodeTextDate(string value, CassieDateStyle? dateStyle)
        {
            string trimmed = value.Trim();
            if (TryDecodeTemporalInfinity(trimmed, CassieTemporalKind.Date, out CassieTemporalValue infinity))
            {
                return infinity;
            }

            CassieDateStyle style = dateStyle ?? CassieDateStyle.Default;
            return style.TryParseDate(trimmed, out DateOnly date)
                ? date
                : LooksLikeExtendedCalendarValue(trimmed)
                || (dateStyle is null && LooksLikeNonIsoTemporalText(trimmed))
                    ? CassieTemporalValue.FromText(CassieTemporalKind.Date, value)
                    : throw new FormatException("Cassie returned an invalid text date value.");
        }

        private static object DecodeTextTime(string value)
        {
            string trimmed = value.Trim();
            return IsTwentyFourHourTime(trimmed)
                ? CassieTemporalValue.FromTimeMicroseconds(86_400_000_000)
                : TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        }

        private static object DecodeTextTimestamp(string value, CassieTemporalKind kind, CassieDateStyle? dateStyle)
        {
            string trimmed = value.Trim();
            if (TryDecodeTemporalInfinity(trimmed, kind, out CassieTemporalValue infinity))
            {
                return infinity;
            }

            CassieDateStyle style = dateStyle ?? CassieDateStyle.Default;
            return TryParseTextTimestamp(trimmed, style, out DateTime timestamp)
                ? timestamp
                : LooksLikeExtendedCalendarValue(trimmed)
                || (dateStyle is null && LooksLikeNonIsoTemporalText(trimmed))
                    ? CassieTemporalValue.FromText(kind, value)
                    : throw new FormatException("Cassie returned an invalid text timestamp value.");
        }

        private static object DecodeTextTimestampTz(string value, CassieDateStyle? dateStyle)
        {
            string trimmed = value.Trim();
            if (TryDecodeTemporalInfinity(trimmed, CassieTemporalKind.TimestampWithTimeZone, out CassieTemporalValue infinity))
            {
                return infinity;
            }

            CassieDateStyle style = dateStyle ?? CassieDateStyle.Default;
            return TryParseTextTimestampWithTimeZone(trimmed, style, out DateTimeOffset timestamp)
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
                return DateTime.TryParseExact(value, dateStyle.TimestampFormats(), CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
            }

            int separator = value.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0 || !dateStyle.TryParseDate(value[..separator], out DateOnly date)
                || !TimeOnly.TryParse(value[(separator + 1)..], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
            {
                timestamp = default;
                return false;
            }

            timestamp = date.ToDateTime(time, DateTimeKind.Unspecified);
            return true;
        }

        private static bool TryParseTextTimestampWithTimeZone(string value, CassieDateStyle dateStyle, out DateTimeOffset timestamp)
        {
            string timestampText = value;
            int offsetSeconds;
            if (dateStyle.Output == CassieDateOutput.Postgres)
            {
                int zoneSeparator = timestampText.LastIndexOf(' ');
                if (zoneSeparator <= 0 || !TryParseTextOffsetSeconds(timestampText.AsSpan(zoneSeparator + 1), out offsetSeconds))
                {
                    timestamp = default;
                    return false;
                }

                timestampText = timestampText[..zoneSeparator];
                if (!DateTime.TryParseExact(timestampText, dateStyle.TimestampFormats(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime postgresTimestamp))
                {
                    timestamp = default;
                    return false;
                }

                return TryCreateDateTimeOffset(postgresTimestamp, offsetSeconds, out timestamp);
            }

            int dateSeparator = timestampText.IndexOf(' ', StringComparison.Ordinal);
            if (dateSeparator <= 0 || !dateStyle.TryParseDate(timestampText[..dateSeparator], out DateOnly date))
            {
                timestamp = default;
                return false;
            }

            ReadOnlySpan<char> clockAndOffset = timestampText.AsSpan(dateSeparator + 1);
            int offsetStart = -1;
            for (int index = 1; index < clockAndOffset.Length; index++)
            {
                if (clockAndOffset[index] is '+' or '-')
                {
                    offsetStart = index;
                }
            }

            if (offsetStart <= 0 || !TryParseTextOffsetSeconds(clockAndOffset[offsetStart..], out offsetSeconds)
                || !TimeOnly.TryParse(clockAndOffset[..offsetStart], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
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
                int separator = value.LastIndexOf(' ');
                return separator > 0 && separator < value.Length - 1 && char.IsLetter(value[separator + 1]);
            }

            int timeStart = value.IndexOf(' ', StringComparison.Ordinal);
            if (timeStart < 0)
            {
                return false;
            }

            ReadOnlySpan<char> timeAndZone = value.AsSpan(timeStart + 1);
            int zoneStart = timeAndZone.IndexOf(' ');
            if (zoneStart < 0)
            {
                return false;
            }

            ReadOnlySpan<char> zone = timeAndZone[(zoneStart + 1)..].TrimStart();
            return !zone.IsEmpty && char.IsLetter(zone[0]);
        }

        private static bool LooksLikeNonIsoTemporalText(string value)
        {
            int separator = value.IndexOf(' ', StringComparison.Ordinal);
            ReadOnlySpan<char> date = separator < 0 ? value.AsSpan() : value.AsSpan(0, separator);
            return date.Contains('/') || date.Contains('.') || (!date.IsEmpty && char.IsLetter(date[0]));
        }

        private static bool TryParseTextOffsetSeconds(ReadOnlySpan<char> value, out int offsetSeconds)
        {
            try
            {
                long parsed = ParseTextOffsetSeconds(value);
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

        private static object DecodeTextTimeWithTimeZone(string value)
        {
            string trimmed = value.Trim();
            int zoneStart = Math.Max(trimmed.LastIndexOf('+'), trimmed.LastIndexOf('-'));
            if (zoneStart <= 0)
            {
                throw new FormatException("Cassie returned a time-with-time-zone value without an offset.");
            }

            long offsetSeconds = ParseTextOffsetSeconds(trimmed.AsSpan(zoneStart));
            string timeText = trimmed[..zoneStart];
            bool isTwentyFour = IsTwentyFourHourTime(timeText);
            if (isTwentyFour)
            {
                return CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(86_400_000_000, checked((int)offsetSeconds));
            }

            TimeOnly time = TimeOnly.Parse(timeText, CultureInfo.InvariantCulture);
            return new CassieTimeWithTimeZone(time, checked((int)offsetSeconds));
        }

        private static long ParseTextOffsetSeconds(ReadOnlySpan<char> value)
        {
            if (value.Length < 3 || value[0] is not ('+' or '-'))
            {
                throw new FormatException("Cassie returned an invalid time-zone offset.");
            }

            int sign = value[0] == '-' ? -1 : 1;
            ReadOnlySpan<char> components = value[1..];
            int firstColon = components.IndexOf(':');
            ReadOnlySpan<char> hoursText = firstColon < 0 ? components : components[..firstColon];
            if (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out int hours))
            {
                throw new FormatException("Cassie returned an invalid time-zone offset.");
            }

            int minutes = 0;
            int seconds = 0;
            if (firstColon >= 0)
            {
                ReadOnlySpan<char> remaining = components[(firstColon + 1)..];
                int secondColon = remaining.IndexOf(':');
                ReadOnlySpan<char> minutesText = secondColon < 0 ? remaining : remaining[..secondColon];
                if (!int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
                {
                    throw new FormatException("Cassie returned an invalid time-zone offset.");
                }

                if (secondColon >= 0 && !int.TryParse(remaining[(secondColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                {
                    throw new FormatException("Cassie returned an invalid time-zone offset.");
                }
            }

            return checked(sign * (((long)hours * 3600) + ((long)minutes * 60) + seconds));
        }

        private static bool TryDecodeTemporalInfinity(string value, CassieTemporalKind kind, out CassieTemporalValue temporal)
        {
            long? sentinel = value switch
            {
                "infinity" or "+infinity" or "Infinity" or "+Infinity" => kind == CassieTemporalKind.Date ? int.MaxValue : long.MaxValue,
                "-infinity" or "-Infinity" => kind == CassieTemporalKind.Date ? int.MinValue : long.MinValue,
                _ => null
            };
            if (sentinel is not long rawValue || kind is not (CassieTemporalKind.Date or CassieTemporalKind.Timestamp or CassieTemporalKind.TimestampWithTimeZone))
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

            int consecutiveDigits = 0;
            foreach (char character in value)
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

            ReadOnlySpan<char> suffix = value.AsSpan("24:00:00".Length);
            if (suffix.IsEmpty)
            {
                return true;
            }

            if (suffix[0] != '.')
            {
                return false;
            }

            foreach (char digit in suffix[1..])
            {
                if (digit != '0')
                {
                    return false;
                }
            }

            return true;
        }

        private static object DecodeBinaryDate(ReadOnlySpan<byte> bytes)
        {
            int days = BinaryPrimitives.ReadInt32BigEndian(bytes);
            if (days is int.MinValue or int.MaxValue)
            {
                return CassieTemporalValue.FromDateDays(days);
            }

            long dayNumber = (long)new DateOnly(2000, 1, 1).DayNumber + days;
            return dayNumber is >= 0 and <= 3_652_058
                ? DateOnly.FromDayNumber((int)dayNumber)
                : CassieTemporalValue.FromDateDays(days);
        }

        private static object DecodeBinaryTime(ReadOnlySpan<byte> bytes)
        {
            long microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
            return microseconds is < 0 or > 86_400_000_000
                ? throw new FormatException("Cassie returned an invalid binary time value.")
                : microseconds == 86_400_000_000
                ? CassieTemporalValue.FromTimeMicroseconds(microseconds)
                : new TimeOnly(microseconds * 10);
        }

        private static object DecodeBinaryTimeTz(ReadOnlySpan<byte> bytes)
        {
            long microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes[..8]);
            int zoneWest = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
            int offsetSeconds = zoneWest == int.MinValue
                ? throw new FormatException("Cassie returned an invalid binary time-zone offset.")
                : -zoneWest;
            return microseconds is < 0 or > 86_400_000_000
                ? throw new FormatException("Cassie returned an invalid binary time-with-time-zone value.")
                : microseconds == 86_400_000_000
                ? CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(microseconds, offsetSeconds)
                : new CassieTimeWithTimeZone(new TimeOnly(microseconds * 10), offsetSeconds);
        }

        private static CassieInterval DecodeBinaryInterval(ReadOnlySpan<byte> bytes)
        {
            long microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
            int days = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
            int months = BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
            return new CassieInterval(months, days, microseconds);
        }

        private static object DecodeBinaryTimestamp(ReadOnlySpan<byte> bytes)
        {
            long microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
            long epochTicks = new DateTime(2000, 1, 1).Ticks;
            long minimumMicroseconds = (DateTime.MinValue.Ticks - epochTicks) / 10;
            long maximumMicroseconds = (DateTime.MaxValue.Ticks - epochTicks) / 10;
            return microseconds is long.MinValue or long.MaxValue || microseconds < minimumMicroseconds || microseconds > maximumMicroseconds
                ? CassieTemporalValue.FromTimestampMicroseconds(microseconds)
                : new DateTime(epochTicks + (microseconds * 10), DateTimeKind.Unspecified);
        }

        private static object DecodeBinaryTimestampTz(ReadOnlySpan<byte> bytes)
        {
            long microseconds = BinaryPrimitives.ReadInt64BigEndian(bytes);
            long epochTicks = new DateTime(2000, 1, 1).Ticks;
            long minimumMicroseconds = (DateTimeOffset.MinValue.UtcTicks - epochTicks) / 10;
            long maximumMicroseconds = (DateTimeOffset.MaxValue.UtcTicks - epochTicks) / 10;
            return microseconds is long.MinValue or long.MaxValue || microseconds < minimumMicroseconds || microseconds > maximumMicroseconds
                ? CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(microseconds)
                : new DateTimeOffset(new DateTime(epochTicks + (microseconds * 10), DateTimeKind.Utc));
        }

        private static byte[] DecodeBytea(string value)
        {
            return !value.StartsWith("\\x", StringComparison.Ordinal) || (value.Length - 2) % 2 != 0
                ? throw new FormatException("Cassie returned bytea outside the supported hex format.")
                : Convert.FromHexString(value.AsSpan(2));
        }

    }
}
