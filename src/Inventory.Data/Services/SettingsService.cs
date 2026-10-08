using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class SettingsService(IDbContextFactory<InventoryDbContext> dbFactory)
{
    public async Task<TransportKind> GetDefaultTransportAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var value = await db.Settings.Where(s => s.Key == SettingKeys.DefaultTransport).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return Enum.TryParse<TransportKind>(value, out var kind) ? kind : TransportKind.SmbService;
    }

    public async Task SetDefaultTransportAsync(TransportKind kind, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.FindAsync([SettingKeys.DefaultTransport], ct);
        if (setting is null)
            db.Settings.Add(new Setting { Key = SettingKeys.DefaultTransport, Value = kind.ToString() });
        else
            setting.Value = kind.ToString();
        await db.SaveChangesAsync(ct);
    }
}
