using System.Buffers.Binary;
using System.Text;

namespace Cntryl.Cassie.Protocol
{
    /// <summary>Big-endian writers for building frontend message payloads.</summary>
    internal static class PgWriter
    {
        public static void WriteInt16(Stream stream, short value)
        {
            ArgumentNullException.ThrowIfNull(stream);
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public static void WriteInt32(Stream stream, int value)
        {
            ArgumentNullException.ThrowIfNull(stream);
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public static void WriteCString(Stream stream, string value)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(value);
            stream.Write(Encoding.UTF8.GetBytes(value));
            stream.WriteByte(0);
        }

        /// <summary>Appends one complete frame (tag, length, payload) to a batch of frames.</summary>
        public static void AppendFrame(MemoryStream batch, byte tag, ReadOnlySpan<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(batch);
            batch.WriteByte(tag);
            WriteInt32(batch, checked(payload.Length + sizeof(int)));
            batch.Write(payload);
        }

        /// <summary>Appends the written portion of a payload buffer as one frame.</summary>
        public static void AppendFrame(MemoryStream batch, byte tag, MemoryStream payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            AppendFrame(batch, tag, payload.GetBuffer().AsSpan(0, (int)payload.Length));
        }
    }
}
