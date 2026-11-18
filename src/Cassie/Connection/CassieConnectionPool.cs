using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Connection;

[SuppressMessage("Reliability", "CA1001",
    Justification = "Pool semaphores remain live while any lease can be returned or waiting to rent.")]
internal sealed class CassieConnectionPool(int maximumPoolSize, TimeSpan idleLifetime, TimeSpan maximumLifetime)
{
    private readonly SemaphoreSlim _changed = new(0);
    private readonly Lock _gate = new();
    private readonly Queue<PgSession> _idle = new();
    private bool _cleared;
    private int _physicalCount;
    private int _waiters;

    internal Task<(PgSession? Session, bool ReservedSlot)> RentAsync(bool synchronous,
        CancellationToken cancellationToken)
    {
        return synchronous ? Task.FromResult(Rent(cancellationToken)) : RentCoreAsync(cancellationToken);
    }

    private async Task<(PgSession? Session, bool ReservedSlot)> RentCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, session) = TryRent();
            switch (outcome)
            {
                case RentOutcome.Rented:
                    return (session, session is null);
                case RentOutcome.Stale:
                    await session!.DisposeAsync().ConfigureAwait(false);
                    break;
                default:
                    try
                    {
                        await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        StopWaiting();
                    }

                    break;
            }
        }
    }

    /// <summary>The blocking form, used by synchronous opens: it waits on the calling thread.</summary>
    private (PgSession? Session, bool ReservedSlot) Rent(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, session) = TryRent();
            switch (outcome)
            {
                case RentOutcome.Rented:
                    return (session, session is null);
                case RentOutcome.Stale:
                    session!.Dispose();
                    break;
                default:
                    try
                    {
                        _changed.Wait(cancellationToken);
                    }
                    finally
                    {
                        StopWaiting();
                    }

                    break;
            }
        }
    }

    /// <summary>Takes an idle session, reserves a slot for a new one (a null session), or registers a waiter.</summary>
    private (RentOutcome Outcome, PgSession? Session) TryRent()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_cleared, this);
            if (_idle.TryDequeue(out var available))
            {
                if (!IsUnusable(available))
                {
                    return (RentOutcome.Rented, available);
                }

                _physicalCount--;
                return (RentOutcome.Stale, available);
            }

            if (_physicalCount < maximumPoolSize)
            {
                _physicalCount++;
                return (RentOutcome.Rented, null);
            }

            _waiters++;
            return (RentOutcome.MustWait, null);
        }
    }

    private void StopWaiting()
    {
        lock (_gate)
        {
            _waiters--;
        }
    }

    private bool IsUnusable(PgSession session)
    {
        var now = Stopwatch.GetTimestamp();
        return (idleLifetime > TimeSpan.Zero && Stopwatch.GetElapsedTime(session.ReturnedTimestamp, now) > idleLifetime)
               || (maximumLifetime > TimeSpan.Zero &&
                   Stopwatch.GetElapsedTime(session.CreatedTimestamp, now) > maximumLifetime)
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

        foreach (var session in sessions)
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

    private enum RentOutcome
    {
        /// <summary>An idle session was taken, or a slot was reserved for a new one.</summary>
        Rented,

        /// <summary>An unusable idle session was removed and must be closed before trying again.</summary>
        Stale,

        /// <summary>The pool is full; the caller is registered as a waiter.</summary>
        MustWait
    }
}
