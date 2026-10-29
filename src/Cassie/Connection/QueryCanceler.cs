using Cntryl.Cassie.Protocol;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Sends a PostgreSQL CancelRequest on a separate connection.</summary>
    internal static class QueryCanceler
    {
        /// <summary>Returns true when the cancel request reached the server, and false when it could not be sent.</summary>
        public static async Task<bool> SendAsync(CassieConnectionStringBuilder settings, int backendProcessId, int backendSecretKey)
        {
            ArgumentNullException.ThrowIfNull(settings);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(settings.Timeout, 1, 5)));
            return await SendAsync(settings, backendProcessId, backendSecretKey, timeout.Token).ConfigureAwait(false);
        }

        /// <summary>Sends a cancel request within the caller's existing recovery deadline.</summary>
        public static async Task<bool> SendAsync(CassieConnectionStringBuilder settings, int backendProcessId, int backendSecretKey, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            byte[] packet = SessionMessages.CancelRequest(backendProcessId, backendSecretKey);
            try
            {
                using TcpClient cancelClient = new() { NoDelay = true };
                await cancelClient.ConnectAsync(settings.Host, settings.Port, cancellationToken).ConfigureAwait(false);
                Stream cancelStream = cancelClient.GetStream();
                if (settings.SslMode != CassieSslMode.Disable)
                {
                    cancelStream = await TlsNegotiator.NegotiateAsync(cancelStream, settings, cancellationToken).ConfigureAwait(false);
                }

                await using (cancelStream.ConfigureAwait(false))
                {
                    await cancelStream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                    await cancelStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or AuthenticationException)
            {
                return false;
            }
        }
    }
}
