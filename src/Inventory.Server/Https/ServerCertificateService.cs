using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Https;

/// <summary>Die Wahl in den Einstellungen.</summary>
/// <param name="StoreName">Zertifikatspeicher: der DNS-Name, für den das neueste gültige Zertifikat gilt.</param>
/// <param name="Password">Passwort der Datei; null = das gespeicherte behalten.</param>
public sealed record CertificateChoice(ServerCertificateSource Source, string? StoreName = null, string? File = null, string? KeyFile = null, string? Password = null);

/// <summary>Eine Meldung für die Oberfläche, wenn ein Zertifikat nicht taugt.</summary>
public sealed class CertificateException(string message) : Exception(message);

/// <summary>
/// Hält das HTTPS-Zertifikat des Servers. Ein Wechsel (andere Quelle, Erneuerung) greift sofort, ohne Neustart: Kestrel holt
/// sich bei jedem Verbindungsaufbau den aktuellen <see cref="Context"/>. Taugt das gewählte Zertifikat nicht, bleibt das
/// bisherige aktiv; beim Start springt das selbstsignierte ein.
/// </summary>
public sealed class ServerCertificateService
{
    /// <summary>So lange vor dem Ablauf meldet sich der Alarm „Serverzertifikat“; das selbstsignierte erneuert sich selbst.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);
    private static readonly TimeSpan AlertInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const string SelfSignedKeyFile = "self-signed.key";
    private const string SelfSignedCertificateFile = "self-signed.cer";
    private const string UploadFile = "upload.pfx";
    private const string AcmeFile = "acme.pfx";

    private sealed record Active(X509Certificate2 Certificate, SslStreamCertificateContext Context, ServerCertificateSource Source);

    private readonly SettingsService _settings;
    private readonly ISecretProtector _secrets;
    private readonly IDataProtector _files;
    private readonly AuditService _audit;
    private readonly IAlertSink? _alerts;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerCertificateService> _logger;
    private readonly IStringLocalizer<SharedResource> L;
    private readonly string _folder;
    private readonly Func<List<X509Certificate2>> _machineStore;
    private readonly Func<X509Certificate2, bool> _trusted;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Active? _active;
    private DateTime _lastAlertUtc = DateTime.MinValue;

    /// <param name="machineStore">Nur für Tests: statt des Zertifikatspeichers des Computers.</param>
    /// <param name="trusted">Nur für Tests: statt der Kettenprüfung gegen die Stammzertifikate des Servers.</param>
    public ServerCertificateService(SettingsService settings, ISecretProtector secrets, IDataProtectionProvider protection, AuditService audit,
        TimeProvider time, ILogger<ServerCertificateService> logger, string dataDirectory, IAlertSink? alerts = null,
        IStringLocalizer<SharedResource>? localizer = null, Func<List<X509Certificate2>>? machineStore = null, Func<X509Certificate2, bool>? trusted = null)
    {
        _settings = settings;
        _secrets = secrets;
        _files = protection.CreateProtector("Inventory.Https.v1");
        _audit = audit;
        _alerts = alerts;
        _time = time;
        _logger = logger;
        L = localizer ?? PassthroughLocalizer.Instance;
        _folder = Path.Combine(dataDirectory, "certificates");
        _machineStore = machineStore ?? ServerCertificates.ReadMachineStore;
        _trusted = trusted ?? ServerCertificates.Trusted;
    }

    /// <summary>Für Kestrel: Zertifikat samt Kette; null, solange keines geladen ist.</summary>
    public SslStreamCertificateContext? Context => _active?.Context;

    public X509Certificate2? Certificate => _active?.Certificate;

    /// <summary>Woher das aktive Zertifikat stammt; nach einem Fehler beim Start kann das vom gewählten abweichen.</summary>
    public ServerCertificateSource? Source => _active?.Source;

    /// <summary>Warum das gewählte Zertifikat nicht aktiv ist oder sich nicht erneuern ließ; null = alles in Ordnung.</summary>
    public string? Problem { get; private set; }

    /// <summary>Das aktive Zertifikat hat gewechselt.</summary>
    public event Action? Changed;

    /// <summary>Lädt das gewählte Zertifikat; scheitert das, springt das selbstsignierte ein und <see cref="Problem"/> sagt warum.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var choice = await ReadChoiceAsync(includePassword: true, ct);
            try
            {
                Activate(await LoadAsync(choice, ct), problem: null);
            }
            catch (Exception ex) when (choice.Source != ServerCertificateSource.SelfSigned && IsCertificateError(ex))
            {
                _logger.LogError("The HTTPS certificate ({Source}) cannot be loaded, using the self-signed one: {Message}", choice.Source, ex.Message);
                Activate(await LoadSelfSignedAsync(ct), L["The chosen certificate cannot be loaded, the self-signed one is used instead: {0}", ex.Message]);
                Raise(L["HTTPS certificate cannot be loaded"], ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Die Wahl aus den Einstellungen; das Passwort nur auf Wunsch (für die Oberfläche bleibt es verborgen).</summary>
    public async Task<CertificateChoice> ReadChoiceAsync(bool includePassword = false, CancellationToken ct = default)
    {
        var source = Enum.TryParse<ServerCertificateSource>(await _settings.GetAsync(SettingKeys.ServerCertificateSource, ct), out var s) ? s : ServerCertificateSource.SelfSigned;
        string? password = null;
        if (includePassword && await _settings.GetAsync(SettingKeys.ServerCertificatePassword, ct) is { Length: > 0 } stored)
        {
            try
            {
                password = _secrets.Unprotect(stored);
            }
            catch (CryptographicException ex)
            {
                // Wie ein fehlendes Passwort: das Laden der Datei scheitert dann mit einer Meldung, beim Start greift das selbstsignierte.
                _logger.LogWarning("The stored password of the certificate file cannot be decrypted: {Message}", ex.Message);
            }
        }
        return new CertificateChoice(source, await _settings.GetAsync(SettingKeys.ServerCertificateStoreName, ct), await _settings.GetAsync(SettingKeys.ServerCertificateFile, ct),
            await _settings.GetAsync(SettingKeys.ServerCertificateKeyFile, ct), password);
    }

    /// <summary>Wechselt auf die Wahl, wenn ihr Zertifikat taugt, und speichert sie; sonst bleibt alles, wie es war.</summary>
    /// <returns>null bei Erfolg, sonst die Meldung für die Oberfläche.</returns>
    public async Task<string?> ApplyAsync(CertificateChoice choice, AuditActor actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            choice = choice with { StoreName = Trimmed(choice.StoreName), File = Trimmed(choice.File), KeyFile = Trimmed(choice.KeyFile) };
            var password = choice.Password;
            if (password is null && choice.Source == ServerCertificateSource.File)
                password = (await ReadChoiceAsync(includePassword: true, ct)).Password;
            Active next;
            try
            {
                next = await LoadAsync(choice with { Password = password }, ct);
            }
            catch (Exception ex) when (IsCertificateError(ex))
            {
                return ex.Message;
            }

            await _settings.SetAsync(SettingKeys.ServerCertificateSource, choice.Source.ToString(), ct);
            await _settings.SetAsync(SettingKeys.ServerCertificateStoreName, choice.Source == ServerCertificateSource.MachineStore ? choice.StoreName : null, ct);
            await _settings.SetAsync(SettingKeys.ServerCertificateFile, choice.Source == ServerCertificateSource.File ? choice.File : null, ct);
            await _settings.SetAsync(SettingKeys.ServerCertificateKeyFile, choice.Source == ServerCertificateSource.File ? choice.KeyFile : null, ct);
            await _settings.SetAsync(SettingKeys.ServerCertificatePassword,
                choice.Source == ServerCertificateSource.File && !string.IsNullOrEmpty(password) ? _secrets.Protect(password) : null, ct);
            Activate(next, problem: null);
            await _audit.LogAsync(actor, L["HTTPS certificate changed"], Describe(next), ct);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Nimmt eine hochgeladene PFX-Datei an und wechselt darauf.</summary>
    /// <returns>null bei Erfolg, sonst die Meldung für die Oberfläche.</returns>
    public async Task<string?> UploadAsync(byte[] data, string? password, AuditActor actor, CancellationToken ct = default)
    {
        byte[] stored;
        try
        {
            var (certificate, chain) = ServerCertificates.LoadPkcs12(data, string.IsNullOrEmpty(password) ? null : password);
            Validate(certificate);
            stored = ServerCertificates.ExportPkcs12(certificate, chain);
        }
        catch (Exception ex) when (IsCertificateError(ex))
        {
            return ex.Message;
        }
        WriteProtected(UploadFile, stored);
        return await ApplyAsync(new CertificateChoice(ServerCertificateSource.Upload), actor, ct);
    }

    /// <summary>Gibt es schon ein von Let's Encrypt ausgestelltes Zertifikat?</summary>
    public bool HasIssued => System.IO.File.Exists(Path.Combine(_folder, AcmeFile));

    /// <summary>Legt ein von Let's Encrypt ausgestelltes Zertifikat (PKCS#12 mit Schlüssel und Kette) ab und wechselt darauf.</summary>
    /// <returns>null bei Erfolg, sonst die Meldung für die Oberfläche.</returns>
    public async Task<string?> UseIssuedAsync(byte[] pkcs12, AuditActor actor, CancellationToken ct = default)
    {
        try
        {
            var (certificate, _) = ServerCertificates.LoadPkcs12(pkcs12, null);
            using (certificate)
                Validate(certificate);
        }
        catch (Exception ex) when (IsCertificateError(ex))
        {
            return ex.Message;
        }
        WriteProtected(AcmeFile, pkcs12);
        return await ApplyAsync(new CertificateChoice(ServerCertificateSource.Acme), actor, ct);
    }

    /// <summary>
    /// Stündlich: erneuert das selbstsignierte, nimmt ein erneuertes aus dem Speicher oder eine geänderte Datei, versucht nach
    /// einem Fehler das gewählte wieder und meldet ein bald ablaufendes Zertifikat.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var choice = await ReadChoiceAsync(includePassword: true, ct);
            var current = _active;
            Active next;
            try
            {
                next = await LoadAsync(choice, ct);
            }
            catch (Exception ex) when (IsCertificateError(ex))
            {
                _logger.LogError("The HTTPS certificate ({Source}) cannot be loaded: {Message}", choice.Source, ex.Message);
                Problem = current?.Source == choice.Source
                    ? L["The certificate cannot be reloaded, the previous one stays active: {0}", ex.Message]
                    : L["The chosen certificate cannot be loaded, the self-signed one is used instead: {0}", ex.Message];
                Raise(L["HTTPS certificate cannot be loaded"], ex.Message);
                return;
            }
            if (current is null || current.Source != next.Source || current.Certificate.Thumbprint != next.Certificate.Thumbprint)
            {
                Activate(next, problem: null);
                await _audit.LogAsync(AuditActor.System(L["HTTPS"]), L["HTTPS certificate replaced"], Describe(next), ct);
            }
            else
            {
                // Unverändert: das neu geladene wird nicht gebraucht.
                next.Certificate.Dispose();
                Problem = null;
            }

            var active = _active!;
            var left = active.Certificate.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime;
            if (active.Source != ServerCertificateSource.SelfSigned && left <= ExpiryWarning)
                Raise(L["HTTPS certificate expires soon"], L["The HTTPS certificate {0} expires on {1:yyyy-MM-dd}.", active.Certificate.Subject, active.Certificate.NotAfter.ToUniversalTime()]);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Das aktive Zertifikat ohne Schlüssel (DER), zum Verteilen per Gruppenrichtlinie oder MDM.</summary>
    public byte[]? ExportPublic() => _active?.Certificate.Export(X509ContentType.Cert);

    /// <summary>Gültige Serverzertifikate mit privatem Schlüssel im Speicher des Computers; leer außerhalb von Windows.</summary>
    public List<CertificateInfo> StoreCandidates()
    {
        var certificates = _machineStore();
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return [.. certificates
                .Where(c => c.HasPrivateKey && c.NotAfter.ToUniversalTime() > now && ServerCertificates.IsForServers(c))
                .Select(c => CertificateInfo.From(c, ServerCertificates.KeyProblem(c)))
                .OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(c => c.NotAfterUtc)];
        }
        finally
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
        }
    }

    private async Task<Active> LoadAsync(CertificateChoice choice, CancellationToken ct)
    {
        switch (choice.Source)
        {
            case ServerCertificateSource.SelfSigned:
                return await LoadSelfSignedAsync(ct);

            case ServerCertificateSource.MachineStore:
            {
                if (string.IsNullOrWhiteSpace(choice.StoreName))
                    throw new CertificateException(L["Choose a certificate from the certificate store."]);
                var certificates = _machineStore();
                X509Certificate2? picked = null;
                try
                {
                    picked = ServerCertificates.Pick(certificates, choice.StoreName, _time.GetUtcNow().UtcDateTime, _trusted)
                             ?? throw new CertificateException(L["The certificate store of the computer has no valid, trusted certificate with a private key for {0}.", choice.StoreName]);
                    if (ServerCertificates.KeyProblem(picked) is { } problem)
                    {
                        picked = null;
                        throw new CertificateException(L["The private key of the certificate cannot be used – does the service account have access to it? {0}", problem]);
                    }
                    return Prepare(picked, null, ServerCertificateSource.MachineStore);
                }
                finally
                {
                    foreach (var certificate in certificates.Where(c => !ReferenceEquals(c, picked)))
                        certificate.Dispose();
                }
            }

            case ServerCertificateSource.File:
            {
                if (string.IsNullOrWhiteSpace(choice.File))
                    throw new CertificateException(L["Enter the path of the certificate file."]);
                if (!System.IO.File.Exists(choice.File))
                    throw new CertificateException(L["The file {0} does not exist on the server.", choice.File]);
                if (!string.IsNullOrWhiteSpace(choice.KeyFile) && !System.IO.File.Exists(choice.KeyFile))
                    throw new CertificateException(L["The file {0} does not exist on the server.", choice.KeyFile]);
                var (certificate, chain) = ServerCertificates.LoadFile(choice.File, choice.KeyFile, choice.Password);
                Validate(certificate);
                return Prepare(certificate, chain, ServerCertificateSource.File);
            }

            case ServerCertificateSource.Upload:
            {
                var data = ReadProtected(UploadFile) ?? throw new CertificateException(L["Upload a PFX file first."]);
                var (certificate, chain) = ServerCertificates.LoadPkcs12(data, null);
                Validate(certificate);
                return Prepare(certificate, chain, ServerCertificateSource.Upload);
            }

            case ServerCertificateSource.Acme:
            {
                var data = ReadProtected(AcmeFile) ?? throw new CertificateException(L["Let's Encrypt has not issued a certificate yet."]);
                var (certificate, chain) = ServerCertificates.LoadPkcs12(data, null);
                Validate(certificate);
                return Prepare(certificate, chain, ServerCertificateSource.Acme);
            }

            default:
                throw new CertificateException(choice.Source.ToString());
        }
    }

    /// <summary>
    /// Das selbstsignierte Zertifikat: beim ersten Mal erzeugt, danach mit demselben Schlüssel erneuert, wenn es bald abläuft oder
    /// ein Name fehlt (etwa nach einer neuen Server-Adresse).
    /// </summary>
    private async Task<Active> LoadSelfSignedAsync(CancellationToken ct)
    {
        var names = await SelfSignedNamesAsync(ct);
        var now = _time.GetUtcNow();
        using var key = RSA.Create(2048);
        var certificatePath = Path.Combine(_folder, SelfSignedCertificateFile);
        var imported = false;
        if (ReadProtected(SelfSignedKeyFile) is { } storedKey)
        {
            try
            {
                key.ImportPkcs8PrivateKey(storedKey, out _);
                imported = true;
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning("The key of the self-signed HTTPS certificate cannot be read, creating a new one: {Message}", ex.Message);
            }
        }
        if (!imported)
            WriteProtected(SelfSignedKeyFile, key.ExportPkcs8PrivateKey());

        X509Certificate2? existing = null;
        if (imported && System.IO.File.Exists(certificatePath))
        {
            try
            {
                existing = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning("The self-signed HTTPS certificate cannot be read, creating a new one: {Message}", ex.Message);
            }
        }

        if (existing is not null && SameKey(existing, key)
            && existing.NotAfter.ToUniversalTime() - now.UtcDateTime > ServerCertificates.SelfSignedRenewBefore
            && names.All(n => ServerCertificates.Names(existing).Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            using (existing)
            using (var withKey = existing.CopyWithPrivateKey(key))
                return Prepare(ServerCertificates.Persistable(withKey), null, ServerCertificateSource.SelfSigned);
        }

        using var created = ServerCertificates.CreateSelfSigned(key, names, now);
        Directory.CreateDirectory(_folder);
        AtomicWrite(certificatePath, created.Export(X509ContentType.Cert));
        if (existing is not null)
        {
            _logger.LogInformation("Self-signed HTTPS certificate renewed with the same key: {Names}", string.Join(", ", names));
            existing.Dispose();
        }
        return Prepare(ServerCertificates.Persistable(created), null, ServerCertificateSource.SelfSigned);
    }

    /// <summary>Namen des Rechners und die Hosts der eingetragenen Adressen, damit Browser und Agenten unter jedem davon passen.</summary>
    private async Task<List<string>> SelfSignedNamesAsync(CancellationToken ct)
    {
        var names = ServerCertificates.MachineNames();
        foreach (var key in new[] { SettingKeys.AgentServerUrl, SettingKeys.ServerHttpsAddress })
            if (Uri.TryCreate(await _settings.GetAsync(key, ct), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.IdnHost)
                && !names.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
                names.Add(uri.IdnHost.Trim('[', ']').ToLowerInvariant());
        return names;
    }

    private static bool SameKey(X509Certificate2 certificate, RSA key)
    {
        using var publicKey = certificate.GetRSAPublicKey();
        return publicKey is not null && publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo());
    }

    private void Validate(X509Certificate2 certificate)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (ServerCertificates.KeyProblem(certificate) is { } problem)
            throw new CertificateException(problem);
        if (!ServerCertificates.IsForServers(certificate))
            throw new CertificateException(L["The certificate is not meant for servers (server authentication is missing)."]);
        if (certificate.NotAfter.ToUniversalTime() <= now)
            throw new CertificateException(L["The certificate expired on {0:yyyy-MM-dd}.", certificate.NotAfter.ToUniversalTime()]);
        // Etwas Spielraum: ein gerade ausgestelltes Zertifikat gilt ab dem Moment der Ausstellung, und die Uhr des Servers kann
        // ein wenig hinter der der Zertifizierungsstelle liegen.
        if (certificate.NotBefore.ToUniversalTime() > now + ClockSkew)
            throw new CertificateException(L["The certificate is only valid from {0:yyyy-MM-dd}.", certificate.NotBefore.ToUniversalTime()]);
    }

    /// <summary>Baut die Kette einmal; fehlende Zwischenzertifikate holt .NET bei Bedarf über die Adresse im Zertifikat.</summary>
    private static Active Prepare(X509Certificate2 certificate, X509Certificate2Collection? chain, ServerCertificateSource source) =>
        new(certificate, SslStreamCertificateContext.Create(certificate, chain ?? [], offline: false), source);

    private void Activate(Active next, string? problem)
    {
        _active = next;
        Problem = problem;
        _logger.LogInformation("HTTPS certificate ({Source}): {Subject}, valid until {NotAfter:yyyy-MM-dd}, {Thumbprint}",
            next.Source, next.Certificate.Subject, next.Certificate.NotAfter, next.Certificate.Thumbprint);
        Changed?.Invoke();
    }

    private string Describe(Active active) =>
        $"{active.Source}: {active.Certificate.Subject} ({string.Join(", ", ServerCertificates.Names(active.Certificate))}), {L["valid until {0:yyyy-MM-dd}", active.Certificate.NotAfter]}, {active.Certificate.Thumbprint}";

    /// <summary>Höchstens einmal am Tag, damit ein dauerhaftes Problem die Kanäle nicht flutet.</summary>
    private void Raise(string title, string text)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_alerts is null || now - _lastAlertUtc < AlertInterval)
            return;
        _lastAlertUtc = now;
        _alerts.Raise(new AlertEvent(AlertKind.ServerCertificate, title, text, "settings"));
    }

    private byte[]? ReadProtected(string name)
    {
        var path = Path.Combine(_folder, name);
        if (!System.IO.File.Exists(path))
            return null;
        try
        {
            return _files.Unprotect(System.IO.File.ReadAllBytes(path));
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning("{File} cannot be decrypted: {Message}", path, ex.Message);
            return null;
        }
    }

    private void WriteProtected(string name, byte[] data)
    {
        Directory.CreateDirectory(_folder);
        AtomicWrite(Path.Combine(_folder, name), _files.Protect(data));
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        var temporary = path + ".tmp";
        System.IO.File.WriteAllBytes(temporary, data);
        System.IO.File.Move(temporary, path, overwrite: true);
    }

    private static bool IsCertificateError(Exception ex) => ex is CertificateException or CryptographicException or IOException or UnauthorizedAccessException;

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
