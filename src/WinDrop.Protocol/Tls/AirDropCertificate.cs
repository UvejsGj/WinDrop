using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinDrop.Protocol.Tls;

/// <summary>
/// Generates the self-signed certificate each AirDrop endpoint presents.
///
/// WHY SELF-SIGNED IS CORRECT HERE. In "Everyone" mode neither side validates the
/// other's certificate: there is no shared CA, and the peer is reached at an IPv6
/// link-local address that has no name to check a certificate against. Apple's own
/// implementation presents a certificate from an Apple-issued chain, but only uses it
/// for the Contacts-Only identity check, which we cannot participate in anyway. So TLS
/// here buys confidentiality against a passive listener and nothing else. The real
/// security boundary is the consent prompt on the receiving device.
/// </summary>
public static class AirDropCertificate
{
    public static X509Certificate2 CreateSelfSigned(
        string commonName = "WinDrop",
        TimeSpan? lifetime = null)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        // Both roles are used on one connection in some flows, so the certificate has to
        // be valid for server and client authentication alike.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
                critical: false));

        // Backdate slightly: a peer whose clock is a little behind ours would otherwise
        // reject a certificate that is not yet valid.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = request.CreateSelfSigned(
            now.AddMinutes(-5),
            now.Add(lifetime ?? TimeSpan.FromDays(365)));

        // Round-tripping through PKCS#12 is not redundant. On Windows, SslStream cannot
        // use the ephemeral key that CreateSelfSigned attaches, and fails the handshake
        // with "the credentials supplied to the package were not recognized" — an error
        // that says nothing about the actual cause. Exporting and re-importing gives the
        // certificate a key handle the SChannel provider will accept.
        return new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);
    }
}
