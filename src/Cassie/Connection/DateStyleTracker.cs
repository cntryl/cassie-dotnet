using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection
{
    /// <summary>
    /// Tracks the session DateStyle and the transaction status that decide when it must be re-read from the server.
    /// </summary>
    internal sealed class DateStyleTracker
    {
        private bool _transactionMayHaveChangedDateStyle;

        /// <summary>Gets the current DateStyle, or null when the server reported a value this provider cannot parse.</summary>
        public CassieDateStyle? Current { get; private set; } = CassieDateStyle.Default;

        /// <summary>Gets the DateStyle established at startup, which <c>DISCARD ALL</c> restores.</summary>
        public CassieDateStyle Startup { get; private set; } = CassieDateStyle.Default;

        private bool Refreshing { get; set; }

        public bool NeedsRefresh { get; private set; }

        public byte TransactionStatus { get; private set; } = Protocol.TransactionStatus.Idle;

        /// <summary>Gets whether a SHOW DateStyle round trip is due and safe to start.</summary>
        public bool CanRefresh => NeedsRefresh && !Refreshing && TransactionStatus != Protocol.TransactionStatus.Failed;

        /// <summary>Records the DateStyle in force once startup completes.</summary>
        public void CompleteStartup()
        {
            Startup = Current ?? CassieDateStyle.Default;
        }

        /// <summary>Clears per-lease state when a pooled session is handed to a new connection.</summary>
        public void BeginLease()
        {
            NeedsRefresh = false;
            TransactionStatus = Protocol.TransactionStatus.Idle;
            _transactionMayHaveChangedDateStyle = false;
        }

        public void ApplyParameterStatus(string name, string value)
        {
            if (string.Equals(name, "DateStyle", StringComparison.OrdinalIgnoreCase))
            {
                Current = CassieDateStyle.TryParse(value, out CassieDateStyle style) ? style : null;
                NeedsRefresh = false;
            }
        }

        /// <summary>Records the transaction status; returns false when the payload is not a valid status.</summary>
        public bool TryObserveReadyForQuery(ReadOnlySpan<byte> payload)
        {
            if (payload.Length != 1 || !Protocol.TransactionStatus.IsValid(payload[0]))
            {
                return false;
            }

            byte prior = TransactionStatus;
            TransactionStatus = payload[0];
            if ((TransactionStatus == Protocol.TransactionStatus.Idle && prior != Protocol.TransactionStatus.Idle)
                || (TransactionStatus == Protocol.TransactionStatus.InTransaction && prior == Protocol.TransactionStatus.Idle))
            {
                _transactionMayHaveChangedDateStyle = false;
            }

            return true;
        }

        /// <summary>Notes that a statement may have changed a session setting, so DateStyle must be re-read.</summary>
        public void NoteSettingChange()
        {
            MarkNeedsRefresh();
            _transactionMayHaveChangedDateStyle |= TransactionStatus == Protocol.TransactionStatus.InTransaction;
        }

        /// <summary>Notes that SQL text may change settings through <c>set_config</c>.</summary>
        public void ObserveSql(string sql)
        {
            ArgumentNullException.ThrowIfNull(sql);
            if (SqlLexicalTraversal.ContainsFunctionCall(sql, "set_config"))
            {
                NoteSettingChange();
            }
        }

        public void ObserveCommandComplete(CommandTag tag)
        {
            if (tag.HasVerb("SET") || tag.HasVerb("RESET"))
            {
                NoteSettingChange();
            }
            else if (tag.HasVerb("COMMIT") || tag.HasVerb("END") || tag.HasVerb("ROLLBACK") || tag.HasVerb("ABORT"))
            {
                if (_transactionMayHaveChangedDateStyle)
                {
                    MarkNeedsRefresh();
                }
            }
            else if (tag.Matches("DISCARD ALL"))
            {
                Current = Startup;
                NeedsRefresh = false;
                _transactionMayHaveChangedDateStyle = false;
            }
        }

        public void BeginRefresh()
        {
            Refreshing = true;
        }

        public void EndRefresh()
        {
            Refreshing = false;
        }

        private void MarkNeedsRefresh()
        {
            Current = null;
            NeedsRefresh = true;
        }
    }
}
