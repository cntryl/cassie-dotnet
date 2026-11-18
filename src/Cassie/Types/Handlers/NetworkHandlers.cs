using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cntryl.Cassie.Types.Handlers;

/// <summary>Handles inet and cidr, which differ only in the expected cidr flag.</summary>
internal sealed class NetworkAddressHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return DecodeBinaryNetwork(value, oid == 650);
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is CassieNetworkAddress address ? EncodeTextNetworkAddress(address, oid == 650) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value is CassieNetworkAddress address ? EncodeBinaryNetwork(address, oid == 650) : null;
    }

    private static CassieNetworkAddress DecodeBinaryNetwork(byte[] value, bool expectedCidr)
    {
        if (value.Length < 4)
        {
            throw new FormatException("Cassie returned a truncated binary network address.");
        }

        var family = value[0];
        var width = family switch
        {
            2 => 32,
            3 => 128,
            _ => throw new FormatException("Cassie returned an invalid binary network address family.")
        };
        var addressLength = family == 2 ? 4 : 16;
        int prefixLength = value[1];
        var isCidr = value[2] switch
        {
            0 => false,
            1 => true,
            _ => throw new FormatException("Cassie returned an invalid binary network address type flag.")
        };
        if (prefixLength > width || isCidr != expectedCidr || value[3] != addressLength ||
            value.Length != 4 + addressLength)
        {
            throw new FormatException("Cassie returned an invalid binary network address frame.");
        }

        ReadOnlySpan<byte> addressBytes = value.AsSpan(4);
        if (isCidr)
        {
            for (var bit = prefixLength; bit < width; bit++)
            {
                if ((addressBytes[bit / 8] & (1 << (7 - (bit % 8)))) != 0)
                {
                    throw new FormatException("Cassie returned a CIDR address with host bits set.");
                }
            }
        }

        return new CassieNetworkAddress(new IPAddress(addressBytes), prefixLength, isCidr);
    }

    internal static string EncodeTextNetworkAddress(CassieNetworkAddress address, bool expectedCidr)
    {
        return address.Address is null || address.IsCidr != expectedCidr
                                       || (address.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                           address.Address.ScopeId != 0)
            ? throw new ArgumentException("The network address value does not match the PostgreSQL type OID.",
                nameof(address))
            : address.ToString();
    }

    private static byte[] EncodeBinaryNetwork(CassieNetworkAddress address, bool expectedCidr)
    {
        if (address.Address is null || address.IsCidr != expectedCidr
                                    || (address.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                        address.Address.ScopeId != 0))
        {
            throw new ArgumentException("The network address value does not match the PostgreSQL type OID.",
                nameof(address));
        }

        var addressBytes = address.Address.GetAddressBytes();
        byte family = address.Address.AddressFamily switch
        {
            AddressFamily.InterNetwork => 2,
            AddressFamily.InterNetworkV6 => 3,
            _ => throw new ArgumentException("Only IPv4 and IPv6 network addresses are supported.", nameof(address))
        };
        var width = addressBytes.Length * 8;
        if ((uint)address.PrefixLength > (uint)width)
        {
            throw new ArgumentException("The network prefix length is outside the address family range.",
                nameof(address));
        }

        if (address.IsCidr)
        {
            for (var bit = address.PrefixLength; bit < width; bit++)
            {
                if ((addressBytes[bit / 8] & (1 << (7 - (bit % 8)))) != 0)
                {
                    throw new ArgumentException("CIDR parameters cannot contain host bits.", nameof(address));
                }
            }
        }

        return
        [
            family, checked((byte)address.PrefixLength), address.IsCidr ? (byte)1 : (byte)0,
            checked((byte)addressBytes.Length), .. addressBytes
        ];
    }
}

/// <summary>Handles macaddr and macaddr8.</summary>
internal sealed class MacAddressHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return DecodeBinaryMacAddress(value, oid == 774 ? 8 : 6);
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is PhysicalAddress address ? FormatTextMacAddress(address, oid == 774 ? 8 : 6) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value is PhysicalAddress address ? EncodeBinaryMacAddress(address, oid == 774 ? 8 : 6) : null;
    }

    private static PhysicalAddress DecodeBinaryMacAddress(byte[] value, int expectedLength)
    {
        return value.Length == expectedLength
            ? new PhysicalAddress(value)
            : throw new FormatException("Cassie returned an invalid binary MAC address frame.");
    }

    internal static string FormatTextMacAddress(PhysicalAddress address, int expectedLength)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == expectedLength
            ? string.Join(':', bytes.Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)))
            : throw new ArgumentException("The MAC address length does not match the PostgreSQL type OID.",
                nameof(address));
    }

    private static byte[] EncodeBinaryMacAddress(PhysicalAddress address, int expectedLength)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == expectedLength
            ? bytes
            : throw new ArgumentException("The MAC address length does not match the PostgreSQL type OID.",
                nameof(address));
    }
}

/// <summary>Handles bit and varbit.</summary>
internal sealed class BitStringHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return DecodeBinaryBitString(value, oid == 1562);
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is CassieBitString bitString ? EncodeTextBitString(bitString, oid == 1562) : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        return value is not CassieBitString bitString
            ? null
            : bitString.Value is not null && bitString.IsVarying == (oid == 1562)
                ? EncodeBinaryBitString(bitString)
                : throw new ArgumentException("The bit string value does not match the PostgreSQL type OID.",
                    nameof(value));
    }

    private static CassieBitString DecodeBinaryBitString(ReadOnlySpan<byte> bytes, bool isVarying)
    {
        var offset = 0;
        var bitLength = BinaryWire.ReadInt32(bytes, ref offset);
        if (bitLength < 0 || bitLength > (bytes.Length - 4) * 8)
        {
            throw new FormatException("Cassie returned an invalid binary bit-string length.");
        }

        var byteLength = (bitLength + 7) / 8;
        if (bytes.Length != 4 + byteLength)
        {
            throw new FormatException("Cassie returned an invalid binary bit-string frame.");
        }

        var bits = new char[bitLength];
        for (var bit = 0; bit < bitLength; bit++)
        {
            bits[bit] = (bytes[4 + (bit / 8)] & (1 << (7 - (bit % 8)))) == 0 ? '0' : '1';
        }

        return new CassieBitString(new string(bits), isVarying);
    }

    internal static string EncodeTextBitString(CassieBitString bitString, bool expectedVarying)
    {
        return bitString.Value is null || bitString.IsVarying != expectedVarying
            ? throw new ArgumentException("The bit string value does not match the PostgreSQL type OID.",
                nameof(bitString))
            : bitString.Value;
    }

    private static byte[] EncodeBinaryBitString(CassieBitString bitString)
    {
        if (bitString.Value is null)
        {
            throw new ArgumentException("A default bit-string value is invalid.", nameof(bitString));
        }

        var payloadLength = (bitString.Value.Length + 7) / 8;
        var bytes = new byte[4 + payloadLength];
        BinaryPrimitives.WriteInt32BigEndian(bytes, bitString.Value.Length);
        for (var bit = 0; bit < bitString.Value.Length; bit++)
        {
            if (bitString.Value[bit] == '1')
            {
                bytes[4 + (bit / 8)] |= (byte)(1 << (7 - (bit % 8)));
            }
        }

        return bytes;
    }
}

internal sealed class LsnHandler : CassieTypeHandler
{
    internal override object? DecodeBinary(int oid, byte[] value)
    {
        return value.Length == sizeof(ulong) ? new CassieLsn(BinaryPrimitives.ReadUInt64BigEndian(value)) : null;
    }

    internal override string? EncodeText(int oid, object value)
    {
        return value is CassieLsn lsn ? lsn.ToString() : null;
    }

    internal override byte[]? EncodeBinary(int oid, object value)
    {
        if (value is not CassieLsn lsn)
        {
            return null;
        }

        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, lsn.Value);
        return bytes;
    }
}

/// <summary>Encodes and decodes pgvector values independently of their database-specific OID.</summary>
internal static class VectorHandler
{
    internal static CassieVector DecodeBinary(ReadOnlySpan<byte> bytes, int typeModifier)
    {
        if (bytes.Length < 4)
        {
            throw new FormatException("Cassie returned a truncated binary vector header.");
        }

        int dimension = BinaryPrimitives.ReadInt16BigEndian(bytes);
        var reserved = BinaryPrimitives.ReadInt16BigEndian(bytes[2..]);
        if (dimension < 1 || (typeModifier >= 0 && dimension != typeModifier) || reserved != 0 ||
            bytes.Length != 4 + (dimension * sizeof(float)))
        {
            throw new FormatException("Cassie returned an invalid binary vector frame.");
        }

        var components = new float[dimension];
        for (var index = 0; index < dimension; index++)
        {
            components[index] =
                BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32BigEndian(bytes[(4 + (index * sizeof(float)))..]));
            if (!float.IsFinite(components[index]))
            {
                throw new FormatException("Cassie returned a non-finite binary vector component.");
            }
        }

        return new CassieVector(components);
    }

    internal static string EncodeText(CassieVector vector)
    {
        return "[" + string.Join(",", vector.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))) +
               "]";
    }

    internal static byte[] EncodeBinary(CassieVector vector)
    {
        var bytes = new byte[4 + (vector.Count * sizeof(float))];
        BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)vector.Dimension));
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(2), 0);
        for (var index = 0; index < vector.Count; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4 + (index * sizeof(float))),
                BitConverter.SingleToInt32Bits(vector[index]));
        }

        return bytes;
    }
}
