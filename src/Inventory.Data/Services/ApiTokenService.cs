using System.Security.Cryptography;
using System.Text;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class ApiTokenService(IDbContextFactory<InventoryDbContext> dbFactory, TimeProvider time)
{
    public const string TokenPrefix = "inv_";
    /// <summary>So oft wird "zuletzt verwendet" höchstens gespeichert.</summary>
    private static readonly TimeSpan UsedInterval = TimeSpan.FromMinutes(1);

    public async Task<List<ApiToken>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>Legt ein Token an und liefert den Klartext. Er lässt sich danach nicht mehr abrufen.</summary>
    public async Task<string> CreateAsync(string name, string createdBy, DateTime? expiresUtc, CancellationToken ct = default)
    {
        var token = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiTokens.Add(new ApiToken
        {
            Name = name.Trim(), TokenHash = Hash(token), Prefix = token[..(TokenPrefix.Length + 6)], CreatedBy = createdBy, ExpiresUtc = expiresUtc,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ApiTokens.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Liefert das Token, wenn es existiert und nicht abgelaufen ist.</summary>
    public async Task<ApiToken?> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;
        var hash = Hash(token);
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || stored.ExpiresUtc < now)
            return null;
        if (stored.LastUsedUtc is null || now - stored.LastUsedUtc > UsedInterval)
        {
            stored.LastUsedUtc = now;
            await db.SaveChangesAsync(ct);
        }
        return stored;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
