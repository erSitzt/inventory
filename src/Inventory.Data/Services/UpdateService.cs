using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Inventory.Core;
using Inventory.Core.Abstractions;

namespace Inventory.Data.Services;

public sealed class UpdateOptions
{
    /// <summary>GitHub-Repository in der Form besitzer/name.</summary>
    public string Repository { get; set; } = "erSitzt/inventory";
    public bool Enabled { get; set; } = true;
    public required string CurrentVersion { get; init; }
    public required string UpdatesDirectory { get; init; }

    /// <summary>null = der Server kann sich selbst aktualisieren; sonst der Grund, warum nicht.</summary>
    public string? CannotInstallReason { get; init; }

    /// <summary>Startet das Setup losgelöst vom Server (Setup-Pfad, Log-Pfad). Es beendet den Dienst und startet ihn neu.</summary>
    public required Action<string, string> Launch { get; init; }

    /// <summary>Prüft die Signatur des Setups; liefert eine Fehlermeldung oder null. Nicht gesetzt = keine Signaturprüfung.</summary>
    public Func<string, string?>? VerifySignature { get; init; }
}

public sealed record UpdateSettings(bool Automatic, TimeSpan WindowStart);

/// <summary>Sucht nach neuen Versionen und spielt sie auf Wunsch oder automatisch im Wartungsfenster ein.</summary>
public sealed class UpdateService(IReleaseSource source, SettingsService settings, BackupService backups, DeploymentService deployments,
    AuditService audit, TimeProvider time, UpdateOptions options)
{
    private const string AutoKey = "Update.Automatic";
    private const string WindowKey = "Update.WindowStart";
    private const string AttemptedKey = "Update.AttemptedVersion";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(2);
    /// <summary>Hat das Setup den Dienst nach dieser Zeit nicht neu gestartet, gilt der Versuch als gescheitert.</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string CurrentVersion => options.CurrentVersion;
    public string Repository => options.Repository;
    public bool Enabled => options.Enabled;
    public string? CannotInstallReason => options.CannotInstallReason;

    /// <summary>Lokal gebaute Stände tragen 0.0.0; für sie wäre jedes Release "neuer".</summary>
    public bool IsDevelopmentBuild => options.CurrentVersion.StartsWith("0.0.0", StringComparison.Ordinal);

    public ReleaseInfo? Latest { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }
    public string? LastError { get; private set; }
    public bool Installing { get; private set; }

    public bool UpdateAvailable => Latest is not null && !IsDevelopmentBuild && SemVer.Compare(options.CurrentVersion, Latest.Version) < 0;

    /// <summary>Wird aus Hintergrund-Threads ausgelöst, wenn sich der Update-Stand ändert.</summary>
    public event Action? Changed;

    private string MarkerPath => Path.Combine(options.UpdatesDirectory, "pending.json");

    private sealed record Marker(string From, string To, string Log, DateTime StartedUtc, string User);

    /// <summary>Fragt die Release-Quelle ab; ohne <paramref name="force"/> höchstens alle paar Stunden.</summary>
    public async Task CheckAsync(bool force = false, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (!options.Enabled || IsDevelopmentBuild || (!force && LastCheckUtc is { } last && now - last < CheckInterval))
            return;
        LastCheckUtc = now;
        try
        {
            Latest = await source.GetLatestAsync(options.Repository, ct);
            LastError = null;
        }
        catch (RemoteException ex)
        {
            LastError = ex.Message;
        }
        Changed?.Invoke();
    }

    public async Task<UpdateSettings> GetSettingsAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(AutoKey, ct) == bool.TrueString,
        TimeSpan.TryParse(await settings.GetAsync(WindowKey, ct), CultureInfo.InvariantCulture, out var start) ? start : new TimeSpan(3, 0, 0));

    public async Task SaveSettingsAsync(bool automatic, TimeSpan windowStart, CancellationToken ct = default)
    {
        await settings.SetAsync(AutoKey, automatic.ToString(), ct);
        await settings.SetAsync(WindowKey, windowStart.ToString("c", CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Lädt das Setup, prüft es, sichert die Datenbank und startet die Installation. Liefert eine Fehlermeldung oder null.</summary>
    public async Task<string?> ApplyAsync(string user, CancellationToken ct = default)
    {
        if (!await _lock.WaitAsync(0, ct))
            return "Eine Aktualisierung läuft bereits.";
        try
        {
            if (options.CannotInstallReason is { } reason)
                return reason;
            if (!UpdateAvailable || Latest is not { SetupUrl: not null, SetupName: not null } release)
                return "Es liegt keine neuere Version mit Setup vor.";
            if (release.ChecksumsUrl is null)
                return "Das Release enthält keine Prüfsummen; ohne sie wird nichts installiert.";
            if (deployments.AnyRunning)
                return "Es laufen noch Deployments. Bitte warten, bis sie beendet sind.";

            Installing = true;
            Changed?.Invoke();
            Directory.CreateDirectory(options.UpdatesDirectory);
            // Nur der Dateiname: ein präparierter Asset-Name darf nicht aus dem Update-Ordner herausführen.
            var setup = Path.Combine(options.UpdatesDirectory, Path.GetFileName(release.SetupName));
            var sums = Path.Combine(options.UpdatesDirectory, "SHA256SUMS.txt");
            await source.DownloadAsync(release.ChecksumsUrl, sums, ct);
            await source.DownloadAsync(release.SetupUrl, setup, ct);

            if (VerifyChecksum(setup, await File.ReadAllTextAsync(sums, ct)) is { } checksumError)
                return Fail(setup, checksumError);
            if (options.VerifySignature?.Invoke(setup) is { } signatureError)
                return Fail(setup, signatureError);

            await backups.BackupAsync(ct);
            var log = Path.Combine(options.UpdatesDirectory, $"setup-{release.Version}.log");
            await File.WriteAllTextAsync(MarkerPath, JsonSerializer.Serialize(new Marker(options.CurrentVersion, release.Version, log, time.GetUtcNow().UtcDateTime, user)), ct);
            await settings.SetAsync(AttemptedKey, release.Version, ct);
            await audit.LogAsync(user, "Aktualisierung gestartet", $"{options.CurrentVersion} → {release.Version}", ct);
            options.Launch(setup, log);
            return null;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Fail(null, $"Aktualisierung fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Automatik: installiert, wenn sie eingeschaltet ist, das Wartungsfenster läuft und nichts dagegen spricht.</summary>
    public async Task<bool> ApplyIfDueAsync(CancellationToken ct = default)
    {
        if (!UpdateAvailable || Installing || options.CannotInstallReason is not null || deployments.AnyRunning)
            return false;
        var current = await GetSettingsAsync(ct);
        if (!current.Automatic || !InWindow(current.WindowStart))
            return false;
        // Jede Version nur einmal automatisch versuchen: ein fehlschlagendes Setup soll nicht jede Nacht erneut laufen.
        if (await settings.GetAsync(AttemptedKey, ct) == Latest!.Version)
            return false;
        return await ApplyAsync("(automatisch)", ct) is null;
    }

    /// <summary>
    /// Wertet einen begonnenen Update-Versuch aus und schreibt das Ergebnis ins Protokoll. Beim Start immer;
    /// im laufenden Betrieb nur, wenn das Setup den Dienst längst hätte neu starten müssen.
    /// </summary>
    public async Task ReportPreviousAsync(bool startup, CancellationToken ct = default)
    {
        if (!File.Exists(MarkerPath))
            return;
        Marker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<Marker>(await File.ReadAllTextAsync(MarkerPath, ct));
        }
        catch (JsonException)
        {
            marker = null;
        }
        if (marker is null)
        {
            File.Delete(MarkerPath);
            return;
        }
        if (!startup && time.GetUtcNow().UtcDateTime - marker.StartedUtc < InstallTimeout)
            return;

        File.Delete(MarkerPath);
        Installing = false;
        if (SemVer.Compare(options.CurrentVersion, marker.To) == 0)
        {
            await audit.LogAsync(marker.User, "Aktualisierung abgeschlossen", $"{marker.From} → {marker.To}", ct);
        }
        else
        {
            var tail = File.Exists(marker.Log) ? string.Join(" | ", (await File.ReadAllLinesAsync(marker.Log, ct)).TakeLast(6)) : "kein Setup-Protokoll vorhanden";
            LastError = $"Die Aktualisierung auf {marker.To} ist fehlgeschlagen; es läuft weiter {options.CurrentVersion}. Setup-Protokoll: {marker.Log}";
            await audit.LogAsync(marker.User, "Aktualisierung fehlgeschlagen", $"{marker.From} → {marker.To}: {tail}", ct);
        }
        Changed?.Invoke();
    }

    private bool InWindow(TimeSpan start)
    {
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).TimeOfDay;
        // Über Mitternacht hinweg rechnen: ein Fenster ab 23:00 reicht bis 01:00.
        var sinceStart = (local - start + TimeSpan.FromDays(1)).Ticks % TimeSpan.TicksPerDay;
        return sinceStart < WindowLength.Ticks;
    }

    private string Fail(string? setup, string message)
    {
        if (setup is not null && File.Exists(setup))
            File.Delete(setup);
        Installing = false;
        LastError = message;
        Changed?.Invoke();
        return message;
    }

    /// <summary>Vergleicht die Datei mit ihrem Eintrag in einer SHA256SUMS-Liste; liefert eine Fehlermeldung oder null.</summary>
    internal static string? VerifyChecksum(string path, string checksums)
    {
        var name = Path.GetFileName(path);
        var expected = checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*').Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();
        if (expected is null)
            return $"Für {name} steht keine Prüfsumme in der Liste des Releases.";
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Die Prüfsumme von {name} stimmt nicht mit dem Release überein; die Datei wurde verworfen.";
    }
}
