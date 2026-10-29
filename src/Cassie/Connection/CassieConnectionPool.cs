using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Connection
{
    [SuppressMessage("Reliability", "CA1001", Justification = "Pool semaphores remain live while any lease can be returned or waiting to rent.")]
    internal sealed class CassieConnectionPool(int maximumPoolSize, TimeSpan idleLifetime, TimeSpan maximumLifetime)
    {
        private readonly SemaphoreSlim _changed = new(0);
        private readonly Queue<PgSession> _idle = new();
        private readonly Lock _gate = new();
        private int _physicalCount;
        private int _waiters;
        private bool _cleared;

        internal async Task<(PgSession? Session, bool ReservedSlot)> RentAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                PgSession? stale = null;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_cleared, this);
                    if (_idle.TryDequeue(out PgSession? available))
                    {
                        if (!IsUnusable(available))
                        {
                            return (available, false);
                        }

                        _physicalCount--;
                        stale = available;
                    }
                    else if (_physicalCount < maximumPoolSize)
                    {
                        _physicalCount++;
                        return (null, true);
                    }
                    else
                    {
                        _waiters++;
                    }
                }

                if (stale is not null)
                {
                    await stale.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    lock (_gate)
                    {
                        _waiters--;
                    }

                    throw;
                }

                lock (_gate)
                {
                    _waiters--;
                }
            }
        }

        private bool IsUnusable(PgSession session)
        {
            long now = Stopwatch.GetTimestamp();
            return (idleLifetime > TimeSpan.Zero && Stopwatch.GetElapsedTime(session.ReturnedTimestamp, now) > idleLifetime)
                || (maximumLifetime > TimeSpan.Zero && Stopwatch.GetElapsedTime(session.CreatedTimestamp, now) > maximumLifetime)
                || !session.IsTransportAlive();
        }

        internal async ValueTask ReturnAsync(PgSession session)
        {
            session.ReturnedTimestamp = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (!_cleared)
                {
                    _idle.Enqueue(session);
                    if (_waiters > 0)
                    {
                        _ = _changed.Release();
                    }

                    return;
                }

                _physicalCount--;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                SignalWaiter();
            }
        }

        internal void ReleaseReservedSlot()
        {
            lock (_gate)
            {
                _physicalCount--;
            }

            SignalWaiter();
        }

        internal async Task ClearAsync()
        {
            List<PgSession> sessions;
            lock (_gate)
            {
                _cleared = true;
                sessions = [.. _idle];
                _idle.Clear();
                _physicalCount -= sessions.Count;
                if (_waiters > 0)
                {
                    _ = _changed.Release(_waiters);
                }
            }

            foreach (PgSession session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void SignalWaiter()
        {
            lock (_gate)
            {
                if (_waiters > 0)
                {
                    _ = _changed.Release();
                }
            }
        }
    }
}
