using System.Net.Sockets;
using System.Security.Authentication;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

/// <summary>Sends a PostgreSQL CancelRequest on a separate connection.</summary>
internal static class QueryCanceler
{
    /// <summary>Returns true after the server closes the cancel connection.</summary>
    public static async Task<bool> SendAsync(CassieConnectionStringBuilder settings, int backendProcessId,
        int backendSecretKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(settings.Timeout, 1, 5)));
        return await SendAsync(settings, backendProcessId, backendSecretKey, timeout.Token)
            .ConfigureAwait(false);
    }

    /// <summary>Sends a cancel request within the caller's existing recovery deadline.</summary>
    public static Task<bool> SendAsync(CassieConnectionStringBuilder settings, int backendProcessId,
        int backendSecretKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var packet = SessionMessages.CancelRequest(backendProcessId, backendSecretKey);
        return SendCoreAsync(settings, packet, cancellationToken);
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
                // The server processes the cancellation before closing this separate connection.
                return await cancelStream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) == 0;
            }
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
