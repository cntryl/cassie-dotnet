using System.Text;

namespace Cntryl.Cassie.Protocol;

/// <summary>Parsers for RowDescription and DataRow payloads.</summary>
internal static class RowMessages
{
    public static FieldDescription[] ParseRowDescription(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var offset = 0;
        var count = PgPayload.ReadInt16(payload, ref offset);
        if (count < 0)
        {
            throw new IOException("RowDescription contained an invalid negative column count.");
        }

        var fields = new FieldDescription[count];
        for (var i = 0; i < count; i++)
        {
            var name = PgPayload.ReadCString(payload, ref offset);
            var tableOid = unchecked((uint)PgPayload.ReadInt32(payload, ref offset));
            var attributeNumber = PgPayload.ReadInt16(payload, ref offset);
            var typeOid = PgPayload.ReadInt32(payload, ref offset);
            var typeSize = PgPayload.ReadInt16(payload, ref offset);
            var typeModifier = PgPayload.ReadInt32(payload, ref offset);
            var format = PgPayload.ReadInt16(payload, ref offset);
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

    /// <summary>
    ///     Validates a DataRow and records where each field's bytes sit in the payload, without decoding them.
    ///     A NULL field has length -1.
    /// </summary>
    public static void IndexDataRow(byte[] payload, Span<int> fieldOffsets, Span<int> fieldLengths)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var offset = 0;
        var count = PgPayload.ReadInt16(payload, ref offset);
        if (count != fieldOffsets.Length || count != fieldLengths.Length)
        {
            throw new IOException("DataRow column count did not match RowDescription.");
        }

        for (var i = 0; i < count; i++)
        {
            var length = PgPayload.ReadInt32(payload, ref offset);
            if (length < -1)
            {
                throw new IOException("DataRow contained an invalid negative field length.");
            }

            fieldOffsets[i] = offset;
            fieldLengths[i] = length;
            if (length == -1)
            {
                continue;
            }

            if (offset > payload.Length - length)
            {
                throw new IOException("Truncated DataRow value.");
            }

            offset += length;
        }

        if (offset != payload.Length)
        {
            throw new IOException("DataRow payload contained trailing bytes.");
        }
    }

    /// <summary>Decodes one indexed field into a text string or a binary byte array according to its format code.</summary>
    public static object DecodeField(byte[] payload, int offset, int length, short formatCode)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return formatCode switch
        {
            0 => Encoding.UTF8.GetString(payload, offset, length),
            1 => payload.AsSpan(offset, length).ToArray(),
            _ => throw new IOException($"Cassie returned unsupported result format {formatCode}.")
        };
    }
}
