using Cntryl.Cassie.Protocol;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Opens a new physical session: TCP connect, optional TLS, startup, and authentication.</summary>
    internal static class PgSessionConnector
    {
        private const int AuthenticationOk = 0;
        private const int AuthenticationCleartextPassword = 3;

        public static async Task<PgSession> ConnectAsync(CassieConnectionStringBuilder settings, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            PgSession session = new(new DateStyleTracker(), Stopwatch.GetTimestamp());
            try
            {
                await session.ConnectAsync(settings.Host, settings.Port, cancellationToken).ConfigureAwait(false);
                if (settings.SslMode != CassieSslMode.Disable)
                {
                    session.UseStream(await TlsNegotiator.NegotiateAsync(session.Stream, settings, cancellationToken).ConfigureAwait(false));
                }

                await session.Stream.WriteAsync(SessionMessages.Startup(settings.Username, settings.Database, settings.ApplicationName, settings.Options), cancellationToken).ConfigureAwait(false);
                (string serverVersion, int processId, int secretKey) = await AuthenticateAsync(session.Stream, settings, session.DateStyle, cancellationToken).ConfigureAwait(false);
                session.Identify(serverVersion, processId, secretKey);
                session.DateStyle.CompleteStartup();
                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static async Task<(string ServerVersion, int ProcessId, int SecretKey)> AuthenticateAsync(Stream stream, CassieConnectionStringBuilder settings, DateStyleTracker dateStyle, CancellationToken cancellationToken)
        {
            bool authenticated = false;
            string serverVersion = string.Empty;
            int processId = 0;
            int secretKey = 0;
            while (true)
            {
                (byte tag, byte[] payload) = await PgFraming.ReadBackendFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                switch (tag)
                {
                    case BackendMessage.Authentication:
                        authenticated |= await HandleAuthenticationAsync(stream, settings, payload, cancellationToken).ConfigureAwait(false);
                        break;
                    case BackendMessage.ParameterStatus:
                        (string name, string value) = ParameterStatusMessage.Parse(payload);
                        if (name == "server_version")
                        {
                            serverVersion = value;
                        }

                        dateStyle.ApplyParameterStatus(name, value);
                        break;
                    case BackendMessage.BackendKeyData:
                        if (payload.Length != 8)
                        {
                            throw new IOException("Invalid backend key data frame.");
                        }

                        processId = BinaryPrimitives.ReadInt32BigEndian(payload);
                        secretKey = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4));
                        break;
                    case BackendMessage.ErrorResponse:
                        throw ServerErrorParser.Parse(payload);
                    case BackendMessage.ReadyForQuery:
                        if (!authenticated)
                        {
                            throw new IOException("The server became ready before authentication completed.");
                        }

                        return dateStyle.TryObserveReadyForQuery(payload)
                            ? (serverVersion, processId, secretKey)
                            : throw new IOException("Cassie returned an invalid ReadyForQuery transaction status.");
                    default:
                        break;
                }
            }
        }

        /// <summary>Answers an authentication request; returns true once the server reports success.</summary>
        private static async Task<bool> HandleAuthenticationAsync(Stream stream, CassieConnectionStringBuilder settings, byte[] payload, CancellationToken cancellationToken)
        {
            if (payload.Length != 4)
            {
                throw new IOException("Invalid authentication frame from server.");
            }

            int method = BinaryPrimitives.ReadInt32BigEndian(payload);
            if (method == AuthenticationOk)
            {
                return true;
            }

            if (method != AuthenticationCleartextPassword)
            {
                throw new NotSupportedException($"Cassie requested unsupported authentication method {method}.");
            }

            byte[] passwordPayload = new byte[Encoding.UTF8.GetByteCount(settings.Password) + 1];
            _ = Encoding.UTF8.GetBytes(settings.Password, passwordPayload);
            try
            {
                await PgFraming.WriteFrontendFrameAsync(stream, FrontendMessage.PasswordMessage, passwordPayload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordPayload);
            }

            return false;
        }
    }
}
