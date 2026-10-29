using Cntryl.Cassie.Connection;
using System.Data;
using System.Net.Sockets;

namespace Cntryl.Cassie.Commands
{
    /// <summary>Owns a command's server statement identity and delegates wire cleanup to its session owner.</summary>
    internal sealed class PreparedStatementLease
    {
        private CassieConnection? _connection;
        private Guid _sessionIdentity;
        private string? _statementName;
        private string? _sql;
        private int[]? _parameterTypeOids;

        public bool IsRequested { get; private set; }

        public void Request()
        {
            IsRequested = true;
        }

        public string EnsurePrepared(CassieConnection connection, CassiePreparedCommand prepared, int commandTimeout)
        {
            if (Matches(connection, prepared))
            {
                return _statementName!;
            }

            Release();
            string statementName = connection.PrepareServerStatement(prepared, commandTimeout);
            Remember(connection, prepared, statementName);
            return statementName;
        }

        public async Task<string> EnsurePreparedAsync(CassieConnection connection, CassiePreparedCommand prepared, int commandTimeout, CancellationToken cancellationToken)
        {
            if (Matches(connection, prepared))
            {
                return _statementName!;
            }

            await ReleaseAsync(cancellationToken).ConfigureAwait(false);
            string statementName = await connection.PrepareServerStatementAsync(prepared, commandTimeout, cancellationToken).ConfigureAwait(false);
            Remember(connection, prepared, statementName);
            return statementName;
        }

        public void Release()
        {
            try
            {
                if (CanRelease())
                {
                    _connection!.CloseServerStatement(_statementName!);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or CassieException or InvalidOperationException or TimeoutException)
            {
                // Disposal is best-effort; the connection owns transport failure and active-reader deferral.
            }
            finally
            {
                Clear();
            }
        }

        public async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (CanRelease())
                {
                    await _connection!.CloseServerStatementAsync(_statementName!, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or CassieException or InvalidOperationException or TimeoutException)
            {
                // Disposal is best-effort; the connection owns transport failure and active-reader deferral.
            }
            finally
            {
                Clear();
            }
        }

        private bool Matches(CassieConnection connection, CassiePreparedCommand prepared)
        {
            return ReferenceEquals(connection, _connection)
                && connection.SessionIdentity == _sessionIdentity
                && _statementName is not null
                && _sql == prepared.Sql
                && _parameterTypeOids is not null
                && _parameterTypeOids.SequenceEqual(prepared.ParameterTypeOids);
        }

        private bool CanRelease()
        {
            return _statementName is not null
                && _connection?.State == ConnectionState.Open
                && _connection.SessionIdentity == _sessionIdentity;
        }

        private void Remember(CassieConnection connection, CassiePreparedCommand prepared, string statementName)
        {
            _connection = connection;
            _sessionIdentity = connection.SessionIdentity;
            _statementName = statementName;
            _sql = prepared.Sql;
            _parameterTypeOids = [.. prepared.ParameterTypeOids];
        }

        private void Clear()
        {
            _connection = null;
            _sessionIdentity = Guid.Empty;
            _statementName = null;
            _sql = null;
            _parameterTypeOids = null;
        }
    }
}
