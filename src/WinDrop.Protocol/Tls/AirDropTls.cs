using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace WinDrop.Protocol.Tls;

/// <summary>
/// TLS setup for both ends of an AirDrop connection.
///
/// Certificate validation is disabled deliberately, not accidentally. See
/// <see cref="AirDropCertificate"/> for why it cannot be otherwise: no shared trust
/// anchor exists, and a link-local IPv6 address carries no name to validate against.
/// The callbacks below are written explicitly rather than by passing null so that the
/// choice is visible at the call site and cannot be mistaken for an oversight.
///
/// What this means in practice: an active attacker on the same link can impersonate a
/// peer. That is equally true of Apple's own implementation in Everyone mode, and it is
/// why the receiving user is shown a consent prompt naming the sender before a single
/// byte of file data is written to disk.
/// </summary>
public static class AirDropTls
{
    /// <summary>Sender side. Presents a client certificate, validates nothing.</summary>
    public static async Task<SslStream> AuthenticateAsClientAsync(
        Stream inner,
        X509Certificate2 certificate,
        CancellationToken ct = default)
    {
        // The validation callback belongs in the authentication options below,
        // not here: SslStream throws if it is supplied in both places.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);

        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    // Not a real name — nothing checks it. Present so the handshake has
                    // a target host field to fill.
                    TargetHost = "airdrop",
                    ClientCertificates = [certificate],
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                ct);

            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Receiver side. Requests a client certificate but does not require one: in
    /// Everyone mode a sender may present nothing, and refusing it would break the
    /// flow we actually support.
    /// </summary>
    public static async Task<SslStream> AuthenticateAsServerAsync(
        Stream inner,
        X509Certificate2 certificate,
        CancellationToken ct = default)
    {
        // The validation callback belongs in the authentication options below,
        // not here: SslStream throws if it is supplied in both places.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);

        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                ct);

            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }
}
