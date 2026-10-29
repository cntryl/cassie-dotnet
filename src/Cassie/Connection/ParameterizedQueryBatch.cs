using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Protocol;
using Cntryl.Cassie.Types;
using System.Security.Cryptography;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Builds the extended-protocol message batch for one parameterized command.</summary>
    internal static class ParameterizedQueryBatch
    {
        /// <summary>Appends Parse (when unnamed), Describe, and Sync for a schema-only request.</summary>
        public static void AppendSchemaRequest(MemoryStream batch, CassiePreparedCommand prepared, string? statementName)
        {
            AppendParse(batch, prepared, statementName);
            using MemoryStream describe = ExtendedQueryMessages.DescribeStatement(statementName ?? string.Empty);
            PgWriter.AppendFrame(batch, FrontendMessage.Describe, describe);
            PgWriter.AppendFrame(batch, FrontendMessage.Sync, []);
        }

        /// <summary>Appends Parse (when unnamed), Bind, Describe, and the first portal page for an execution request.</summary>
        public static void AppendExecuteRequest(MemoryStream batch, CassiePreparedCommand prepared, string? statementName, bool binaryResults, byte[] portalPageFrames, EncodedParameters? encodedParameters = null)
        {
            encodedParameters ??= EncodeParameters(prepared);
            AppendParse(batch, prepared, statementName);
            using (MemoryStream bind = new())
            {
                try
                {
                    BuildBind(bind, encodedParameters, statementName, binaryResults);
                    PgWriter.AppendFrame(batch, FrontendMessage.Bind, bind);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bind.GetBuffer());
                }
            }

            using MemoryStream describe = ExtendedQueryMessages.DescribeUnnamedPortal();
            PgWriter.AppendFrame(batch, FrontendMessage.Describe, describe);
            batch.Write(portalPageFrames);
        }

        private static void AppendParse(MemoryStream batch, CassiePreparedCommand prepared, string? statementName)
        {
            if (statementName is not null)
            {
                return;
            }

            using MemoryStream parse = ExtendedQueryMessages.Parse(string.Empty, prepared.Sql, prepared.ParameterTypeOids);
            PgWriter.AppendFrame(batch, FrontendMessage.Parse, parse);
        }

        /// <summary>Validates every parameter before any execution or automatic re-preparation uses the session.</summary>
        public static EncodedParameters EncodeParameters(CassiePreparedCommand prepared)
        {
            ValidateParameterCount(prepared);
            short[] formats = new short[prepared.Values.Count];
            byte[]?[] values = new byte[prepared.Values.Count][];
            for (int index = 0; index < prepared.Values.Count; index++)
            {
                byte[]? binary = EncodeBinary(prepared, index);
                formats[index] = binary is null ? (short)0 : (short)1;
                object? value = prepared.Values[index];
                values[index] = value is null or DBNull
                    ? null
                    : binary ?? CassieParameterCodec.EncodeTextParameter(value, prepared.ParameterTypeOids[index]);
            }

            return new EncodedParameters(formats, values);
        }

        public static void ValidateParameterCount(CassiePreparedCommand prepared)
        {
            if (prepared.Values.Count > short.MaxValue || prepared.ParameterTypeOids.Count > short.MaxValue)
            {
                throw new NotSupportedException("The pgwire protocol limits one command to 32767 parameters.");
            }
        }

        private static void BuildBind(MemoryStream bind, EncodedParameters parameters, string? statementName, bool binaryResults)
        {
            PgWriter.WriteCString(bind, string.Empty);
            PgWriter.WriteCString(bind, statementName ?? string.Empty);
            PgWriter.WriteInt16(bind, checked((short)parameters.Formats.Count));
            foreach (short format in parameters.Formats)
            {
                PgWriter.WriteInt16(bind, format);
            }

            PgWriter.WriteInt16(bind, checked((short)parameters.Values.Count));
            foreach (byte[]? bytes in parameters.Values)
            {
                if (bytes is null)
                {
                    PgWriter.WriteInt32(bind, -1);
                    continue;
                }

                PgWriter.WriteInt32(bind, bytes.Length);
                bind.Write(bytes);
            }

            PgWriter.WriteInt16(bind, binaryResults ? (short)1 : (short)0);
            if (binaryResults)
            {
                PgWriter.WriteInt16(bind, 1);
            }
        }

        private static byte[]? EncodeBinary(CassiePreparedCommand prepared, int index)
        {
            CassieParameterFormat requestedFormat = prepared.ParameterFormats is { } formats ? formats[index] : CassieParameterFormat.Auto;
            if (!Enum.IsDefined(requestedFormat))
            {
                throw new ArgumentOutOfRangeException(nameof(prepared), "A parameter format is invalid.");
            }

            if (requestedFormat == CassieParameterFormat.Text)
            {
                return null;
            }

            byte[]? binary = CassieParameterCodec.GetBinaryParameter(prepared.Values[index], prepared.ParameterTypeOids[index]);
            return requestedFormat == CassieParameterFormat.Binary && binary is null
                ? throw new NotSupportedException($"Binary parameters for PostgreSQL OID {prepared.ParameterTypeOids[index]} are not supported by this provider.")
                : binary;
        }
    }
}
