using System.Data;
using System.Globalization;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    private const string UndefinedPreparedStatementSqlState = "26000";
    private const int StatementCloseGroupSize = 64;
    private int _nextPreparedStatementId;

    internal async Task<string> PrepareServerStatementAsync(
        CassiePreparedCommand prepared,
        CommandBudget budget,
        CancellationToken cancellationToken
    )
    {
        var session = RequireSession("The connection must be open to prepare a command.");
        ThrowIfReaderActive();
        var statementName = "cassie_dotnet_" + session.BackendProcessId.ToString(CultureInfo.InvariantCulture) + "_" +
                            Interlocked.Increment(ref _nextPreparedStatementId).ToString(CultureInfo.InvariantCulture) +
                            "_" + Guid.NewGuid().ToString("N");
        using MemoryStream request = new();
        using var parse = ExtendedQueryMessages.Parse(statementName, prepared.Sql, prepared.ParameterTypeOids);
        using var describe = ExtendedQueryMessages.DescribeStatement(statementName);
        PgWriter.AppendFrame(request, FrontendMessage.Parse, parse);
        PgWriter.AppendFrame(request, FrontendMessage.Describe, describe);
        PgWriter.AppendFrame(request, FrontendMessage.Sync, []);

        var reader = await RunReaderAsync(
            token => session.WriteBatchAsync(request, token),
            () => new CassieDataReader(new ConnectionReaderSession(this, session), budget, CommandBehavior.Default),
            budget, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        return statementName;
    }

    internal void CloseServerStatement(string statementName, Guid lease)
    {
        RunSynchronously(() => CloseServerStatementAsync(statementName, lease, CancellationToken.None));
    }

    internal Task CloseServerStatementAsync(string statementName, Guid lease, CancellationToken cancellationToken)
    {
        PgSession? session;
        long serial;
        lock (_abortGate)
        {
            session = _session;

            if (_state != ConnectionState.Open || session is null || SessionIdentity != lease)
            {
                return Task.CompletedTask;
            }


            _ = (session.StatementsToClose ??= []).Add(statementName);

            if (_gate.IsActive)
            {
                return Task.CompletedTask;
            }


            _gate.Begin();
            serial = _cleanupOperation = _gate.Serial;
        }

        return FinishQueryAsync(session, serial, null, null, cancellationToken);
    }

    internal Task ReaderClosedAsync(CleanupDeadline? drainDeadline = null)
    {
        PgSession session;
        long serial;
        Task<bool>? pendingCancel;
        lock (_abortGate)
        {
            pendingCancel = _pendingQueryCancel;
            _pendingQueryCancel = null;

            if (_session is not { } current ||
                (drainDeadline is null && pendingCancel is null && current.StatementsToClose is not { Count: > 0 }))
            {
                _gate.End();
                return Task.CompletedTask;
            }


            session = current;
            // ReadyForQuery retires the query, but its cancel must settle before further wire I/O.
            _gate.Begin();
            serial = _cleanupOperation = _gate.Serial;
        }

        return FinishQueryAsync(session, serial, pendingCancel, drainDeadline, CancellationToken.None);
    }

    private async Task FinishQueryAsync(
        PgSession session,
        long serial,
        Task<bool>? pendingCancel,
        CleanupDeadline? drainDeadline,
        CancellationToken cancellationToken
    )
    {
        using var ownedDeadline = drainDeadline is null ? new CleanupDeadline(session, Clock) : null;
        var deadline = drainDeadline ?? ownedDeadline!;
        CassieException? firstError = null;

        try
        {
            if (pendingCancel is not null && !await pendingCancel.WaitAsync(deadline.Token).ConfigureAwait(false))
            {
                AbortTransport(expectedOperation: serial);
                return;
            }

            while (true)
            {
                string[] group;
                lock (_abortGate)
                {
                    if (!ReferenceEquals(session, _session) || _gate.Serial != serial)
                    {
                        throw new IOException("The connection closed before prepared statement cleanup completed.");
                    }

                    if (session.StatementsToClose is not { Count: > 0 } pending)
                    {
                        deadline.Complete();
                        _gate.End();
                        break;
                    }

                    group = [.. pending.Take(StatementCloseGroupSize)];
                    pending.ExceptWith(group);
                }

                using MemoryStream request = new();

                foreach (var statement in group)
                {
                    using var close = ExtendedQueryMessages.CloseStatement(statement);
                    PgWriter.AppendFrame(request, FrontendMessage.Close, close);
                    PgWriter.AppendFrame(request, FrontendMessage.Sync, []);
                }


                await session.WriteBatchAsync(request, cancellationToken).ConfigureAwait(false);

                for (var index = 0; index < group.Length; index++)
                {
                    var error = await ReadStatementCloseAsync(session, cancellationToken).ConfigureAwait(false);

                    if (error is not null && error.SqlState != UndefinedPreparedStatementSqlState)
                    {
                        firstError ??= error;
                    }
                }
            }
        }
        catch (Exception exception) when (deadline.Expired && CleanupDeadline.IsTransportFailure(exception))
        {
            AbortTransport(expectedOperation: serial);
            throw new TimeoutException("Cassie query cleanup exceeded its deadline.", exception);
        }
        catch
        {
            AbortTransport(expectedOperation: serial);
            throw;
        }


        if (firstError is not null)
        {
            throw firstError;
        }
    }

    private async Task<CassieException?> ReadStatementCloseAsync(PgSession session, CancellationToken cancellationToken)
    {
        var acknowledged = false;
        CassieException? error = null;

        while (true)
        {
            var (tag, payload) = await session.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

            switch (tag)
            {
                case BackendMessage.CloseComplete when !acknowledged && payload.Length == 0:
                    acknowledged = true;
                    break;
                case BackendMessage.ErrorResponse when !acknowledged:
                    error = ServerErrorParser.Parse(payload);
                    acknowledged = true;
                    break;
                case BackendMessage.ReadyForQuery when acknowledged:
                    HandleReadyForQuery(payload);
                    return error;
                case BackendMessage.ParameterStatus:
                    HandleParameterStatusMessage(payload);
                    break;
                case BackendMessage.NoticeResponse:
                case BackendMessage.NotificationResponse:
                    break;
                default:
                    throw new IOException("Cassie returned an unexpected response while closing prepared statements.");
            }
        }
    }
}
