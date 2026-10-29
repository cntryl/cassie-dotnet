using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie
{
    /// <summary>Selects the pgwire format used for an input parameter.</summary>
    public enum CassieParameterFormat
    {
        /// <summary>Use binary when this provider has a codec; otherwise use text.</summary>
        Auto,
        /// <summary>Send the provider's PostgreSQL text representation.</summary>
        Text,
        /// <summary>Require and send a supported PostgreSQL binary representation.</summary>
        Binary
    }

    /// <summary>Represents a parameter in a Cassie command.</summary>
    public sealed class CassieParameter : DbParameter
    {
        /// <inheritdoc />
        public override DbType DbType { get; set; } = DbType.Object;
        /// <inheritdoc />
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        /// <inheritdoc />
        public override bool IsNullable { get; set; }
        /// <inheritdoc />
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        /// <inheritdoc />
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        /// <inheritdoc />
        public override object? Value { get; set; }
        /// <inheritdoc />
        public override bool SourceColumnNullMapping { get; set; }
        /// <inheritdoc />
        public override int Size { get; set; }
        /// <summary>Gets or sets an explicit PostgreSQL type OID; zero lets Cassie infer the type. Negative values are rejected when the command is prepared.</summary>
        public int PostgresTypeOid { get; set; }
        /// <summary>Gets or sets the pgwire format used for this parameter. The default automatically selects binary when a codec is available.</summary>
        public CassieParameterFormat ParameterFormat { get; set; }
        /// <inheritdoc />
        public override void ResetDbType()
        {
            DbType = DbType.Object;
        }
    }
}
