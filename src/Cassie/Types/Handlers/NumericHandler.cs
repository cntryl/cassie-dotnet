using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Types.Handlers;

internal sealed class NumericHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return new CassieNumeric(value);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return DecodeBinaryNumeric(value);
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value switch
        {
            CassieNumeric numeric => EncodeBinaryNumeric(numeric.Value),
            decimal numeric => EncodeBinaryNumeric(numeric.ToString(CultureInfo.InvariantCulture)),
            ulong numeric => EncodeBinaryNumeric(numeric.ToString(CultureInfo.InvariantCulture)),
            _ => null
        };
    }

    private static CassieNumeric DecodeBinaryNumeric(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            throw new FormatException("Cassie returned a truncated binary numeric header.");
        }

        int digitCount = BinaryPrimitives.ReadInt16BigEndian(bytes);
        int weight = BinaryPrimitives.ReadInt16BigEndian(bytes[2..]);
        var sign = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        int scale = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);

        if (digitCount < 0 || bytes.Length != 8 + (digitCount * sizeof(ushort)))
        {
            throw new FormatException("Cassie returned invalid binary numeric framing.");
        }


        if (sign is 0xC000 or 0xD000 or 0xF000)
        {
            var special = sign switch
            {
                0xC000 => "NaN",
                0xD000 => "Infinity",
                _ => "-Infinity"
            };
            return digitCount == 0
                ? new CassieNumeric(special)
                : throw new FormatException("Cassie returned digits for a special numeric value.");
        }

        if (sign is not 0x0000 and not 0x4000 || scale > 16383)
        {
            throw new FormatException("Cassie returned an invalid binary numeric sign or scale.");
        }

        var digits = new int[digitCount];
        var nonzero = false;

        for (var index = 0; index < digitCount; index++)
        {
            int digit = BinaryPrimitives.ReadUInt16BigEndian(bytes[(8 + (index * sizeof(ushort)))..]);

            if (digit > 9999)
            {
                throw new FormatException("Cassie returned an out-of-range binary numeric digit.");
            }


            digits[index] = digit;
            nonzero |= digit != 0;
        }


        var fractionalGroups = (scale + 3) / 4;
        var integerGroups = Math.Max(0, weight + 1);
        StringBuilder text = new(checked(Math.Max(1, integerGroups * 4) + (scale == 0 ? 0 : scale + 1) + 1));

        if (sign == 0x4000 && nonzero)
        {
            _ = text.Append('-');
        }


        var firstInteger = ReadNumericBase10000Digit(digits, weight, weight);

        if (integerGroups == 0)
        {
            _ = text.Append('0');
        }
        else
        {
            _ = text.Append(firstInteger.ToString(CultureInfo.InvariantCulture));

            for (var group = 1; group < integerGroups; group++)
            {
                var position = weight - group;
                _ = text.Append(ReadNumericBase10000Digit(digits, weight, position)
                    .ToString("D4", CultureInfo.InvariantCulture));
            }
        }


        if (scale > 0)
        {
            _ = text.Append('.');

            for (var group = 1; group <= fractionalGroups; group++)
            {
                var position = -group;
                _ = text.Append(ReadNumericBase10000Digit(digits, weight, position)
                    .ToString("D4", CultureInfo.InvariantCulture));
            }


            text.Length -= (fractionalGroups * 4) - scale;
        }

        return new CassieNumeric(text.ToString());
    }

    private static int ReadNumericBase10000Digit(ReadOnlySpan<int> digits, int weight, int position)
    {
        var index = weight - position;
        return (uint)index < (uint)digits.Length ? digits[index] : 0;
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

        var index = 0;
        var negative = value.Length > 0 && value[0] == '-';

        if (negative || (value.Length > 0 && value[0] == '+'))
        {
            index++;
        }


        var exponent = 0;
        var exponentIndex = value.IndexOfAny(['e', 'E'], index);
        var mantissa = exponentIndex < 0 ? value.AsSpan(index) : value.AsSpan(index, exponentIndex - index);

        if (exponentIndex >= 0 && !int.TryParse(value.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent))
        {
            throw new FormatException("CassieNumeric exponent is outside the supported range.");
        }


        var point = mantissa.IndexOf('.');
        var integerPart = point < 0 ? mantissa : mantissa[..point];
        var fractionalPart = point < 0 ? [] : mantissa[(point + 1)..];
        var decimalScale = checked(fractionalPart.Length - exponent);

        if (decimalScale > 16383 || integerPart.Length + fractionalPart.Length + Math.Max(0, -decimalScale) > 131072)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                "The numeric value exceeds Cassie's binary NUMERIC limits.");
        }


        var coefficient = integerPart.ToString() + fractionalPart.ToString();

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

        var integerDigitCount = coefficient.Length - decimalScale;
        var integerDigits = coefficient[..integerDigitCount];
        var fractionDigits = coefficient[integerDigitCount..];
        scale = checked((ushort)decimalScale);

        var integerPadding = (4 - (integerDigits.Length % 4)) % 4;
        var fractionPadding = (4 - (fractionDigits.Length % 4)) % 4;
        integerDigits = new string('0', integerPadding) + integerDigits;
        fractionDigits += new string('0', fractionPadding);
        List<ushort> digits = [];

        for (var start = 0; start < integerDigits.Length; start += 4)
        {
            digits.Add(ushort.Parse(integerDigits.AsSpan(start, 4), CultureInfo.InvariantCulture));
        }


        for (var start = 0; start < fractionDigits.Length; start += 4)
        {
            digits.Add(ushort.Parse(fractionDigits.AsSpan(start, 4), CultureInfo.InvariantCulture));
        }

        var weight = (integerDigits.Length / 4) - 1;
        var leading = 0;

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

    private static byte[] EncodeNumericHeader(List<ushort> digits, short weight, ushort sign, ushort scale)
    {
        using MemoryStream payload = new(8 + (digits.Count * sizeof(ushort)));
        PgWriter.WriteInt16(payload, checked((short)digits.Count));
        PgWriter.WriteInt16(payload, weight);
        WriteUInt16(payload, sign);
        WriteUInt16(payload, scale);

        foreach (var digit in digits)
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
}
