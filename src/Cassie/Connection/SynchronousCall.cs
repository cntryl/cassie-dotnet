using System.Net.Sockets;
using System.Security.Authentication;

namespace Cntryl.Cassie.Connection;

/// <summary>
///     Support for the synchronous ADO.NET surface. A synchronous call switches the session transport to
///     blocking I/O, so every task underneath it is already complete when the caller asks for its result
///     and no thread-pool thread is needed to finish the call.
/// </summary>
internal static class SynchronousCall
{
    /// <summary>
    ///     How many times this thread had to block on an unfinished task. Zero means every synchronous
    ///     call on this thread ran entirely on the calling thread.
    /// </summary>
    [ThreadStatic] internal static int PendingWaitsOnThisThread;

    internal static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            PendingWaitsOnThisThread++;
        }

        task.GetAwaiter().GetResult();
    }

    internal static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            PendingWaitsOnThisThread++;
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Runs blocking I/O. Blocking calls cannot observe a token, so cancellation disposes
    ///     <paramref name="abortTarget" />, which fails the call and is reported as cancellation.
    /// </summary>
    internal static void RunBlocking<TState>(
        IDisposable abortTarget,
        TState state,
        Action<TState> io,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var abort =
            cancellationToken.UnsafeRegister(static target => ((IDisposable)target!).Dispose(), abortTarget);

        try
        {
            io(state);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested &&
                                          exception is IOException or SocketException or ObjectDisposedException
                                              or AuthenticationException)
        {
            throw new OperationCanceledException("The operation was canceled.", exception, cancellationToken);
        }
    }
}
