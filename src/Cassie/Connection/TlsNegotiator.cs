using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

/// <summary>Performs the PostgreSQL SSLRequest exchange and the TLS handshake that follows it.</summary>
internal static class TlsNegotiator
{
    private const byte ServerAcceptsTls = (byte)'S';
    private const byte ServerDeclinesTls = (byte)'N';

    public static async Task<Stream> NegotiateAsync(
        Stream stream,
        CassieConnectionStringBuilder settings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(settings);
        await stream.WriteAsync(SessionMessages.SslRequest(), cancellationToken).ConfigureAwait(false);
        var response = new byte[1];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);

        if (!ServerAcceptedTls(response[0], settings))
        {
            return stream;
        }


        var trustedRootsPem = settings.SslRootCertificate.Length > 0
            ? await File.ReadAllTextAsync(settings.SslRootCertificate, cancellationToken).ConfigureAwait(false)
            : null;
        SslStream tls = new(stream, false);
        await tls.AuthenticateAsClientAsync(CreateAuthenticationOptions(settings, trustedRootsPem), cancellationToken)
            .ConfigureAwait(false);
        return tls;
    }

    /// <summary>The blocking form of the same exchange, used by synchronous ADO.NET calls.</summary>
    public static Stream NegotiateBlocking(
        Stream stream,
        CassieConnectionStringBuilder settings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(settings);
        var response = new byte[1];
        SynchronousCall.RunBlocking(stream, (stream, response), static exchange =>
        {
            exchange.stream.Write(SessionMessages.SslRequest());
            exchange.stream.ReadExactly(exchange.response);
        }, cancellationToken);

        if (!ServerAcceptedTls(response[0], settings))
        {
            return stream;
        }


        cancellationToken.ThrowIfCancellationRequested();
        var trustedRootsPem = settings.SslRootCertificate.Length > 0
            ? File.ReadAllText(settings.SslRootCertificate)
            : null;
        SslStream tls = new(stream, false);
        SynchronousCall.RunBlocking(tls, (tls, options: CreateAuthenticationOptions(settings, trustedRootsPem)),
            static handshake => handshake.tls.AuthenticateAsClient(handshake.options), cancellationToken);
        return tls;
    }

    /// <summary>Interprets the single-byte SSLRequest reply; false means continue without TLS.</summary>
    private static bool ServerAcceptedTls(byte response, CassieConnectionStringBuilder settings)
    {
        return response switch
        {
            ServerAcceptsTls => true,
            ServerDeclinesTls => settings.SslMode == CassieSslMode.Require
                ? throw new InvalidOperationException("The server does not support TLS, but SSL Mode is Require.")
                : false,
            _ => throw new IOException("The server returned an invalid TLS negotiation response.")
        };
    }

    private static SslClientAuthenticationOptions CreateAuthenticationOptions(
        CassieConnectionStringBuilder settings,
        string? trustedRootsPem
    )
    {
        SslClientAuthenticationOptions authenticationOptions = new() { TargetHost = settings.Host };

        if (trustedRootsPem is null)
        {
            return authenticationOptions;
        }


        X509Certificate2Collection trustedRoots = [];
        trustedRoots.ImportFromPem(trustedRootsPem);

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
        return authenticationOptions;
    }
}
