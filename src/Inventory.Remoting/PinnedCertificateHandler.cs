using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inventory.Remoting;

/// <summary>
/// HTTPS zu einer Verwaltung mit eigenem Zertifikat: Windows-vertraute Zertifikate gelten, ein anderes nur, wenn sein SHA-256-
/// Fingerabdruck eingetragen ist. Den Fingerabdruck eines abgelehnten Zertifikats merkt sich der Handler für die Fehlermeldung.
/// </summary>
internal sealed class PinnedCertificateHandler
{
    public SocketsHttpHandler Handler { get; }

    /// <summary>Fingerabdruck des zuletzt abgelehnten Zertifikats; null, wenn keins abgelehnt wurde.</summary>
    public string? Rejected { get; private set; }

    public PinnedCertificateHandler(string? pinnedThumbprint, bool cookies = false)
    {
        var pinned = Core.CertificateThumbprints.Normalize(pinnedThumbprint);
        Handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = cookies,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;
                    var thumbprint = certificate is null ? null : Thumbprint(certificate);
                    if (thumbprint is not null && string.Equals(thumbprint, pinned, StringComparison.Ordinal))
                        return true;
                    Rejected = thumbprint;
                    return false;
                },
            },
        };
    }

    public static string Thumbprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
}
