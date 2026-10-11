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
            .Include(c => c.PendingUpdates).Include(c => c.UserProfiles).Include(c => c.MemoryModules).Include(c => c.PhysicalDisks).Include(c => c.Volumes).Include(c => c.Monitors).Include(c => c.Printers)
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

    /// <summary>Setzt die Bestandsangaben eines Rechners; leere Texte werden zu "nicht angegeben".</summary>
    public async Task UpdateAssetAsync(int id, AssetData asset, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computer = await db.Computers.FindAsync([id], ct) ?? throw new KeyNotFoundException(L["Computer {0} not found", id]);
        Apply(computer, asset);
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(Computer computer, AssetData asset)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        computer.Owner = Clean(asset.Owner);
        computer.Location = Clean(asset.Location);
        computer.CostCenter = Clean(asset.CostCenter);
        computer.InventoryNumber = Clean(asset.InventoryNumber);
        computer.PurchaseDate = asset.PurchaseDate;
        computer.WarrantyUntil = asset.WarrantyUntil;
        computer.Tags = AssetTags.Normalize(asset.Tags);
        computer.Notes = Clean(asset.Notes);
    }

    /// <summary>
    /// Übernimmt Bestandsangaben aus einer CSV-Tabelle (Semikolon oder Komma, erste Zeile = Spaltennamen wie im Export
    /// oder englisch). Zugeordnet wird über den Rechnernamen. Eine leere Zelle lässt den Wert unverändert, "-" löscht ihn;
    /// Spalten, die die Tabelle nicht enthält, bleiben unberührt.
    /// </summary>
    public async Task<AssetImportResult> ImportAssetsAsync(string csv, CancellationToken ct = default)
    {
        var rows = AssetCsv.Parse(csv);
        if (rows.Count < 2)
            throw new InvalidOperationException(L["The table needs a header row and at least one data row."]);
        var columns = rows[0].Select(h => AssetCsv.Column(h, L)).ToList();
        var nameIndex = columns.IndexOf("Name");
        if (nameIndex < 0)
            throw new InvalidOperationException(L["The table has no column \"Name\"."]);
        if (columns.Count(c => c is not null and not "Name") == 0)
            throw new InvalidOperationException(L["The table contains none of the asset columns."]);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.ToListAsync(ct);
        var byName = computers.GroupBy(c => c.Name.Split('.')[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var updated = new List<string>();
        var unknown = new List<string>();
        var errors = new List<string>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var name = nameIndex < row.Count ? row[nameIndex].Trim() : "";
            if (name.Length == 0)
                continue;
            if (!byName.TryGetValue(name.Split('.')[0], out var computer))
            {
                unknown.Add(name);
                continue;
            }
            var asset = computer.Asset;
            var description = computer.Description;
            string? problem = null;
            for (var c = 0; c < columns.Count && c < row.Count && problem is null; c++)
            {
                var cell = row[c].Trim();
                if (columns[c] is not { } column || column == "Name" || cell.Length == 0)
                    continue;
                var text = cell == "-" ? null : cell;
                switch (column)
                {
                    case "Owner": asset = asset with { Owner = text }; break;
                    case "Location": asset = asset with { Location = text }; break;
                    case "Cost center": asset = asset with { CostCenter = text }; break;
                    case "Inventory number": asset = asset with { InventoryNumber = text }; break;
                    case "Tags": asset = asset with { Tags = text }; break;
                    case "Notes": asset = asset with { Notes = text }; break;
                    case "Description": description = text; break;
                    case "Purchase date" or "Warranty until":
                        DateOnly? date = null;
                        if (text is not null)
                        {
                            if (AssetCsv.Date(text) is { } parsed)
                                date = parsed;
                            else
                                problem = L["{0}: \"{1}\" is not a date (expected e.g. 2027-03-31 or 31.03.2027).", name, cell];
                        }
                        asset = column == "Purchase date" ? asset with { PurchaseDate = date } : asset with { WarrantyUntil = date };
                        break;
                }
            }
            if (problem is not null)
            {
                errors.Add(problem);
                continue;
            }
            Apply(computer, asset);
            computer.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            updated.Add(computer.Name);
        }
        await db.SaveChangesAsync(ct);
        return new AssetImportResult(updated, unknown, errors);
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
    /// <param name="states">Je Rechner: im Verzeichnis vorhanden (und nicht ausgeschlossen), Konto deaktiviert, dazu das Konto selbst, soweit das Verzeichnis es geliefert hat.</param>
    public async Task SetDirectoryStateAsync(IReadOnlyDictionary<int, (bool Present, bool Disabled, DirectoryComputer? Account)> states, DateTime nowUtc, CancellationToken ct = default)
    {
        var ids = states.Keys.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var computer in await db.Computers.Where(c => ids.Contains(c.Id)).ToListAsync(ct))
        {
            var (present, disabled, account) = states[computer.Id];
            computer.DirectoryDisabled = disabled;
            // Ohne Antwort zum Konto (es fehlt) bleibt stehen, was zuletzt bekannt war.
            if (account is not null)
            {
                computer.DirectoryPath = DirectoryNames.Parent(account.DistinguishedName);
                computer.DirectoryGroups = account.Groups is { Count: > 0 } groups ? string.Join('\n', groups) : null;
                computer.DirectoryDescription = account.Description;
                computer.DirectoryLastLogonUtc = account.LastLogonUtc;
            }
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
