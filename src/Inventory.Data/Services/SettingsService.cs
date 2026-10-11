using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class SettingsService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    public async Task<TransportKind> GetDefaultTransportAsync(CancellationToken ct = default) =>
        Enum.TryParse<TransportKind>(await GetAsync(SettingKeys.DefaultTransport, ct), out var kind) ? kind : TransportKind.SmbService;

    public Task SetDefaultTransportAsync(TransportKind kind, CancellationToken ct = default) =>
        SetAsync(SettingKeys.DefaultTransport, kind.ToString(), ct);

    /// <summary>Bei Zustellfehlern den jeweils anderen agentenlosen Transport versuchen.</summary>
    public async Task<bool> GetTransportFallbackAsync(CancellationToken ct = default) =>
        await GetAsync(SettingKeys.TransportFallback, ct) == bool.TrueString;

    public Task SetTransportFallbackAsync(bool enabled, CancellationToken ct = default) =>
        SetAsync(SettingKeys.TransportFallback, enabled.ToString(), ct);

    /// <summary>Der Runner bricht ab, wenn er auf einem anders heißenden Rechner landet (Schutz vor veralteten DNS-Einträgen). Standard: an.</summary>
    public async Task<bool> GetVerifyTargetNameAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await VerifyTargetNameAsync(db, ct);
    }

    /// <summary>Für Dienste, die bereits einen Kontext offen haben.</summary>
    internal static async Task<bool> VerifyTargetNameAsync(InventoryDbContext db, CancellationToken ct) =>
        await db.Settings.Where(s => s.Key == SettingKeys.SkipTargetNameCheck).Select(s => s.Value).FirstOrDefaultAsync(ct) != bool.TrueString;

    public Task SetVerifyTargetNameAsync(bool enabled, CancellationToken ct = default) =>
        SetAsync(SettingKeys.SkipTargetNameCheck, (!enabled).ToString(), ct);

    /// <summary>Automatisch übernommene Bibliotheks-Updates behalten die Freigabe des Pakets. Standard: aus.</summary>
    public async Task<bool> GetAutoApproveLibraryUpdatesAsync(CancellationToken ct = default) =>
        await GetAsync(SettingKeys.AutoApproveLibraryUpdates, ct) == bool.TrueString;

    public Task SetAutoApproveLibraryUpdatesAsync(bool enabled, CancellationToken ct = default) =>
        SetAsync(SettingKeys.AutoApproveLibraryUpdates, enabled.ToString(), ct);

    public const int DefaultMaxParallelTargets = 16;

    /// <summary>Obergrenze gleichzeitig laufender Deployment-Ziele über alle Deployments hinweg.</summary>
    public async Task<int> GetMaxParallelTargetsAsync(CancellationToken ct = default) =>
        int.TryParse(await GetAsync(SettingKeys.MaxParallelTargets, ct), out var value) && value > 0 ? Math.Min(value, 128) : DefaultMaxParallelTargets;

    public Task SetMaxParallelTargetsAsync(int value, CancellationToken ct = default) =>
        SetAsync(SettingKeys.MaxParallelTargets, Math.Clamp(value, 1, 128).ToString(), ct);

    public const int DefaultAuditRetentionDays = 730;
    public const int MaxAuditRetentionDays = 3650;

    /// <summary>Aufbewahrungsfrist des Protokolls in Tagen; 0 = unbegrenzt.</summary>
    public async Task<int> GetAuditRetentionDaysAsync(CancellationToken ct = default) =>
        int.TryParse(await GetAsync(SettingKeys.AuditRetentionDays, ct), out var days) && days >= 0 ? Math.Min(days, MaxAuditRetentionDays) : DefaultAuditRetentionDays;

    public Task SetAuditRetentionDaysAsync(int days, CancellationToken ct = default) =>
        SetAsync(SettingKeys.AuditRetentionDays, Math.Clamp(days, 0, MaxAuditRetentionDays).ToString(), ct);

    public const int DefaultRebootWaitMinutes = 30;

    /// <summary>Wartefrist nach einem Neustart-Schritt; danach gilt das Ziel als fehlgeschlagen.</summary>
    public async Task<int> GetRebootWaitMinutesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await RebootWaitMinutesAsync(db, ct);
    }

    internal static async Task<int> RebootWaitMinutesAsync(InventoryDbContext db, CancellationToken ct) =>
        int.TryParse(await db.Settings.Where(s => s.Key == SettingKeys.RebootWaitMinutes).Select(s => s.Value).FirstOrDefaultAsync(ct), out var minutes) && minutes > 0
            ? minutes : DefaultRebootWaitMinutes;

    public Task SetRebootWaitMinutesAsync(int minutes, CancellationToken ct = default) =>
        SetAsync(SettingKeys.RebootWaitMinutes, Math.Clamp(minutes, 5, 24 * 60).ToString(), ct);

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.FindAsync([key], ct);
        if (setting is null)
            db.Settings.Add(new Setting { Key = key, Value = value });
        else
            setting.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
