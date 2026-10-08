using System.Globalization;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

namespace Inventory.Data.Services;

public sealed record AdSyncSettings(bool Enabled, string? Server, string? BaseDns, int? CredentialId, int IntervalHours, DateTime? LastRunUtc, string? LastResult);

public sealed record AdSyncPreview(int Total, List<string> NewNames);

/// <summary>Legt Rechner an, die im Active Directory stehen, aber hier noch fehlen. Gelöscht wird nie automatisch.</summary>
public sealed class AdSyncService(IDirectorySource directory, SettingsService settings, CredentialService credentials, ComputerService computers, TimeProvider time)
{
    public async Task<AdSyncSettings> GetSettingsAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(SettingKeys.AdSyncEnabled, ct) == bool.TrueString,
        await settings.GetAsync(SettingKeys.AdSyncServer, ct),
        await settings.GetAsync(SettingKeys.AdSyncBaseDns, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.AdSyncCredentialId, ct), out var credentialId) ? credentialId : null,
        int.TryParse(await settings.GetAsync(SettingKeys.AdSyncIntervalHours, ct), out var hours) && hours > 0 ? hours : 24,
        DateTime.TryParse(await settings.GetAsync(SettingKeys.AdSyncLastRunUtc, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ? last : null,
        await settings.GetAsync(SettingKeys.AdSyncLastResult, ct));

    public async Task SaveSettingsAsync(bool enabled, string? server, string? baseDns, int? credentialId, int intervalHours, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.AdSyncEnabled, enabled.ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncServer, string.IsNullOrWhiteSpace(server) ? null : server.Trim(), ct);
        await settings.SetAsync(SettingKeys.AdSyncBaseDns, string.IsNullOrWhiteSpace(baseDns) ? null : baseDns.Trim(), ct);
        await settings.SetAsync(SettingKeys.AdSyncCredentialId, credentialId?.ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncIntervalHours, Math.Clamp(intervalHours, 1, 24 * 30).ToString(), ct);
    }

    /// <summary>Fragt das Verzeichnis ab, ohne etwas zu ändern.</summary>
    /// <exception cref="RemoteException">Das Verzeichnis ist nicht erreichbar.</exception>
    public async Task<AdSyncPreview> PreviewAsync(CancellationToken ct = default)
    {
        var found = await QueryAsync(await GetSettingsAsync(ct), ct);
        var known = (await computers.ListAsync(ct)).Select(c => c.Name.Split('.')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new AdSyncPreview(found.Count, found.Select(c => c.Name).Where(n => !known.Contains(n)).ToList());
    }

    /// <summary>Synchronisiert sofort; das Ergebnis wird auch in den Einstellungen festgehalten.</summary>
    public async Task<string> SyncAsync(CancellationToken ct = default)
    {
        string result;
        try
        {
            var preview = await PreviewAsync(ct);
            var added = await computers.ImportAsync(string.Join('\n', preview.NewNames), ct);
            result = $"{preview.Total} Computerkonten gefunden, {added.Count} neu angelegt";
        }
        catch (RemoteException ex)
        {
            result = $"Fehler: {ex.Message}";
        }
        await settings.SetAsync(SettingKeys.AdSyncLastRunUtc, time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.AdSyncLastResult, result, ct);
        return result;
    }

    /// <summary>Synchronisiert, wenn die Synchronisation aktiv und das Intervall abgelaufen ist.</summary>
    public async Task<bool> SyncIfDueAsync(CancellationToken ct = default)
    {
        var current = await GetSettingsAsync(ct);
        if (!current.Enabled || (current.LastRunUtc is { } last && time.GetUtcNow().UtcDateTime - last < TimeSpan.FromHours(current.IntervalHours)))
            return false;
        await SyncAsync(ct);
        return true;
    }

    private async Task<List<DirectoryComputer>> QueryAsync(AdSyncSettings current, CancellationToken ct)
    {
        var baseDns = (current.BaseDns ?? "").Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Ohne ausdrücklich gewählte Zugangsdaten fragt der Server mit seinem eigenen (Dienst-)Konto.
        var credential = current.CredentialId is null ? null : await credentials.GetAsync(current.CredentialId, ct);
        return await directory.GetComputersAsync(new DirectoryQuery(current.Server, baseDns, credential), ct);
    }
}
