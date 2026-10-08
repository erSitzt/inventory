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
    public async Task<bool> GetVerifyTargetNameAsync(CancellationToken ct = default) =>
        await GetAsync(SettingKeys.SkipTargetNameCheck, ct) != bool.TrueString;

    public Task SetVerifyTargetNameAsync(bool enabled, CancellationToken ct = default) =>
        SetAsync(SettingKeys.SkipTargetNameCheck, (!enabled).ToString(), ct);

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
