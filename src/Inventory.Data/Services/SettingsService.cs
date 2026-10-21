using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

/// <summary>Name und Logo der Organisation für den Kopf der PDF-Berichte.</summary>
public sealed record ReportBranding(string? Organization, ReportLogo? Logo);

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

    /// <summary>Wann Jobs ohne Agenten laufen dürfen. Standard: immer.</summary>
    public async Task<AgentlessPolicy> GetAgentlessPolicyAsync(CancellationToken ct = default) =>
        Enum.TryParse<AgentlessPolicy>(await GetAsync(SettingKeys.AgentlessPolicy, ct), out var policy) && Enum.IsDefined(policy) ? policy : AgentlessPolicy.Always;

    public Task SetAgentlessPolicyAsync(AgentlessPolicy policy, CancellationToken ct = default) =>
        SetAsync(SettingKeys.AgentlessPolicy, policy.ToString(), ct);

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

    public const int DefaultDeploymentRetentionDays = 730;
    /// <summary>Kürzer wäre riskant: Zeitpläne und die Übersicht werten die letzten Wochen aus.</summary>
    public const int MinDeploymentRetentionDays = 30;

    /// <summary>Aufbewahrungsfrist abgeschlossener Deployments in Tagen; 0 = unbegrenzt.</summary>
    public async Task<int> GetDeploymentRetentionDaysAsync(CancellationToken ct = default) =>
        int.TryParse(await GetAsync(SettingKeys.DeploymentRetentionDays, ct), out var days) && days >= 0
            ? days == 0 ? 0 : Math.Clamp(days, MinDeploymentRetentionDays, MaxAuditRetentionDays) : DefaultDeploymentRetentionDays;

    public Task SetDeploymentRetentionDaysAsync(int days, CancellationToken ct = default) =>
        SetAsync(SettingKeys.DeploymentRetentionDays, (days <= 0 ? 0 : Math.Clamp(days, MinDeploymentRetentionDays, MaxAuditRetentionDays)).ToString(), ct);

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

    public const int MaxReportOrganizationLength = 120;

    /// <summary>Name und Logo für den Kopf der PDF-Berichte; ein unlesbar gewordenes Logo fällt still weg.</summary>
    public async Task<ReportBranding> GetReportBrandingAsync(CancellationToken ct = default)
    {
        var organization = await GetAsync(SettingKeys.ReportOrganization, ct);
        var stored = await GetAsync(SettingKeys.ReportLogo, ct);
        // Jeder Bericht braucht das Logo: einmal lesen, solange es sich nicht ändert.
        var cached = _logo;
        if (cached?.Stored != stored)
        {
            ReportLogo? logo = null;
            if (!string.IsNullOrEmpty(stored))
            {
                try
                {
                    logo = ReportImage.Parse(Convert.FromBase64String(stored));
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException)
                {
                }
            }
            _logo = cached = new CachedLogo(stored, logo);
        }
        return new ReportBranding(string.IsNullOrWhiteSpace(organization) ? null : organization, cached.Logo);
    }

    private sealed record CachedLogo(string? Stored, ReportLogo? Logo);
    private CachedLogo? _logo;

    public async Task<byte[]?> GetReportLogoAsync(CancellationToken ct = default)
    {
        var value = await GetAsync(SettingKeys.ReportLogo, ct);
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public Task SetReportOrganizationAsync(string? name, CancellationToken ct = default)
    {
        var value = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return SetAsync(SettingKeys.ReportOrganization, value is { Length: > MaxReportOrganizationLength } ? value[..MaxReportOrganizationLength] : value, ct);
    }

    /// <param name="data">PNG oder JPEG; null entfernt das Logo.</param>
    /// <exception cref="InvalidDataException">Zu groß oder kein lesbares PNG/JPEG; gespeichert wird dann nichts.</exception>
    public Task SetReportLogoAsync(byte[]? data, CancellationToken ct = default)
    {
        if (data is not null)
            ReportImage.Parse(data);
        return SetAsync(SettingKeys.ReportLogo, data is null ? null : Convert.ToBase64String(data), ct);
    }

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
