using System.Data;
using System.Net.Sockets;
using Cntryl.Cassie.Connection;

namespace Cntryl.Cassie.Commands;

/// <summary>Owns a command's server statement identity and delegates wire cleanup to its session owner.</summary>
internal sealed class PreparedStatementLease
{
    private CassieConnection? _connection;
    private int[]? _parameterTypeOids;
    private Guid _sessionIdentity;
    private string? _sql;
    private string? _statementName;

    public bool IsRequested { get; private set; }

    public void Request()
    {
        IsRequested = true;
    }

    public async Task<string> EnsurePreparedAsync(
        CassieConnection connection,
        CassiePreparedCommand prepared,
        CommandBudget budget,
        CancellationToken cancellationToken
    )
    {
        if (Matches(connection, prepared))
        {
            return _statementName!;
        }

        if (CanRelease())
        {
            using var deadline = new CommandDeadline(budget, cancellationToken);
            deadline.ThrowIfExpired();

            try
            {
                await ReleaseAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                deadline.ThrowIfExpired();
                throw;
            }
        }

        var statementName = await connection.PrepareServerStatementAsync(prepared, budget, cancellationToken)
            .ConfigureAwait(false);
        Remember(connection, prepared, statementName);
        return statementName;
    }

    public void Release()
    {
        try
        {
            if (CanRelease())
            {
                _connection!.CloseServerStatement(_statementName!, _sessionIdentity);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                              or CassieException or InvalidOperationException or TimeoutException)
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
                await _connection!.CloseServerStatementAsync(_statementName!, _sessionIdentity, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                              or CassieException or InvalidOperationException or TimeoutException)
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
