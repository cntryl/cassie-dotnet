using System.Diagnostics.CodeAnalysis;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Types.Handlers;

namespace Cntryl.Cassie.Types;

internal static class CassieResultCodec
{
    [return:
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
                                   DynamicallyAccessedMemberTypes.PublicFields)]
    internal static Type GetClrType(int oid)
    {
        return CassieTypeMap.GetClrType(oid);
    }

    [return:
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
                                   DynamicallyAccessedMemberTypes.PublicFields)]
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
        return CassieTypeHandlers.Find(oid)?.DecodeText(oid, value, dateStyle) ?? value;
    }

    internal static object ConvertBinaryValue(byte[] value, int oid)
    {
        var decoded = !CassieTypeMap.SupportsBinaryResult(oid)
            ? null
            : CassieTypeMap.GetArrayElementOid(oid) is int elementTypeOid
                ? DecodeBinaryArray(value, elementTypeOid)
                : CassieTypeHandlers.Find(oid)?.DecodeBinary(oid, value);
        return decoded ??
               throw new NotSupportedException(
                   $"Binary results for PostgreSQL OID {oid} are not supported by this provider.");
    }

    private static CassieArray DecodeBinaryArray(byte[] value, int expectedElementTypeOid)
    {
        ReadOnlySpan<byte> bytes = value;
        var offset = 0;
        var rank = BinaryWire.ReadInt32(bytes, ref offset);
        var hasNull = BinaryWire.ReadInt32(bytes, ref offset);
        var elementTypeOid = BinaryWire.ReadInt32(bytes, ref offset);

        if (rank is < 0 or > 6 || hasNull is not 0 and not 1 || elementTypeOid != expectedElementTypeOid)
        {
            throw new FormatException("Cassie returned an invalid binary array header.");
        }


        if (rank == 0 && hasNull != 0)
        {
            throw new FormatException("Cassie returned an empty binary array with a null flag.");
        }

        var dimensions = new int[rank];
        var lowerBounds = new int[rank];
        long elementCount = rank == 0 ? 0 : 1;

        for (var dimension = 0; dimension < rank; dimension++)
        {
            var length = BinaryWire.ReadInt32(bytes, ref offset);
            var lowerBound = BinaryWire.ReadInt32(bytes, ref offset);

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

            if (elementCount > bytes.Length / sizeof(int))
            {
                throw new FormatException("Cassie returned binary array dimensions larger than the available payload.");
            }
        }


        var elements = new object?[checked((int)elementCount)];
        var foundNull = false;

        for (var index = 0; index < elements.Length; index++)
        {
            var length = BinaryWire.ReadInt32(bytes, ref offset);

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
}
