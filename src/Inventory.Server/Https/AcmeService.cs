using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting.Acme;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Https;

/// <summary>Die Einstellungen für Let's Encrypt; das Geheimnis nur, wenn ausdrücklich gelesen.</summary>
public sealed record AcmeSettings(IReadOnlyList<string> Names, string? Email, bool Staging, AzureDnsSettings Azure)
{
    /// <summary>Namen aus einer Eingabe: durch Komma, Semikolon, Leerzeichen oder Zeilen getrennt, klein geschrieben, ohne Doppelte.</summary>
    public static List<string> ParseNames(string? text) =>
        [.. (text ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.TrimEnd('.').ToLowerInvariant()).Distinct()];
}

/// <summary>
/// Beantragt das HTTPS-Zertifikat bei Let's Encrypt (oder einer anderen ACME-Zertifizierungsstelle) mit der DNS-Prüfung über
/// Azure DNS und erneuert es, wenn ein Drittel der Laufzeit übrig ist. Der Server muss dafür aus dem Internet nicht
/// erreichbar sein; die Namen müssen in einer öffentlichen Zone in Azure liegen.
/// </summary>
public sealed class AcmeService
{
    /// <summary>Nach einem gescheiterten automatischen Antrag erst nach dieser Zeit wieder (Grenzen von Let's Encrypt).</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);
    private static readonly TimeSpan AlertInterval = TimeSpan.FromDays(1);

    private readonly ServerCertificateService _certificates;
    private readonly SettingsService _settings;
    private readonly ISecretProtector _secrets;
    private readonly IDataProtector _files;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<AcmeService> _logger;
    private readonly IStringLocalizer<SharedResource> L;
    private readonly IAlertSink? _alerts;
    private readonly HttpClient _http;
    private readonly Func<AcmeSettings, IDnsChallenge> _dns;
    private readonly string _folder;
    private readonly string? _directory;
    private int _running;
    private DateTime _lastFailureUtc = DateTime.MinValue;
    private DateTime _lastAlertUtc = DateTime.MinValue;

    /// <param name="directory">Adresse des ACME-Verzeichnisses statt Let's Encrypt (Inventory:Https:AcmeDirectory).</param>
    /// <param name="http">Nur für Tests: etwa für den Testserver Pebble mit eigenem Zertifikat.</param>
    /// <param name="dns">Nur für Tests: statt Azure DNS.</param>
    public AcmeService(ServerCertificateService certificates, SettingsService settings, ISecretProtector secrets, IDataProtectionProvider protection,
        AuditService audit, TimeProvider time, ILogger<AcmeService> logger, string dataDirectory, string? directory = null, IAlertSink? alerts = null,
        IStringLocalizer<SharedResource>? localizer = null, HttpClient? http = null, Func<AcmeSettings, IDnsChallenge>? dns = null)
    {
        _certificates = certificates;
        _settings = settings;
        _secrets = secrets;
        _files = protection.CreateProtector("Inventory.Https.v1");
        _audit = audit;
        _time = time;
        _logger = logger;
        L = localizer ?? PassthroughLocalizer.Instance;
        _alerts = alerts;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _dns = dns ?? (s => new AzureDnsChallenge(_http, s.Azure));
        _folder = Path.Combine(dataDirectory, "certificates");
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
    }

    /// <summary>So lange nach dem Setzen der TXT-Einträge warten, bis die Nameserver von Azure sie ausliefern.</summary>
    public TimeSpan Propagation { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Ein Antrag läuft gerade.</summary>
    public bool Running => _running != 0;

    /// <summary>Eine eigene ACME-Zertifizierungsstelle ist in appsettings.json eingetragen.</summary>
    public string? CustomDirectory => _directory;

    public async Task<AcmeSettings> ReadAsync(bool includeSecret = false, CancellationToken ct = default)
    {
        string? secret = null;
        if (includeSecret && await _settings.GetAsync(SettingKeys.ServerAcmeAzureSecret, ct) is { Length: > 0 } stored)
        {
            try
            {
                secret = _secrets.Unprotect(stored);
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning("The stored Azure secret cannot be decrypted: {Message}", ex.Message);
            }
        }
        return new AcmeSettings(AcmeSettings.ParseNames(await _settings.GetAsync(SettingKeys.ServerAcmeNames, ct)),
            await _settings.GetAsync(SettingKeys.ServerAcmeEmail, ct),
            bool.TryParse(await _settings.GetAsync(SettingKeys.ServerAcmeStaging, ct), out var staging) && staging,
            new AzureDnsSettings(await Get(SettingKeys.ServerAcmeAzureTenant), await Get(SettingKeys.ServerAcmeAzureClientId), secret ?? "",
                await Get(SettingKeys.ServerAcmeAzureSubscription), await Get(SettingKeys.ServerAcmeAzureResourceGroup), await Get(SettingKeys.ServerAcmeAzureZone)));

        async Task<string> Get(string key) => await _settings.GetAsync(key, ct) ?? "";
    }

    /// <summary>Speichert die Einstellungen; ein leeres Geheimnis behält das gespeicherte.</summary>
    public async Task SaveAsync(AcmeSettings value, CancellationToken ct = default)
    {
        await _settings.SetAsync(SettingKeys.ServerAcmeNames, value.Names.Count == 0 ? null : string.Join(", ", value.Names), ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeEmail, string.IsNullOrWhiteSpace(value.Email) ? null : value.Email.Trim(), ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeStaging, value.Staging ? bool.TrueString : null, ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeAzureTenant, Trimmed(value.Azure.TenantId), ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeAzureClientId, Trimmed(value.Azure.ClientId), ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeAzureSubscription, Trimmed(value.Azure.SubscriptionId), ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeAzureResourceGroup, Trimmed(value.Azure.ResourceGroup), ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeAzureZone, Trimmed(value.Azure.Zone)?.TrimEnd('.'), ct);
        if (!string.IsNullOrEmpty(value.Azure.ClientSecret))
            await _settings.SetAsync(SettingKeys.ServerAcmeAzureSecret, _secrets.Protect(value.Azure.ClientSecret), ct);
    }

    /// <summary>Ergebnis und Zeitpunkt des letzten Antrags.</summary>
    public async Task<(string? Result, DateTime? RunUtc)> LastAsync(CancellationToken ct = default) =>
        (await _settings.GetAsync(SettingKeys.ServerAcmeLastResult, ct),
         DateTime.TryParse(await _settings.GetAsync(SettingKeys.ServerAcmeLastRunUtc, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var run) ? run : null);

    /// <summary>Beantragt das Zertifikat mit den gespeicherten Einstellungen und wechselt darauf.</summary>
    /// <returns>null bei Erfolg, sonst die Meldung für die Oberfläche.</returns>
    public async Task<string?> RequestAsync(AuditActor actor, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return L["A request is already running."];
        try
        {
            var settings = await ReadAsync(includeSecret: true, ct);
            if (settings.Names.Count == 0)
                return L["Enter the names of the certificate."];
            if (settings.Azure.Missing() is { } missing)
                return L["Enter {0} for Azure DNS.", L[missing]];
            var directory = _directory is not null ? new Uri(_directory) : settings.Staging ? AcmeClient.LetsEncryptStaging : AcmeClient.LetsEncrypt;
            _logger.LogInformation("Requesting an HTTPS certificate for {Names} from {Directory}", string.Join(", ", settings.Names), directory);

            byte[] pkcs12;
            try
            {
                using var account = AccountKey(directory);
                using var key = RSA.Create(2048);
                var client = new AcmeClient(_http, directory, account);
                var pem = await client.OrderAsync(settings.Names, key, _dns(settings), Propagation, settings.Email, progress, ct);
                var chain = new X509Certificate2Collection();
                chain.ImportFromPem(pem);
                using var leaf = chain[0].CopyWithPrivateKey(key);
                chain.RemoveAt(0);
                pkcs12 = ServerCertificates.ExportPkcs12(leaf, chain);
            }
            catch (Exception ex) when (ex is AcmeException or HttpRequestException or CryptographicException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return await FailAsync(ex.Message, ct);
            }

            if (await _certificates.UseIssuedAsync(pkcs12, actor, ct) is { } refused)
                return await FailAsync(refused, ct);
            var issued = _certificates.Certificate!;
            await StoreResultAsync(L["Issued for {0}, valid until {1:yyyy-MM-dd}.", string.Join(", ", settings.Names), issued.NotAfter.ToUniversalTime()], ct);
            await _audit.LogAsync(actor, L["Let's Encrypt certificate issued"], $"{string.Join(", ", settings.Names)}{(settings.Staging ? " (staging)" : "")}, {issued.Thumbprint}", ct);
            _lastFailureUtc = DateTime.MinValue;
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Stündlich, wenn Let's Encrypt gewählt ist: beantragt neu, wenn kein Zertifikat da ist oder nur noch ein Drittel der
    /// Laufzeit übrig ist. Nach einem Fehlschlag frühestens nach 6 Stunden wieder; Fehlschläge melden sich als Alarm.
    /// </summary>
    public async Task RenewIfDueAsync(CancellationToken ct = default)
    {
        if ((await _certificates.ReadChoiceAsync(ct: ct)).Source != ServerCertificateSource.Acme || Running)
            return;
        var now = _time.GetUtcNow().UtcDateTime;
        var current = _certificates.Source == ServerCertificateSource.Acme ? _certificates.Certificate : null;
        if (current is not null && !IsDue(current, now))
            return;
        if (now - _lastFailureUtc < RetryAfterFailure)
            return;
        if (await RequestAsync(AuditActor.System(L["HTTPS"]), null, ct) is { } error)
        {
            _lastFailureUtc = now;
            if (_alerts is not null && now - _lastAlertUtc >= AlertInterval)
            {
                _lastAlertUtc = now;
                _alerts.Raise(new AlertEvent(AlertKind.ServerCertificate, L["Let's Encrypt certificate could not be renewed"], error, "settings"));
            }
        }
    }

    /// <summary>Fällig, wenn weniger als ein Drittel der Laufzeit übrig ist (bei 90 Tagen: 30 Tage vor dem Ende).</summary>
    public static bool IsDue(X509Certificate2 certificate, DateTime nowUtc)
    {
        var notAfter = certificate.NotAfter.ToUniversalTime();
        return notAfter - nowUtc < (notAfter - certificate.NotBefore.ToUniversalTime()) / 3;
    }

    /// <summary>
    /// Zeigt der Name auf diesen Server? Browser und Agenten müssen ihn intern hierher auflösen; Let's Encrypt prüft nur die
    /// öffentliche Zone. null = ja (oder ein Platzhalter, der sich nicht prüfen lässt), sonst ein Hinweis.
    /// </summary>
    public async Task<string?> CheckNameAsync(string name, CancellationToken ct = default)
    {
        if (name.StartsWith("*.", StringComparison.Ordinal))
            return null;
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(name, ct);
        }
        catch (SocketException)
        {
            return L["{0} cannot be resolved here. Add it to the internal DNS so that it points to this server.", name];
        }
        var own = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address).ToHashSet();
        return addresses.Any(a => IPAddress.IsLoopback(a) || own.Contains(a))
            ? null
            : L["{0} points to {1}, not to this server. Browsers and agents would end up there.", name, string.Join(", ", addresses.Select(a => a.ToString()))];
    }

    private async Task<string> FailAsync(string message, CancellationToken ct)
    {
        _logger.LogError("Requesting the HTTPS certificate failed: {Message}", message);
        await StoreResultAsync(L["Failed: {0}", message], ct);
        return message;
    }

    private async Task StoreResultAsync(string result, CancellationToken ct)
    {
        await _settings.SetAsync(SettingKeys.ServerAcmeLastResult, result, ct);
        await _settings.SetAsync(SettingKeys.ServerAcmeLastRunUtc, _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Der Kontoschlüssel je Zertifizierungsstelle (Let's Encrypt und seine Testumgebung haben getrennte Konten).</summary>
    private ECDsa AccountKey(Uri directory)
    {
        var name = $"acme-account-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.AbsoluteUri)))[..12].ToLowerInvariant()}.key";
        var path = Path.Combine(_folder, name);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            try
            {
                key.ImportPkcs8PrivateKey(_files.Unprotect(File.ReadAllBytes(path)), out _);
                return key;
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning("The ACME account key cannot be read, creating a new account: {Message}", ex.Message);
            }
        }
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(path, _files.Protect(key.ExportPkcs8PrivateKey()));
        return key;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
