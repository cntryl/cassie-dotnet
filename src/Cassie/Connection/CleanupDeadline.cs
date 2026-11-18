using System.Net.Sockets;

namespace Cntryl.Cassie.Connection;

/// <summary>Interrupts cleanup I/O on its borrowed socket without releasing the lease from the timer callback.</summary>
internal sealed class CleanupDeadline : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenRegistration _abort;

    internal CleanupDeadline(PgSession session, TimeProvider timeProvider)
    {
        _timeout = new CancellationTokenSource(Budget, timeProvider);
        _abort = _timeout.Token.UnsafeRegister(static client => ((TcpClient)client!).Dispose(), session.Client);
    }

    internal CancellationToken Token => _timeout.Token;
    internal bool Expired => _timeout.IsCancellationRequested;

    internal static bool IsTransportFailure(Exception error)
    {
        return error is IOException or SocketException or ObjectDisposedException or OperationCanceledException;
    }

    internal void Complete()
    {
        // A callback that has started makes the session ineligible for reuse.
        if (!_abort.Unregister() || Expired)
        {
            throw new TimeoutException("Cassie session cleanup exceeded its deadline.");
        }
    }

    public void Dispose()
    {
        _abort.Dispose();
        _timeout.Dispose();
    }
}
