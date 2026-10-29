namespace Cntryl.Cassie.Protocol
{
    /// <summary>Payload builders for the extended query protocol messages that carry no bound values.</summary>
    internal static class ExtendedQueryMessages
    {
        private const byte StatementTarget = (byte)'S';
        private const byte PortalTarget = (byte)'P';

        public static MemoryStream Parse(string statementName, string sql, IReadOnlyList<int> parameterTypeOids)
        {
            ArgumentNullException.ThrowIfNull(parameterTypeOids);
            MemoryStream payload = new();
            PgWriter.WriteCString(payload, statementName);
            PgWriter.WriteCString(payload, sql);
            PgWriter.WriteInt16(payload, checked((short)parameterTypeOids.Count));
            foreach (int typeOid in parameterTypeOids)
            {
                PgWriter.WriteInt32(payload, typeOid);
            }

            return payload;
        }

        public static MemoryStream DescribeStatement(string statementName)
        {
            return Target(StatementTarget, statementName);
        }

        public static MemoryStream DescribeUnnamedPortal()
        {
            return Target(PortalTarget, string.Empty);
        }

        public static MemoryStream CloseStatement(string statementName)
        {
            return Target(StatementTarget, statementName);
        }

        /// <summary>Builds the Execute-plus-Flush frames that fetch the next page of the unnamed portal.</summary>
        public static byte[] ExecutePageFrames(int fetchSize)
        {
            using MemoryStream frames = new();
            using MemoryStream execute = new();
            PgWriter.WriteCString(execute, string.Empty);
            PgWriter.WriteInt32(execute, fetchSize);
            PgWriter.AppendFrame(frames, (byte)'E', execute);
            PgWriter.AppendFrame(frames, (byte)'H', []);
            return frames.ToArray();
        }

        private static MemoryStream Target(byte kind, string name)
        {
            MemoryStream payload = new();
            payload.WriteByte(kind);
            PgWriter.WriteCString(payload, name);
            return payload;
        }
    }
}
