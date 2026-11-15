using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Types.Handlers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;

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
                _ => CassieTypeHandlers.Find(oid)?.DecodeText(oid, value, dateStyle) ?? value
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

            if (CassieTypeHandlers.Find(oid)?.DecodeBinary(oid, value) is { } decoded)
            {
                return decoded;
            }

            ReadOnlySpan<byte> bytes = value;
            return oid switch
            {
                650 or 869 => DecodeBinaryNetwork(value, oid == 650),
                774 => DecodeBinaryMacAddress(value, 8),
                829 => DecodeBinaryMacAddress(value, 6),
                1560 or 1562 => DecodeBinaryBitString(bytes, oid == 1562),
                3220 when bytes.Length == 8 => new CassieLsn(BinaryPrimitives.ReadUInt64BigEndian(bytes)),
                _ => throw new NotSupportedException($"Binary results for PostgreSQL OID {oid} are not supported by this provider.")
            };
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

    }
}
