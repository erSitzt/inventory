using System.Net;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class CredentialService(IDbContextFactory<InventoryDbContext> dbFactory, ISecretProtector protector)
{
    public async Task<List<Credential>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Credentials.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    /// <summary>Legt Zugangsdaten an (id = null) oder ändert sie. Ein leeres Passwort lässt das gespeicherte unverändert.</summary>
    public async Task<Credential> SaveAsync(int? id, string name, string username, string? password, string? description, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Credential credential;
        if (id is null)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Für neue Zugangsdaten ist ein Passwort erforderlich.", nameof(password));
            credential = new Credential
            {
                Name = name.Trim(),
                Username = username.Trim(),
                ProtectedPassword = protector.Protect(password),
                // Die ersten Zugangsdaten werden automatisch Standard.
                IsDefault = !await db.Credentials.AnyAsync(ct),
            };
            db.Credentials.Add(credential);
        }
        else
        {
            credential = await db.Credentials.FindAsync([id.Value], ct) ?? throw new KeyNotFoundException($"Zugangsdaten {id} nicht gefunden");
            credential.Name = name.Trim();
            credential.Username = username.Trim();
            if (!string.IsNullOrEmpty(password))
                credential.ProtectedPassword = protector.Protect(password);
        }
        credential.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync(ct);
        return credential;
    }

    public async Task SetDefaultAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var credential in await db.Credentials.ToListAsync(ct))
            credential.IsDefault = credential.Id == id;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Credentials.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Liefert die für einen Rechner gültigen Zugangsdaten im Klartext: die des Rechners, sonst den Standard.</summary>
    public async Task<NetworkCredential?> ResolveAsync(int computerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var credential = await db.Computers.Where(c => c.Id == computerId).Select(c => c.Credential).FirstOrDefaultAsync(ct)
                         ?? await db.Credentials.FirstOrDefaultAsync(c => c.IsDefault, ct);
        return credential is null ? null : new NetworkCredential(credential.Username, protector.Unprotect(credential.ProtectedPassword));
    }
}
