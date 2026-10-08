using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data.Services;

public sealed class ComputerService(IDbContextFactory<InventoryDbContext> dbFactory, IHostProbe probe)
{
    private const int MaxParallelProbes = 32;

    public async Task<List<Computer>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Computers.AsNoTracking().Include(c => c.Credential).OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<Computer?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Computers.AsNoTracking().Include(c => c.Credential).FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>Legt alle noch unbekannten Rechner aus dem Text an und liefert die neu angelegten.</summary>
    public async Task<List<Computer>> ImportAsync(string? text, CancellationToken ct = default)
    {
        var names = ComputerNames.Parse(text);
        if (names.Count == 0)
            return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = (await db.Computers.Select(c => c.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = names.Where(n => !existing.Contains(n)).Select(n => new Computer { Name = n }).ToList();
        db.Computers.AddRange(added);
        await db.SaveChangesAsync(ct);
        return added;
    }

    public async Task UpdateAsync(int id, string? description, TransportKind? transport, int? credentialId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computer = await db.Computers.FindAsync([id], ct) ?? throw new KeyNotFoundException($"Rechner {id} nicht gefunden");
        computer.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        computer.Transport = transport;
        computer.CredentialId = credentialId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Computers.Where(c => idList.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Prüft die Erreichbarkeit der angegebenen Rechner (null = alle) und speichert das Ergebnis.</summary>
    public async Task<int> ProbeAsync(IEnumerable<int>? ids = null, CancellationToken ct = default)
    {
        var idList = ids?.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.Where(c => idList == null || idList.Contains(c.Id)).ToListAsync(ct);

        await Parallel.ForEachAsync(computers, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelProbes, CancellationToken = ct },
            async (computer, token) =>
            {
                var result = await probe.ProbeAsync(computer.Name, token);
                var now = DateTime.UtcNow;
                computer.IsOnline = result.Online;
                computer.LastProbeUtc = now;
                if (result.IpAddress is not null)
                    computer.IpAddress = result.IpAddress;
                if (result.Online)
                    computer.LastSeenUtc = now;
            });

        await db.SaveChangesAsync(ct);
        return computers.Count(c => c.IsOnline);
    }
}
