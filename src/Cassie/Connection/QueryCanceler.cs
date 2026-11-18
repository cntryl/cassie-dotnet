using System.Net.Sockets;
using System.Security.Authentication;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

/// <summary>Sends a PostgreSQL CancelRequest on a separate connection.</summary>
internal static class QueryCanceler
{
    /// <summary>Returns true when the cancel request reached the server, and false when it could not be sent.</summary>
    public static async Task<bool> SendAsync(CassieConnectionStringBuilder settings, int backendProcessId,
        int backendSecretKey, bool synchronous = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(settings.Timeout, 1, 5)));
        return await SendAsync(settings, backendProcessId, backendSecretKey, timeout.Token, synchronous)
            .ConfigureAwait(false);
    }

    /// <summary>Sends a cancel request within the caller's existing recovery deadline.</summary>
    public static Task<bool> SendAsync(CassieConnectionStringBuilder settings, int backendProcessId,
        int backendSecretKey, CancellationToken cancellationToken, bool synchronous = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var packet = SessionMessages.CancelRequest(backendProcessId, backendSecretKey);
        return synchronous
            ? Task.FromResult(Send(settings, packet, cancellationToken))
            : SendCoreAsync(settings, packet, cancellationToken);
    }

    private static async Task<bool> SendCoreAsync(CassieConnectionStringBuilder settings, byte[] packet,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient cancelClient = new() { NoDelay = true };
            await cancelClient.ConnectAsync(settings.Host, settings.Port, cancellationToken).ConfigureAwait(false);
            Stream cancelStream = cancelClient.GetStream();
            if (settings.SslMode != CassieSslMode.Disable)
            {
                cancelStream = await TlsNegotiator.NegotiateAsync(cancelStream, settings, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (cancelStream.ConfigureAwait(false))
            {
                await cancelStream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                await cancelStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception exception) when (IsSendFailure(exception))
        {
            return false;
        }
    }

    /// <summary>The blocking form, used when <c>DbCommand.Cancel</c> is called.</summary>
    private static bool Send(CassieConnectionStringBuilder settings, byte[] packet, CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient cancelClient = new() { NoDelay = true };
            SynchronousCall.RunBlocking(cancelClient, (cancelClient, settings),
                static target => target.cancelClient.Connect(target.settings.Host, target.settings.Port),
                cancellationToken);
            Stream cancelStream = cancelClient.GetStream();
            if (settings.SslMode != CassieSslMode.Disable)
            {
                cancelStream = TlsNegotiator.NegotiateBlocking(cancelStream, settings, cancellationToken);
            }

            using (cancelStream)
            {
                SynchronousCall.RunBlocking(cancelStream, (cancelStream, packet), static request =>
                {
                    request.cancelStream.Write(request.packet);
                    request.cancelStream.Flush();
                }, cancellationToken);
            }

            return true;
        }
        catch (Exception exception) when (IsSendFailure(exception))
        {
            return false;
        }
    }

    private static bool IsSendFailure(Exception exception)
    {
        return exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException
            or InvalidOperationException or AuthenticationException;
    }
}
