using System.Data;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    private static readonly TimeSpan CancelWatchdogDelay = TimeSpan.FromSeconds(5);
    private Task<bool>? _pendingQueryCancel;
    private long _cleanupOperation;
    internal long OperationSerial => _gate.Serial;

    internal void HandleParameterStatusMessage(byte[] payload)
    {
        try
        {
            var (name, value) = ParameterStatusMessage.Parse(payload);
            _session?.DateStyle.ApplyParameterStatus(name, value);
        }
        catch (IOException)
        {
            AbortTransport();
            throw;
        }
    }

    internal void HandleReadyForQuery(ReadOnlySpan<byte> payload)
    {
        if (_session is null || !_session.DateStyle.TryObserveReadyForQuery(payload))
        {
            AbortTransport();
            throw new IOException("Cassie returned an invalid ReadyForQuery transaction status.");
        }
    }

    internal void ObserveCommandComplete(CommandTag tag)
    {
        if (_session is { } session)
        {
            ObserveCommandComplete(session, tag);
        }
    }

    private static void ObserveCommandComplete(PgSession session, CommandTag tag)
    {
        session.DateStyle.ObserveCommandComplete(tag);

        if (tag.HasVerb("ROLLBACK") || tag.HasVerb("ABORT") ||
            tag.Matches("CREATE EXTENSION") || tag.Matches("DROP EXTENSION") || tag.Matches("ALTER EXTENSION"))
        {
            session.VectorOid = null;
        }
    }

    private async Task RefreshDateStyleIfNeededAsync(CommandBudget budget, CancellationToken cancellationToken)
    {
        var tracker = _session?.DateStyle;

        if (tracker is null || !tracker.CanRefresh || _state != ConnectionState.Open || _gate.IsActive)
        {
            return;
        }


        tracker.BeginRefresh();

        try
        {
            var reader = await ExecuteQueryCoreAsync("SHOW DateStyle", budget, CommandBehavior.Default, null, false, cancellationToken).ConfigureAwait(false);

            try
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("Cassie did not return DateStyle from SHOW DateStyle.");
                }

                tracker.ApplyParameterStatus("DateStyle", reader.GetString(0));
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            tracker.EndRefresh();
        }
    }

    internal void AbortTransport(long? expectedOperation = null)
    {
        lock (_abortGate)
        {
            if (expectedOperation is long serial && (!_gate.IsActive || _gate.Serial != serial))
            {
                return;
            }

            ActiveTransaction?.MarkCompleted();
            _gate.End();
            _pendingQueryCancel = null;

            try
            {
                _session?.Dispose();
            }
            catch (Exception exception) when (CleanupDeadline.IsTransportFailure(exception))
            {
                // The socket is closed even if disposing the stream failed.
            }
            finally
            {
                _session = null;

                if (_pool is not null && _ownsPoolSlot)
                {
                    _pool.ReleaseReservedSlot();
                }


                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                SetState(ConnectionState.Closed);
            }
        }
    }

    /// <summary>Requests server-side cancellation and aborts the session if the server does not finish the statement.</summary>
    internal void RequestCancel()
    {
        long serial;
        Task<bool> cancel;
        lock (_abortGate)
        {
            if (!_gate.IsActive || _gate.Serial == _cleanupOperation)
            {
                return;
            }

            serial = _gate.Serial;
            cancel = CancelCurrentQueryAsync(serial);
        }

        var sent = SynchronousCall.Wait(cancel);

        if (!sent)
        {
            if (_gate.Serial == serial && _gate.IsActive)
            {
                AbortTransport(expectedOperation: serial);
            }

            return;
        }


        _ = AbortIfStillRunningAsync(serial);
    }

    private async Task AbortIfStillRunningAsync(long serial)
    {
        await Task.Delay(CancelWatchdogDelay, Clock).ConfigureAwait(false);

        if (_gate.IsActive && _gate.Serial == serial)
        {
            AbortTransport(expectedOperation: serial);
        }
    }

    internal Task<bool> CancelCurrentQueryAsync(long expectedOperation)
    {
        lock (_abortGate)
        {
            return _gate.IsActive && _gate.Serial == expectedOperation && _gate.Serial != _cleanupOperation &&
                   _session is { CanCancel: true } session
                ? _pendingQueryCancel ??= QueryCanceler.SendAsync([with(_connectionString)],
                    session.BackendProcessId, session.BackendSecretKey, Clock)
                : Task.FromResult(false);
        }
    }
}
