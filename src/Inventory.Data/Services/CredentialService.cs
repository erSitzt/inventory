using System.Net;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed class CredentialService(IDbContextFactory<InventoryDbContext> dbFactory, ISecretProtector protector, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


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
                throw new ArgumentException(L["A password is required for new credentials."], nameof(password));
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
            credential = await db.Credentials.FindAsync([id.Value], ct) ?? throw new KeyNotFoundException(L["Credentials {0} not found", id]);
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

    /// <summary>Bestimmte Zugangsdaten im Klartext, sonst der Standard; null, wenn nichts hinterlegt ist.</summary>
    public async Task<NetworkCredential?> GetAsync(int? credentialId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var credential = credentialId is null
            ? await db.Credentials.FirstOrDefaultAsync(c => c.IsDefault, ct)
            : await db.Credentials.FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        return credential is null ? null : new NetworkCredential(credential.Username, protector.Unprotect(credential.ProtectedPassword));
    }
}
