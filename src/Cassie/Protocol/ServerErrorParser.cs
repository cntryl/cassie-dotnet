namespace Cntryl.Cassie.Protocol
{
    /// <summary>Parses an ErrorResponse payload into a <see cref="CassieException"/>.</summary>
    internal static class ServerErrorParser
    {
        private const string FallbackMessage = "Cassie returned an error without a message.";

        public static CassieException Parse(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            Dictionary<byte, string> fields = [];
            int offset = 0;
            while (offset < payload.Length && payload[offset] != 0)
            {
                byte field = payload[offset++];
                fields[field] = PgPayload.ReadCString(payload, ref offset);
            }

            return new CassieException(
                fields.GetValueOrDefault((byte)'M') ?? FallbackMessage,
                fields.GetValueOrDefault((byte)'C'),
                severity: fields.GetValueOrDefault((byte)'V') ?? fields.GetValueOrDefault((byte)'S'),
                detail: fields.GetValueOrDefault((byte)'D'),
                hint: fields.GetValueOrDefault((byte)'H'),
                position: fields.GetValueOrDefault((byte)'P'),
                schemaName: fields.GetValueOrDefault((byte)'s'),
                tableName: fields.GetValueOrDefault((byte)'t'),
                columnName: fields.GetValueOrDefault((byte)'c'),
                constraintName: fields.GetValueOrDefault((byte)'n'),
                file: fields.GetValueOrDefault((byte)'F'),
                line: fields.GetValueOrDefault((byte)'L'),
                routine: fields.GetValueOrDefault((byte)'R'));
        }
    }
}
