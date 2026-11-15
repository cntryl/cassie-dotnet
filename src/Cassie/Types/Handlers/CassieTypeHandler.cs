using Cntryl.Cassie.Connection;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Types.Handlers
{
    /// <summary>
    /// Owns the wire conversions of one PostgreSQL type, or of one family of closely related OIDs.
    /// Metadata such as CLR field types and format capabilities stays declared once in <see cref="CassieTypeMap"/>;
    /// the static members below expose it so a handler is the single entry point for a type.
    /// </summary>
    internal abstract class CassieTypeHandler
    {
        /// <summary>Decodes a text-format value. The default keeps the text, as for any type without a text codec.</summary>
        internal virtual object DecodeText(int oid, string value, CassieDateStyle? dateStyle)
        {
            return value;
        }

        /// <summary>
        /// Decodes a binary-format value. Returns null when the payload framing is not one this handler accepts,
        /// which the caller reports as an unsupported binary result. Malformed content throws.
        /// </summary>
        internal virtual object? DecodeBinary(int oid, byte[] value)
        {
            return null;
        }

        /// <summary>Encodes a parameter as text. Returns null when this handler does not recognise the value for the OID.</summary>
        internal virtual string? EncodeText(int oid, object value)
        {
            return null;
        }

        /// <summary>Encodes a parameter as binary. Returns null when this handler does not recognise the value for the OID.</summary>
        internal virtual byte[]? EncodeBinary(int oid, object value)
        {
            return null;
        }

        /// <summary>Gets the CLR type exposed for a column of this type in the given result format.</summary>
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type GetClrType(int oid, short formatCode)
        {
            return CassieTypeMap.GetClrType(oid, formatCode);
        }

        /// <summary>Gets whether results of this type can be requested in binary format.</summary>
        internal static bool SupportsBinaryResult(int oid)
        {
            return CassieTypeMap.SupportsBinaryResult(oid);
        }

        /// <summary>Gets whether a NULL parameter of this type can be sent in binary format.</summary>
        internal static bool SupportsBinaryNullParameter(int oid)
        {
            return CassieTypeMap.SupportsBinaryNullParameter(oid);
        }
    }
}
