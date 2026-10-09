using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

public sealed class ComputerService(IDbContextFactory<InventoryDbContext> dbFactory, IHostProbe probe, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


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

    /// <summary>Rechner mit allen Inventardaten des letzten Scans.</summary>
    public async Task<Computer?> GetDetailAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Computers.AsNoTracking().AsSplitQuery()
            .Include(c => c.Credential).Include(c => c.Software).Include(c => c.Hotfixes).Include(c => c.Disks)
            .Include(c => c.NetworkAdapters).Include(c => c.Services).Include(c => c.Collections)
            .Include(c => c.LocalUsers).Include(c => c.LocalAdmins)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
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
        var computer = await db.Computers.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Computer {0} not found", id]);
        computer.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        computer.Transport = transport;
        computer.CredentialId = credentialId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Benennt den Eintrag auf den Namen um, den der Rechner selbst gemeldet hat.</summary>
    /// <exception cref="InvalidOperationException">Kein gemeldeter Name vorhanden, oder es gibt bereits einen Eintrag mit diesem Namen.</exception>
    public async Task<string> RenameToReportedAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computer = await db.Computers.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Computer {0} not found", id]);
        var name = computer.ReportedName ?? throw new InvalidOperationException(L["The computer has not reported a name yet."]);
        var names = await db.Computers.Where(c => c.Id != id).Select(c => c.Name).ToListAsync(ct);
        if (names.Any(n => n.Split('.')[0].Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L["{0} is already listed as a computer of its own. This entry is a duplicate and can be deleted.", name]);
        computer.Name = name;
        await db.SaveChangesAsync(ct);
        return name;
    }

    /// <summary>Hält fest, was die Verzeichnis-Synchronisation über die Rechner herausgefunden hat.</summary>
    /// <param name="states">Je Rechner: im Verzeichnis vorhanden (und nicht ausgeschlossen), Konto deaktiviert.</param>
    public async Task SetDirectoryStateAsync(IReadOnlyDictionary<int, (bool Present, bool Disabled)> states, DateTime nowUtc, CancellationToken ct = default)
    {
        var ids = states.Keys.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var computer in await db.Computers.Where(c => ids.Contains(c.Id)).ToListAsync(ct))
        {
            var (present, disabled) = states[computer.Id];
            computer.DirectoryDisabled = disabled;
            if (present)
            {
                computer.DirectorySeenUtc = nowUtc;
                computer.DirectoryMissingSinceUtc = null;
            }
            else
            {
                computer.DirectoryMissingSinceUtc ??= nowUtc;
            }
        }
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
        var verifyName = await SettingsService.VerifyTargetNameAsync(db, ct);

        await Parallel.ForEachAsync(computers, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelProbes, CancellationToken = ct },
            async (computer, token) =>
            {
                var result = await probe.ProbeAsync(computer.Name, token);
                var now = DateTime.UtcNow;
                // Antwortet unter der Adresse ein anderer Rechner, ist der eingetragene nicht erreichbar – nur seine alte Adresse.
                var online = result.Online && !(verifyName && MachineNames.Differs(computer.Name, result.MachineName));
                computer.IsOnline = online;
                computer.LastProbeUtc = now;
                if (result.IpAddress is not null)
                    computer.IpAddress = result.IpAddress;
                if (result.MachineName is not null)
                    computer.ReportedName = result.MachineName;
                if (result.Online)
                    computer.ReverseDnsName = result.ReverseName;
                if (online)
                    computer.LastSeenUtc = now;
            });

        await db.SaveChangesAsync(ct);
        return computers.Count(c => c.IsOnline);
    }
}
