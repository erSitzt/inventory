using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inventory.Server.Https;

/// <summary>Woher das HTTPS-Zertifikat des Servers kommt; steht als Name in den Einstellungen.</summary>
public enum ServerCertificateSource
{
    /// <summary>Vom Server selbst erzeugt und erneuert.</summary>
    SelfSigned,
    /// <summary>Aus dem Zertifikatspeicher des Computers (LocalMachine\My), z. B. per Autoenrollment der AD-Zertifizierungsstelle oder von win-acme.</summary>
    MachineStore,
    /// <summary>PFX- oder PEM-Datei auf dem Server, die ein anderes Werkzeug ablegt und erneuert.</summary>
    File,
    /// <summary>Im Browser hochgeladene PFX-Datei.</summary>
    Upload,
    /// <summary>Von Inventory selbst bei Let's Encrypt beantragt (DNS-Prüfung über Azure DNS) und erneuert.</summary>
    Acme,
}

/// <summary>Angaben zu einem Zertifikat für die Oberfläche.</summary>
/// <param name="KeyProblem">Gesetzt, wenn der Server den privaten Schlüssel nicht benutzen kann.</param>
public sealed record CertificateInfo(string Subject, IReadOnlyList<string> Names, string Issuer, DateTime NotBeforeUtc, DateTime NotAfterUtc,
    string Thumbprint, bool SelfSigned, string? KeyProblem = null)
{
    public static CertificateInfo From(X509Certificate2 certificate, string? keyProblem = null) => new(
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false), ServerCertificates.Names(certificate),
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true), certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
        certificate.Thumbprint, certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData), keyProblem);
}

/// <summary>Erzeugen, Laden und Auswählen von Serverzertifikaten, ohne Zustand.</summary>
public static class ServerCertificates
{
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string SubjectAlternativeName = "2.5.29.17";
    private const string EnhancedKeyUsage = "2.5.29.37";

    /// <summary>Apple nimmt TLS-Serverzertifikate mit höchstens 825 Tagen Laufzeit an, auch solche, denen man selbst vertraut.</summary>
    public static readonly TimeSpan SelfSignedLifetime = TimeSpan.FromDays(824);

    /// <summary>So lange vor dem Ablauf erneuert der Server sein selbstsigniertes Zertifikat.</summary>
    public static readonly TimeSpan SelfSignedRenewBefore = TimeSpan.FromDays(60);

    /// <summary>
    /// Ein selbstsigniertes Serverzertifikat für die Namen; der erste wird der Antragsteller. Mit demselben Schlüssel erneuert,
    /// bleibt ein Pin auf den Schlüssel gültig.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(RSA key, IReadOnlyList<string> names, DateTimeOffset now)
    {
        if (names.Count == 0)
            throw new ArgumentException("At least one name is needed.", nameof(names));
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(names[0]);
        subject.AddOrganizationName("Inventory");
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternative = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var address))
                alternative.AddIpAddress(address);
            else
                alternative.AddDnsName(name);
        }
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(alternative.Build());
        // Eine Stunde zurück: Uhren der Clients gehen nicht genau.
        var notBefore = now.AddHours(-1);
        return request.CreateSelfSigned(notBefore, notBefore + SelfSignedLifetime);
    }

    /// <summary>DNS-Namen und IP-Adressen aus dem Zertifikat; ohne Erweiterung der allgemeine Name.</summary>
    public static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[SubjectAlternativeName];
        if (extension is null)
        {
            var name = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
            return string.IsNullOrEmpty(name) ? [] : [name];
        }
        var alternative = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
        return [.. alternative.EnumerateDnsNames(), .. alternative.EnumerateIPAddresses().Select(a => a.ToString())];
    }

    /// <summary>Ob das Zertifikat für den Namen (oder die Adresse) gilt, Platzhalter eingeschlossen.</summary>
    public static bool Covers(X509Certificate2 certificate, string host)
    {
        try
        {
            return certificate.MatchesHostname(host.Trim('[', ']'), allowWildcards: true, allowCommonName: true);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Ohne Angabe zum Verwendungszweck gilt ein Zertifikat für alles, sonst muss Serverauthentifizierung dabei sein.</summary>
    public static bool IsForServers(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[EnhancedKeyUsage];
        if (extension is null)
            return true;
        var usages = new X509EnhancedKeyUsageExtension(new AsnEncodedData(extension.Oid, extension.RawData), extension.Critical).EnhancedKeyUsages;
        return usages.Cast<Oid>().Any(o => o.Value == ServerAuthentication);
    }

    /// <summary>null, wenn der private Schlüssel benutzbar ist, sonst warum nicht (z. B. fehlende Rechte des Dienstkontos).</summary>
    public static string? KeyProblem(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
            return "The certificate has no private key.";
        try
        {
            using var key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey();
            return key is null ? "The private key uses an unsupported algorithm (RSA or ECDSA needed)." : null;
        }
        catch (CryptographicException ex)
        {
            return $"The private key cannot be used: {ex.Message}";
        }
    }

    /// <summary>
    /// Lädt eine PFX/P12-Datei (mit Passwort) oder eine PEM-Datei: das erste Zertifikat ist das des Servers, die übrigen bilden
    /// die Kette; der Schlüssel steht in derselben Datei oder in <paramref name="keyPath"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Die Datei passt nicht oder das Passwort ist falsch.</exception>
    public static (X509Certificate2 Certificate, X509Certificate2Collection Chain) LoadFile(string path, string? keyPath, string? password)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pfx" or ".p12")
            return LoadPkcs12(File.ReadAllBytes(path), password);

        var all = new X509Certificate2Collection();
        all.ImportFromPemFile(path);
        if (all.Count == 0)
            throw new CryptographicException("The file contains no certificate.");
        var keyFile = string.IsNullOrWhiteSpace(keyPath) ? path : keyPath;
        using var withKey = string.IsNullOrEmpty(password)
            ? X509Certificate2.CreateFromPemFile(path, keyFile)
            : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyFile);
        var chain = new X509Certificate2Collection();
        for (var i = 1; i < all.Count; i++)
            chain.Add(all[i]);
        return (Persistable(withKey), chain);
    }

    /// <summary>PKCS#12: das Zertifikat mit privatem Schlüssel ist das des Servers, die übrigen bilden die Kette.</summary>
    public static (X509Certificate2 Certificate, X509Certificate2Collection Chain) LoadPkcs12(byte[] data, string? password)
    {
        var all = X509CertificateLoader.LoadPkcs12Collection(data, password, X509KeyStorageFlags.Exportable);
        var leaf = all.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
                   ?? throw new CryptographicException("The file contains no certificate with a private key.");
        var chain = new X509Certificate2Collection();
        foreach (var other in all.Cast<X509Certificate2>().Where(c => !ReferenceEquals(c, leaf)))
            chain.Add(other);
        return (leaf, chain);
    }

    /// <summary>Zertifikat samt Schlüssel und Kette als PKCS#12 ohne Passwort; die Datei selbst schützt der Aufrufer.</summary>
    public static byte[] ExportPkcs12(X509Certificate2 certificate, X509Certificate2Collection? chain = null)
    {
        var all = new X509Certificate2Collection(certificate);
        if (chain is not null)
            all.AddRange(chain);
        return all.Export(X509ContentType.Pkcs12)!;
    }

    /// <summary>
    /// Aus PEM geladene Schlüssel sind nur flüchtig; Schannel (TLS unter Windows) kann sie nicht nutzen. Über PKCS#12 neu geladen
    /// liegt der Schlüssel so vor, wie ihn TLS auf allen Systemen braucht.
    /// </summary>
    public static X509Certificate2 Persistable(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

    /// <summary>
    /// Das neueste Zertifikat für den Namen, das jetzt gilt, einen privaten Schlüssel hat, für Server taugt und dem der Server
    /// vertraut. So greift eine Erneuerung (Autoenrollment, win-acme) ohne Zutun.
    /// </summary>
    public static X509Certificate2? Pick(IEnumerable<X509Certificate2> certificates, string name, DateTime nowUtc, Func<X509Certificate2, bool> trusted) =>
        certificates
            .Where(c => c.HasPrivateKey && c.NotBefore.ToUniversalTime() <= nowUtc && c.NotAfter.ToUniversalTime() > nowUtc && IsForServers(c) && Covers(c, name))
            .OrderByDescending(c => c.NotAfter)
            .ThenByDescending(c => c.NotBefore)
            .FirstOrDefault(trusted);

    /// <summary>Ob die Kette bis zu einer vertrauenswürdigen Stammzertifizierungsstelle des Servers reicht (ohne Sperrprüfung).</summary>
    public static bool Trusted(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    /// <summary>Die Zertifikate im Speicher "Eigene Zertifikate" des Computers; nur unter Windows.</summary>
    public static List<X509Certificate2> ReadMachineStore()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return [.. store.Certificates.Cast<X509Certificate2>()];
    }

    /// <summary>Namen dieses Rechners für das selbstsignierte Zertifikat: voller Name, Kurzname, localhost.</summary>
    public static List<string> MachineNames()
    {
        var names = new List<string>();
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrEmpty(properties.DomainName) && !string.IsNullOrEmpty(properties.HostName))
                names.Add($"{properties.HostName}.{properties.DomainName}".ToLowerInvariant());
        }
        catch (NetworkInformationException)
        {
        }
        names.Add(Dns.GetHostName().ToLowerInvariant());
        names.Add("localhost");
        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
