using System.Buffers.Binary;

namespace Cntryl.Cassie.Types.Handlers
{
    /// <summary>Bounds-checked big-endian reads shared by structured binary decoders.</summary>
    internal static class BinaryWire
    {
        internal static int ReadInt32(ReadOnlySpan<byte> bytes, ref int offset)
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
