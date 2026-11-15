using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Commands
{
    /// <summary>Represents one statement of a <see cref="CassieBatch"/>.</summary>
    /// <remarks>
    /// Parameters bind exactly as they do for <see cref="CassieCommand"/>: named <c>@markers</c> in text commands,
    /// or positional arguments of a <c>CALL</c> for stored procedures.
    /// </remarks>
    public sealed class CassieBatchCommand : DbBatchCommand
    {
        private string _commandText = string.Empty;

        /// <summary>Creates an empty batch command.</summary>
        public CassieBatchCommand()
        {
        }

        /// <summary>Creates a batch command with the given SQL text.</summary>
        /// <param name="commandText">The SQL text, or the procedure name for <see cref="CommandType.StoredProcedure"/>.</param>
        public CassieBatchCommand(string commandText)
        {
            CommandText = commandText;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get => _commandText; set => _commandText = value ?? string.Empty; }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; } = CommandType.Text;

        /// <summary>Gets the rows affected by this command in the most recent execution of its batch.</summary>
        /// <remarks>
        /// The value is the count the server reported, or -1 for a completed statement that reports no row count
        /// (such as <c>SELECT</c>). It is 0 when the command affected no rows, failed, or was skipped because an
        /// earlier command in the batch failed, as <see cref="DbBatchCommand.RecordsAffected"/> specifies. Each
        /// execution attempt resets it to 0, including one that fails local validation. Outside an explicit
        /// transaction the server may still roll back commands that completed before a failure, so a reported
        /// count is not proof that the change persisted.
        /// </remarks>
        public override int RecordsAffected => RecordsAffectedValue;

        /// <summary>Gets the parameters bound to this command.</summary>
        public new CassieParameterCollection Parameters { get; } = [];

        /// <inheritdoc />
        public override bool CanCreateParameter => true;

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => Parameters;

        internal int RecordsAffectedValue { get; set; }

        /// <summary>Gets the server statement prepared for this command, when its batch was prepared.</summary>
        internal PreparedStatementLease PreparedStatement { get; } = new();

        /// <inheritdoc />
        public override DbParameter CreateParameter()
        {
            return new CassieParameter();
        }

        internal CassiePreparedCommand PrepareParameters()
        {
            CassiePreparedCommand prepared = CommandParameterBinder.Prepare(CommandText, CommandType, Parameters);
            ParameterizedQueryBatch.ValidateParameterCount(prepared);
            return prepared;
        }
    }
}
