using Cntryl.Cassie.Data;
using Cntryl.Cassie.Protocol;
using System.Data;

namespace Cntryl.Cassie.Connection
{
    public sealed partial class CassieConnection
    {
        private static readonly TimeSpan CancelWatchdogDelay = TimeSpan.FromSeconds(5);

        internal void HandleParameterStatusMessage(byte[] payload)
        {
            try
            {
                (string name, string value) = ParameterStatusMessage.Parse(payload);
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
            _session?.DateStyle.ObserveCommandComplete(tag);
        }

        private async Task RefreshDateStyleIfNeededAsync(CancellationToken cancellationToken)
        {
            DateStyleTracker? tracker = _session?.DateStyle;
            if (tracker is null || !tracker.CanRefresh || _state != ConnectionState.Open || _gate.IsActive)
            {
                return;
            }

            tracker.BeginRefresh();
            try
            {
                CassieDataReader reader = await ExecuteQueryAsync("SHOW DateStyle", cancellationToken).ConfigureAwait(false);
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

        internal async Task ReaderClosedAsync()
        {
            _gate.End();
            if (_preparedStatementsToClose.Count == 0)
            {
                return;
            }

            _gate.Begin();
            try
            {
                while (_preparedStatementsToClose.Count > 0 && _state == ConnectionState.Open)
                {
                    string statement = _preparedStatementsToClose[0];
                    _preparedStatementsToClose.RemoveAt(0);
                    await CloseServerStatementCoreAsync(statement, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (_state == ConnectionState.Open)
                {
                    _gate.End();
                }
            }
        }

        internal void AbortTransport()
        {
            lock (_abortGate)
            {
                ActiveTransaction?.ConnectionClosed();
                _gate.End();
                _session?.Dispose();
                if (_pool is not null && _ownsPoolSlot)
                {
                    _pool.ReleaseReservedSlot();
                }

                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                _preparedStatementsToClose.Clear();
            }

            SetState(ConnectionState.Closed);
        }

        /// <summary>Requests server-side cancellation and aborts the session if the server does not finish the statement.</summary>
        internal void RequestCancel()
        {
            if (!_gate.IsActive)
            {
                return;
            }

            long serial = _gate.Serial;
            bool sent = CancelCurrentQueryAsync().GetAwaiter().GetResult();
            if (!sent)
            {
                if (_gate.Serial == serial && _gate.IsActive)
                {
                    AbortTransport();
                }

                return;
            }

            _ = AbortIfStillRunningAsync(serial);
        }

        private async Task AbortIfStillRunningAsync(long serial)
        {
            await Task.Delay(CancelWatchdogDelay).ConfigureAwait(false);
            if (_gate.IsActive && _gate.Serial == serial)
            {
                AbortTransport();
            }
        }

        internal async Task<bool> CancelCurrentQueryAsync()
        {
            PgSession? session = _session;
            return _gate.IsActive && session is not null && session.CanCancel
                && await QueryCanceler.SendAsync(new CassieConnectionStringBuilder(_connectionString), session.BackendProcessId, session.BackendSecretKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes one request, then reads until ReadyForQuery. Returns the server error if one arrived; any other
        /// failure before ReadyForQuery leaves the stream out of sync, so the transport is aborted.
        /// </summary>
        private async Task<CassieException?> RunSyncedRequestAsync(PgSession session, Func<Task> writeRequest, CancellationToken readToken)
        {
            bool ready = false;
            CassieException? serverError = null;
            try
            {
                await writeRequest().ConfigureAwait(false);
                while (!ready)
                {
                    (byte tag, byte[] payload) = await session.ReadFrameAsync(readToken).ConfigureAwait(false);
                    if (tag == BackendMessage.ErrorResponse)
                    {
                        serverError = ServerErrorParser.Parse(payload);
                    }
                    else if (tag == BackendMessage.ReadyForQuery)
                    {
                        HandleReadyForQuery(payload);
                        ready = true;
                    }
                }

                return serverError;
            }
            catch
            {
                if (!ready)
                {
                    AbortTransport();
                }

                throw;
            }
        }

        internal async Task ExecutePortalPageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _session!.WriteRawAsync(_portalPageFrames, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                AbortTransport();
                throw;
            }
        }

        internal async Task SyncExtendedQueryAsync()
        {
            try
            {
                await _session!.WriteFrameAsync(FrontendMessage.Sync, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                AbortTransport();
                throw;
            }
        }
    }
}
