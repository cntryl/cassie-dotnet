using Cntryl.Cassie.Protocol;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Performs the PostgreSQL SSLRequest exchange and the TLS handshake that follows it.</summary>
    internal static class TlsNegotiator
    {
        private const byte ServerAcceptsTls = (byte)'S';
        private const byte ServerDeclinesTls = (byte)'N';

        public static async Task<Stream> NegotiateAsync(Stream stream, CassieConnectionStringBuilder settings, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(settings);
            await stream.WriteAsync(SessionMessages.SslRequest(), cancellationToken).ConfigureAwait(false);
            byte[] response = new byte[1];
            await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response[0] == ServerDeclinesTls)
            {
                return settings.SslMode == CassieSslMode.Require
                    ? throw new InvalidOperationException("The server does not support TLS, but SSL Mode is Require.")
                    : stream;
            }

            if (response[0] != ServerAcceptsTls)
            {
                throw new IOException("The server returned an invalid TLS negotiation response.");
            }

            SslStream tls = new(stream, leaveInnerStreamOpen: false);
            SslClientAuthenticationOptions authenticationOptions = new() { TargetHost = settings.Host };
            if (settings.SslRootCertificate.Length > 0)
            {
                X509Certificate2Collection trustedRoots = [];
                trustedRoots.ImportFromPem(await File.ReadAllTextAsync(settings.SslRootCertificate, cancellationToken).ConfigureAwait(false));
                if (trustedRoots.Count == 0)
                {
                    throw new InvalidOperationException("SSL Root Certificate must contain at least one PEM certificate.");
                }

                X509ChainPolicy chainPolicy = new()
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck
                };
                chainPolicy.CustomTrustStore.AddRange(trustedRoots);
                authenticationOptions.CertificateChainPolicy = chainPolicy;
            }

            await tls.AuthenticateAsClientAsync(authenticationOptions, cancellationToken).ConfigureAwait(false);
            return tls;
        }
    }
}
