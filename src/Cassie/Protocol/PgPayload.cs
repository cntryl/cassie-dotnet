using System.Buffers.Binary;
using System.Text;

namespace Cntryl.Cassie.Protocol
{
    /// <summary>Bounds-checked big-endian readers over a received message payload.</summary>
    internal static class PgPayload
    {
        public static short ReadInt16(byte[] bytes, ref int offset)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            if (offset > bytes.Length - sizeof(short))
            {
                throw new IOException("Truncated pgwire frame.");
            }

            short value = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset));
            offset += sizeof(short);
            return value;
        }

        public static int ReadInt32(byte[] bytes, ref int offset)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            if (offset > bytes.Length - sizeof(int))
            {
                throw new IOException("Truncated pgwire frame.");
            }

            int value = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
            return value;
        }

        public static string ReadCString(byte[] bytes, ref int offset)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            int end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < 0)
            {
                throw new IOException("Invalid null-terminated pgwire value.");
            }

            string value = Encoding.UTF8.GetString(bytes, offset, end - offset);
            offset = end + 1;
            return value;
        }
    }
}
