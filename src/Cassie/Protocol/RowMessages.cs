using System.Text;

namespace Cntryl.Cassie.Protocol
{
    /// <summary>Parsers for RowDescription and DataRow payloads.</summary>
    internal static class RowMessages
    {
        public static FieldDescription[] ParseRowDescription(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            int offset = 0;
            short count = PgPayload.ReadInt16(payload, ref offset);
            if (count < 0)
            {
                throw new IOException("RowDescription contained an invalid negative column count.");
            }

            FieldDescription[] fields = new FieldDescription[count];
            for (int i = 0; i < count; i++)
            {
                string name = PgPayload.ReadCString(payload, ref offset);
                uint tableOid = unchecked((uint)PgPayload.ReadInt32(payload, ref offset));
                short attributeNumber = PgPayload.ReadInt16(payload, ref offset);
                int typeOid = PgPayload.ReadInt32(payload, ref offset);
                short typeSize = PgPayload.ReadInt16(payload, ref offset);
                int typeModifier = PgPayload.ReadInt32(payload, ref offset);
                short format = PgPayload.ReadInt16(payload, ref offset);
                if (format is not (0 or 1))
                {
                    throw new IOException($"RowDescription column '{name}' used unsupported format code {format}.");
                }

                fields[i] = new FieldDescription(name, tableOid, attributeNumber, typeOid, typeSize, typeModifier, format);
            }

            return offset == payload.Length
                ? fields
                : throw new IOException("RowDescription contained trailing payload bytes.");
        }

        /// <summary>Parses a DataRow into text strings or binary byte arrays according to each column's format code.</summary>
        public static object?[] ParseDataRow(byte[] payload, ReadOnlySpan<short> formatCodes)
        {
            ArgumentNullException.ThrowIfNull(payload);
            int offset = 0;
            short count = PgPayload.ReadInt16(payload, ref offset);
            if (count != formatCodes.Length)
            {
                throw new IOException("DataRow column count did not match RowDescription.");
            }

            object?[] row = new object?[count];
            for (int i = 0; i < count; i++)
            {
                int length = PgPayload.ReadInt32(payload, ref offset);
                if (length == -1)
                {
                    continue;
                }

                if (length < -1)
                {
                    throw new IOException("DataRow contained an invalid negative field length.");
                }

                if (offset > payload.Length - length)
                {
                    throw new IOException("Truncated DataRow value.");
                }

                row[i] = formatCodes[i] switch
                {
                    0 => Encoding.UTF8.GetString(payload, offset, length),
                    1 => payload.AsSpan(offset, length).ToArray(),
                    _ => throw new IOException($"Cassie returned unsupported result format {formatCodes[i]}.")
                };
                offset += length;
            }

            return offset == payload.Length
                ? row
                : throw new IOException("DataRow payload contained trailing bytes.");
        }
    }
}
