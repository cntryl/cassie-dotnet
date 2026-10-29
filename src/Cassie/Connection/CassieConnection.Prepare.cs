using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Protocol;
using System.Data;
using System.Globalization;

namespace Cntryl.Cassie.Connection
{
    public sealed partial class CassieConnection
    {
        private const string UndefinedPreparedStatementSqlState = "26000";
        private readonly List<string> _preparedStatementsToClose = [];
        private int _nextPreparedStatementId;

        internal string PrepareServerStatement(CassiePreparedCommand prepared, int commandTimeout)
        {
            return PrepareServerStatementAsync(prepared, commandTimeout, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal async Task<string> PrepareServerStatementAsync(CassiePreparedCommand prepared, int commandTimeout, CancellationToken cancellationToken)
        {
            ValidateSqlText(prepared.Sql);
            ParameterizedQueryBatch.ValidateParameterCount(prepared);
            PgSession session = RequireSession("The connection must be open to prepare a command.");
            if (_gate.IsActive)
            {
                throw new InvalidOperationException("A reader is already active on this connection.");
            }

            string statementName = "cassie_dotnet_" + session.BackendProcessId.ToString(CultureInfo.InvariantCulture) + "_" + Interlocked.Increment(ref _nextPreparedStatementId).ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            _gate.Begin();
            try
            {
                using CommandDeadline deadline = new(commandTimeout, cancellationToken);
                CassieException? serverError = await RunSyncedRequestAsync(
                    session,
                    async () =>
                    {
                        await session.WriteFrameAsync(FrontendMessage.Parse, ExtendedQueryMessages.Parse(statementName, prepared.Sql, prepared.ParameterTypeOids), cancellationToken).ConfigureAwait(false);
                        await session.WriteFrameAsync(FrontendMessage.Describe, ExtendedQueryMessages.DescribeStatement(statementName), cancellationToken).ConfigureAwait(false);
                        await session.WriteFrameAsync(FrontendMessage.Sync, [], cancellationToken).ConfigureAwait(false);
                    },
                    deadline.Token).ConfigureAwait(false);
                return serverError is not null ? throw serverError : statementName;
            }
            finally
            {
                if (_state == ConnectionState.Open)
                {
                    _gate.End();
                }
            }
        }

        internal void CloseServerStatement(string statementName)
        {
            CloseServerStatementAsync(statementName, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal async Task CloseServerStatementAsync(string statementName, CancellationToken cancellationToken)
        {
            if (_state != ConnectionState.Open || _session is null)
            {
                return;
            }

            if (_gate.IsActive)
            {
                if (!_preparedStatementsToClose.Contains(statementName, StringComparer.Ordinal))
                {
                    _preparedStatementsToClose.Add(statementName);
                }

                return;
            }

            _gate.Begin();
            try
            {
                await CloseServerStatementCoreAsync(statementName, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (_state == ConnectionState.Open)
                {
                    await ReaderClosedAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task CloseServerStatementCoreAsync(string statementName, CancellationToken cancellationToken)
        {
            PgSession session = _session!;
            CassieException? serverError = await RunSyncedRequestAsync(
                session,
                async () =>
                {
                    await session.WriteFrameAsync(FrontendMessage.Close, ExtendedQueryMessages.CloseStatement(statementName), cancellationToken).ConfigureAwait(false);
                    await session.WriteFrameAsync(FrontendMessage.Sync, [], cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (serverError is not null && serverError.SqlState != UndefinedPreparedStatementSqlState)
            {
                throw serverError;
            }
        }
    }
}
