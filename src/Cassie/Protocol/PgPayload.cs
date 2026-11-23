using System.Buffers.Binary;
using System.Text;

namespace Cntryl.Cassie.Protocol;

/// <summary>Bounds-checked big-endian readers over a received message payload.</summary>
internal static class PgPayload
{
    public static short ReadInt16(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset > bytes.Length - sizeof(short))
        {
            throw new IOException("Truncated pgwire frame.");
        }

        var value = BinaryPrimitives.ReadInt16BigEndian(bytes[offset..]);
        offset += sizeof(short);
        return value;
    }

    public static int ReadInt32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset > bytes.Length - sizeof(int))
        {
            throw new IOException("Truncated pgwire frame.");
        }

        var value = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
        offset += sizeof(int);
        return value;
    }

    public static string ReadCString(byte[] bytes, ref int offset)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var end = Array.IndexOf(bytes, (byte)0, offset);

        if (end < 0)
        {
            throw new IOException("Invalid null-terminated pgwire value.");
        }


        var value = Encoding.UTF8.GetString(bytes, offset, end - offset);
        offset = end + 1;
        return value;
    }
}
