using System.Data.Common;

namespace Cntryl.Cassie
{
    /// <summary>Represents an error returned by Cassie.</summary>
    public sealed class CassieException(
        string message,
        string? sqlState = null,
        Exception? innerException = null,
        string? severity = null,
        string? detail = null,
        string? hint = null,
        string? position = null,
        string? schemaName = null,
        string? tableName = null,
        string? columnName = null,
        string? constraintName = null,
        string? file = null,
        string? line = null,
        string? routine = null) : DbException(message, innerException)
    {
        /// <summary>Gets the five-character SQLSTATE returned by Cassie, when available.</summary>
        public override string? SqlState { get; } = sqlState;

        /// <summary>Gets whether retrying the operation, possibly on a new connection, may succeed.</summary>
        public override bool IsTransient => SqlState is not null
            && (SqlState.StartsWith("08", StringComparison.Ordinal)
                || SqlState.StartsWith("53", StringComparison.Ordinal)
                || SqlState is "40001" or "40P01" or "57P01" or "57P02" or "57P03");

        /// <summary>Gets the server error severity.</summary>
        public string? Severity { get; } = severity;

        /// <summary>Gets additional server error detail.</summary>
        public string? Detail { get; } = detail;

        /// <summary>Gets a server-provided suggestion for resolving the error.</summary>
        public string? Hint { get; } = hint;

        /// <summary>Gets the character position in the query text, when available.</summary>
        public string? Position { get; } = position;

        /// <summary>Gets the schema associated with the error.</summary>
        public string? SchemaName { get; } = schemaName;

        /// <summary>Gets the table associated with the error.</summary>
        public string? TableName { get; } = tableName;

        /// <summary>Gets the column associated with the error.</summary>
        public string? ColumnName { get; } = columnName;

        /// <summary>Gets the constraint associated with the error.</summary>
        public string? ConstraintName { get; } = constraintName;

        /// <summary>Gets the server source file.</summary>
        public string? File { get; } = file;

        /// <summary>Gets the server source line.</summary>
        public string? Line { get; } = line;

        /// <summary>Gets the server routine.</summary>
        public string? Routine { get; } = routine;

        /// <summary>Creates an exception with an inner exception and no SQLSTATE.</summary>
        public CassieException(string message, Exception innerException) : this(message, null, innerException) { }

        /// <summary>Creates an exception with no message.</summary>
        public CassieException() : this("Cassie returned an unspecified error.", null, null) { }

    }
}
