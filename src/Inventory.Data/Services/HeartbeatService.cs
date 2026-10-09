using System.Globalization;
using Inventory.Core.Model;

namespace Inventory.Data.Services;

public sealed record HeartbeatSettings(bool Enabled, int IntervalMinutes, DateTime? LastRunUtc, string? LastResult);

/// <summary>
/// Heartbeat: prüft in festen Abständen die Erreichbarkeit aller Rechner, damit der Online-Status nicht erst beim nächsten
/// Scan oder Klick aktuell wird. Rechner mit Namensauffälligkeiten (ein anderer Rechner antwortet, Reverse-DNS nennt einen
/// anderen Namen) bleiben außen vor: dort würde die Prüfung nur die falsche Maschine anpingen.
/// </summary>
public sealed class HeartbeatService(ComputerService computers, SettingsService settings, TimeProvider time)
{
    public const int DefaultIntervalMinutes = 5;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<HeartbeatSettings> GetSettingsAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(SettingKeys.HeartbeatEnabled, ct) == bool.TrueString,
        int.TryParse(await settings.GetAsync(SettingKeys.HeartbeatIntervalMinutes, ct), out var minutes) && minutes > 0 ? minutes : DefaultIntervalMinutes,
        DateTime.TryParse(await settings.GetAsync(SettingKeys.HeartbeatLastRunUtc, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ? last : null,
        await settings.GetAsync(SettingKeys.HeartbeatLastResult, ct));

    public async Task SaveSettingsAsync(bool enabled, int intervalMinutes, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.HeartbeatEnabled, enabled.ToString(), ct);
        await settings.SetAsync(SettingKeys.HeartbeatIntervalMinutes, Math.Clamp(intervalMinutes, 1, 24 * 60).ToString(), ct);
    }

    /// <summary>Rechner, die der Heartbeat prüft: alle ohne Namensauffälligkeit.</summary>
    public static bool Eligible(Computer computer) => !computer.NameMismatch && !computer.ReverseDnsMismatch;

    /// <summary>Prüft, wenn der Heartbeat aktiv und das Intervall abgelaufen ist; liefert true, wenn ein Lauf stattfand.</summary>
    public async Task<bool> RunIfDueAsync(CancellationToken ct = default)
    {
        var current = await GetSettingsAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (!current.Enabled || (current.LastRunUtc is { } last && now - last < TimeSpan.FromMinutes(current.IntervalMinutes)))
            return false;
        await RunAsync(ct);
        return true;
    }

    /// <summary>Prüft sofort alle geeigneten Rechner; läuft nie doppelt.</summary>
    public async Task<string> RunAsync(CancellationToken ct = default)
    {
        if (!await _lock.WaitAsync(0, ct))
            return "läuft bereits";
        try
        {
            var all = await computers.ListAsync(ct);
            var ids = all.Where(Eligible).Select(c => c.Id).ToList();
            var skipped = all.Count - ids.Count;
            var online = ids.Count == 0 ? 0 : await computers.ProbeAsync(ids, ct);
            var result = $"{ids.Count} Rechner geprüft, {online} online" + (skipped > 0 ? $", {skipped} wegen Namensauffälligkeit übersprungen" : "");
            await settings.SetAsync(SettingKeys.HeartbeatLastRunUtc, time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(SettingKeys.HeartbeatLastResult, result, ct);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
