using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Cntryl.Cassie.Connection;

namespace Cntryl.Cassie.Types.Handlers;

internal sealed class BooleanHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return value switch
        {
            "t" or "true" or "TRUE" => true,
            "f" or "false" or "FALSE" => false,
            _ => throw new FormatException("Cassie returned an invalid text boolean value.")
        };
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == 1 && value[0] <= 1 ? value[0] == 1 : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value is bool boolean ? [boolean ? (byte)1 : (byte)0] : null;
    }
}

internal sealed class ByteaHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return !value.StartsWith("\\x", StringComparison.Ordinal) || (value.Length - 2) % 2 != 0
            ? throw new FormatException("Cassie returned bytea outside the supported hex format.")
            : Convert.FromHexString(value.AsSpan(2));
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value as byte[];
    }
}

internal sealed class Int8Handler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(long) ? BinaryPrimitives.ReadInt64BigEndian(value) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not long int8)
        {
            return null;
        }

        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, int8);
        return bytes;
    }
}

internal sealed class Int2Handler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return short.Parse(value, CultureInfo.InvariantCulture);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(short) ? BinaryPrimitives.ReadInt16BigEndian(value) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        short? int2 = value switch
        {
            short number => number,
            byte number => number,
            sbyte number => number,
            _ => null
        };
        if (int2 is not { } found)
        {
            return null;
        }

        var bytes = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(bytes, found);
        return bytes;
    }
}

internal sealed class Int4Handler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(int) ? BinaryPrimitives.ReadInt32BigEndian(value) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        int? int4 = value switch
        {
            int number => number,
            ushort number => number,
            _ => null
        };
        if (int4 is not { } found)
        {
            return null;
        }

        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, found);
        return bytes;
    }
}

internal sealed class Float4Handler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return float.Parse(value, CultureInfo.InvariantCulture);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(float)
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(value))
            : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not float float4)
        {
            return null;
        }

        var bytes = new byte[sizeof(float)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(float4));
        return bytes;
    }
}

internal sealed class Float8Handler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(double)
            ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(value))
            : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not double float8)
        {
            return null;
        }

        var bytes = new byte[sizeof(double)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(float8));
        return bytes;
    }
}

internal sealed class UuidHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return Guid.Parse(value);
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == 16 ? new Guid(value, true) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not Guid uuid)
        {
            return null;
        }

        var bytes = new byte[16];
        _ = uuid.TryWriteBytes(bytes, true, out _);
        return bytes;
    }
}

/// <summary>Handles the character-like types whose binary form is plain UTF-8 text.</summary>
internal sealed class StringHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        switch (value)
        {
            case string name when oid == 19:
                var nameBytes = Encoding.UTF8.GetBytes(name);
                return nameBytes.Length <= 63
                    ? nameBytes
                    : throw new ArgumentOutOfRangeException(nameof(value),
                        "Binary NAME parameters cannot exceed 63 UTF-8 bytes.");
            case string text:
                return Encoding.UTF8.GetBytes(text);
            case char character when oid != 114:
                return Encoding.UTF8.GetBytes(character.ToString());
            default:
                return null;
        }
    }
}

internal sealed class JsonbHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length > 0 && value[0] == 1
            ? Encoding.UTF8.GetString(value.AsSpan(1))
            : throw new FormatException("Cassie returned binary JSONB with an unsupported version.");
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value is string jsonb ? [1, .. Encoding.UTF8.GetBytes(jsonb)] : null;
    }
}

internal sealed class JsonPathHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == 0 || value[0] != 1
            ? throw new FormatException("Cassie returned binary JSONPath with an unsupported version.")
            : Encoding.UTF8.GetString(value.AsSpan(1));
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value is string jsonPath ? [1, .. Encoding.UTF8.GetBytes(jsonPath)] : null;
    }
}

internal sealed class VoidHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == 0
            ? string.Empty
            : throw new FormatException("Cassie returned binary void with a non-empty payload.");
    }
}

/// <summary>Handles oid and the reg* identifier types, which share one four-byte unsigned wire form.</summary>
internal sealed class ObjectIdentifierHandler : CassieTypeHandler
{
    internal override object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
    {
        return oid == 26 ? uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : value;
    }

    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(uint)
            ? BinaryPrimitives.ReadUInt32BigEndian(value)
            : throw new FormatException(
                $"Cassie returned a binary object identifier with length {value.Length} instead of 4.");
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not uint identifier)
        {
            return null;
        }

        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, identifier);
        return bytes;
    }
}
