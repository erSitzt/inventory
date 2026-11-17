using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="Conflicts">Eine überstimmte GPO setzt bei diesem Rechner einen Wert anders.</param>
public sealed record UpdatePolicyMember(int Id, string Name, bool Conflicts);

/// <summary>Rechner mit derselben Update-Richtlinie: gleiche Werte aus denselben GPOs bzw. aus Intune.</summary>
/// <param name="Settings">Die Werte eines der Rechner, stellvertretend für alle.</param>
public sealed record UpdatePolicyGroup(UpdatePolicySummary Summary, IReadOnlyList<ComputerUpdatePolicySetting> Settings, IReadOnlyList<UpdatePolicyMember> Computers)
{
    /// <summary>Keine Richtlinie: Windows-Standard.</summary>
    public bool NotConfigured => Settings.Count == 0;
}

/// <param name="NotCaptured">Windows-Rechner, deren Richtlinie noch kein Scan geliefert hat (älterer Runner oder nie gescannt).</param>
public sealed record UpdatePolicyOverview(IReadOnlyList<UpdatePolicyGroup> Policies, IReadOnlyList<UpdatePolicyMember> NotCaptured)
{
    public int ComputersWithConflicts => Policies.Sum(p => p.Computers.Count(c => c.Conflicts));
}

/// <summary>Welche Rechner welche Windows-Update-Richtlinie bekommen, zum Vergleich über alle Rechner.</summary>
public sealed class UpdatePolicyService(IDbContextFactory<InventoryDbContext> dbFactory, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    public async Task<UpdatePolicyOverview> OverviewAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Macs haben keine Windows-Update-Richtlinie; sie zählen auch nicht als "noch nicht erfasst".
        var computers = await db.Computers.AsNoTracking().Where(c => c.Platform != OsPlatform.MacOS)
            .Select(c => new { c.Id, c.Name, c.UpdatePolicyScanUtc, Settings = c.UpdatePolicySettings.ToList() })
            .ToListAsync(ct);

        var policies = computers.Where(c => c.UpdatePolicyScanUtc is not null)
            .GroupBy(c => UpdatePolicies.Fingerprint(c.Settings), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var settings = g.First().Settings;
                var members = g.Select(c => new UpdatePolicyMember(c.Id, c.Name, UpdatePolicies.Summarize(c.Settings, L).Conflicts))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return new UpdatePolicyGroup(UpdatePolicies.Summarize(settings, L), settings, members);
            })
            // Die größten Gruppen zuerst, der Windows-Standard zuletzt.
            .OrderBy(p => p.NotConfigured).ThenByDescending(p => p.Computers.Count).ThenBy(p => string.Join(", ", p.Summary.Gpos), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var notCaptured = computers.Where(c => c.UpdatePolicyScanUtc is null).Select(c => new UpdatePolicyMember(c.Id, c.Name, false))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new UpdatePolicyOverview(policies, notCaptured);
    }
}
