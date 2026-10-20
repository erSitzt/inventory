using System.Globalization;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Was mit Rechnern geschieht, deren Computerkonto im Verzeichnis nicht mehr gefunden wird.</summary>
public enum AdMissingAction
{
    /// <summary>Behalten und als „nicht im AD“ kennzeichnen.</summary>
    Keep,
    /// <summary>Kennzeichnen und nach der eingestellten Frist samt Inventardaten löschen.</summary>
    Delete,
}

/// <param name="ExcludeDisabled">Deaktivierte Computerkonten nicht anlegen und wie fehlende behandeln.</param>
/// <param name="MissingDays">Frist in Tagen, nach der fehlende Rechner gelöscht werden (nur bei <see cref="AdMissingAction.Delete"/>).</param>
/// <param name="Exclusions">Ausschlussregeln (<see cref="DirectoryExclusions"/>); ausgeschlossene Konten werden wie deaktivierte behandelt.</param>
/// <param name="InactiveDays">Ohne Lebenszeichen seit so vielen Tagen gilt ein Rechner als vermutlich außer Betrieb; 0 = aus.</param>
public sealed record AdSyncSettings(bool Enabled, string? Server, string? BaseDns, int? CredentialId, int IntervalHours, DateTime? LastRunUtc, string? LastResult,
    bool ExcludeDisabled = true, AdMissingAction MissingAction = AdMissingAction.Keep, int MissingDays = 30, string? Exclusions = null, int InactiveDays = AdSyncService.DefaultInactiveDays);

/// <param name="Total">Gefundene Computerkonten, deaktivierte eingeschlossen.</param>
/// <param name="Disabled">Davon deaktiviert.</param>
/// <param name="MissingNames">Rechner, die als „nicht im AD“ gekennzeichnet sind oder es mit diesem Lauf werden.</param>
/// <param name="DeleteNames">Rechner, deren Frist abgelaufen ist und die dieser Lauf löscht.</param>
/// <param name="DeleteBlocked">true, wenn auffällig viele Rechner auf einmal zu löschen wären; dann löscht der Lauf nichts.</param>
/// <param name="Excluded">Konten, die eine Ausschlussregel trifft.</param>
/// <param name="Inactive">Rechner, die nach diesem Lauf als vermutlich außer Betrieb gelten.</param>
public sealed record AdSyncPreview(int Total, List<string> NewNames, int Disabled = 0, List<string>? MissingNames = null, List<string>? DeleteNames = null, bool DeleteBlocked = false,
    int Excluded = 0, int Inactive = 0);

/// <summary>
/// Gleicht die Rechnerliste mit dem Active Directory ab: legt fehlende Rechner an, kennzeichnet Rechner, deren Konto
/// verschwunden (oder deaktiviert) ist, und löscht sie auf Wunsch nach einer Frist. Von Hand angelegte Rechner, die
/// das Verzeichnis nie gekannt hat, bleiben unberührt.
/// </summary>
public sealed class AdSyncService(IDirectorySource directory, SettingsService settings, CredentialService credentials, ComputerService computers, TimeProvider time,
    AuditService? audit = null, IStringLocalizer<SharedResource>? localizer = null, IAlertSink? alerts = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    /// <summary>Mehr als so viele Löschungen in einem Lauf gelten als Hinweis auf eine falsche Suchwurzel, wenn sie zugleich einen großen Teil ausmachen.</summary>
    private const int MassDeleteCount = 5;
    private const double MassDeleteShare = 0.25;

    /// <summary>Übliche Frist für veraltete Computerkonten.</summary>
    public const int DefaultInactiveDays = 90;
    /// <summary>Das Verzeichnis gleicht die letzte Anmeldung nur etwa alle zwei Wochen ab; kürzere Fristen gäben Fehlalarme.</summary>
    public const int MinInactiveDays = 30;

    public async Task<AdSyncSettings> GetSettingsAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(SettingKeys.AdSyncEnabled, ct) == bool.TrueString,
        await settings.GetAsync(SettingKeys.AdSyncServer, ct),
        await settings.GetAsync(SettingKeys.AdSyncBaseDns, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.AdSyncCredentialId, ct), out var credentialId) ? credentialId : null,
        int.TryParse(await settings.GetAsync(SettingKeys.AdSyncIntervalHours, ct), out var hours) && hours > 0 ? hours : 24,
        DateTime.TryParse(await settings.GetAsync(SettingKeys.AdSyncLastRunUtc, ct), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) ? last : null,
        await settings.GetAsync(SettingKeys.AdSyncLastResult, ct),
        await settings.GetAsync(SettingKeys.AdSyncExcludeDisabled, ct) != bool.FalseString,
        Enum.TryParse<AdMissingAction>(await settings.GetAsync(SettingKeys.AdSyncMissingAction, ct), out var action) ? action : AdMissingAction.Keep,
        int.TryParse(await settings.GetAsync(SettingKeys.AdSyncMissingDays, ct), out var days) && days > 0 ? days : 30,
        await settings.GetAsync(SettingKeys.AdSyncExclusions, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.AdSyncInactiveDays, ct), out var inactive) && inactive >= 0 ? inactive : DefaultInactiveDays);

    public async Task SaveSettingsAsync(bool enabled, string? server, string? baseDns, int? credentialId, int intervalHours,
        bool excludeDisabled = true, AdMissingAction missingAction = AdMissingAction.Keep, int missingDays = 30, string? exclusions = null,
        int inactiveDays = DefaultInactiveDays, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.AdSyncEnabled, enabled.ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncServer, string.IsNullOrWhiteSpace(server) ? null : server.Trim(), ct);
        await settings.SetAsync(SettingKeys.AdSyncBaseDns, string.IsNullOrWhiteSpace(baseDns) ? null : baseDns.Trim(), ct);
        await settings.SetAsync(SettingKeys.AdSyncCredentialId, credentialId?.ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncIntervalHours, Math.Clamp(intervalHours, 1, 24 * 30).ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncExcludeDisabled, excludeDisabled.ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncMissingAction, missingAction.ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncMissingDays, Math.Clamp(missingDays, 1, 3650).ToString(), ct);
        await settings.SetAsync(SettingKeys.AdSyncExclusions, string.IsNullOrWhiteSpace(exclusions) ? null : exclusions.Trim(), ct);
        inactiveDays = inactiveDays <= 0 ? 0 : Math.Clamp(inactiveDays, MinInactiveDays, 3650);
        await settings.SetAsync(SettingKeys.AdSyncInactiveDays, inactiveDays.ToString(CultureInfo.InvariantCulture), ct);
        // Eine geänderte Frist gilt sofort, nicht erst nach dem nächsten Lauf.
        await computers.MarkInactiveAsync(inactiveDays, time.GetUtcNow().UtcDateTime, ct);
    }

    /// <summary>Fragt das Verzeichnis ab, ohne etwas zu ändern.</summary>
    /// <exception cref="RemoteException">Das Verzeichnis ist nicht erreichbar.</exception>
    public async Task<AdSyncPreview> PreviewAsync(CancellationToken ct = default) => (await PlanAsync(ct)).Preview;

    /// <summary>Synchronisiert sofort; das Ergebnis wird auch in den Einstellungen festgehalten.</summary>
    public async Task<string> SyncAsync(CancellationToken ct = default)
    {
        string result;
        try
        {
            var plan = await PlanAsync(ct);
            var preview = plan.Preview;
            var skipped = preview.NewNames.Where(n => !ComputerNames.IsValid(n)).ToList();
            var added = await computers.ImportAsync(preview.NewNames.Except(skipped).ToList(), ct);
            foreach (var computer in added)
                plan.States[computer.Id] = (true, false, plan.Found.GetValueOrDefault(ShortName(computer.Name)));
            await computers.SetDirectoryStateAsync(plan.States, plan.NowUtc, ct);
            var current = await GetSettingsAsync(ct);
            var inactive = await computers.MarkInactiveAsync(current.InactiveDays, plan.NowUtc, ct);

            result = L["{0} computer accounts found, {1} newly created", preview.Total, added.Count].Value;
            if (skipped.Count > 0)
                result += ", " + L["{0} not created (unsupported name: {1})", skipped.Count, string.Join(", ", skipped)];
            if (preview.Disabled > 0)
                result += ", " + L["{0} disabled", preview.Disabled];
            if (preview.Excluded > 0)
                result += ", " + L["{0} excluded", preview.Excluded];
            if (preview.MissingNames!.Count > 0)
                result += ", " + L["{0} not in AD", preview.MissingNames.Count];
            if (inactive > 0)
                result += ", " + L["{0} presumably decommissioned (no sign of life for {1} days)", inactive, current.InactiveDays];
            if (preview.DeleteBlocked)
            {
                result += " – " + L["{0} computers would be deleted; that is conspicuously many at once, so nothing was deleted (check the search roots or delete manually)", plan.DeleteIds.Count];
            }
            else if (plan.DeleteIds.Count > 0)
            {
                await computers.DeleteAsync(plan.DeleteIds, ct);
                result += ", " + L["{0} deleted", plan.DeleteIds.Count];
                if (audit is not null)
                    await audit.LogAsync(AuditActor.System(L["(automatic)"]), L["Computers deleted (no longer in AD)"], string.Join(", ", preview.DeleteNames!), ct);
            }
        }
        catch (RemoteException ex)
        {
            result = L["Error"] + $": {ex.Message}";
            alerts?.Raise(new(AlertKind.AdSyncFailed, L["Active Directory synchronization failed"], ex.Message, "settings"));
        }
        await settings.SetAsync(SettingKeys.AdSyncLastRunUtc, time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.AdSyncLastResult, result, ct);
        return result;
    }

    /// <summary>Synchronisiert, wenn die Synchronisation aktiv und das Intervall abgelaufen ist.</summary>
    public async Task<bool> SyncIfDueAsync(CancellationToken ct = default)
    {
        var current = await GetSettingsAsync(ct);
        if (!current.Enabled || (current.LastRunUtc is { } last && time.GetUtcNow().UtcDateTime - last < TimeSpan.FromHours(current.IntervalHours)))
            return false;
        await SyncAsync(ct);
        return true;
    }

    private sealed record Plan(AdSyncPreview Preview, Dictionary<int, (bool Present, bool Disabled, DirectoryComputer? Account)> States, List<int> DeleteIds, DateTime NowUtc,
        Dictionary<string, DirectoryComputer> Found);

    private async Task<Plan> PlanAsync(CancellationToken ct)
    {
        var current = await GetSettingsAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var found = (await QueryAsync(current, ct)).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var disabled = found.Values.Count(c => !c.Enabled);
        var exclusions = DirectoryExclusions.Parse(current.Exclusions);
        var excluded = found.Values.Count(c => exclusions.Excludes(c.Name, c.DistinguishedName));
        // Ausgeschlossene Konten zählen wie deaktivierte: nicht anlegen, vorhandene Rechner gelten als „nicht im AD“.
        bool Counts(DirectoryComputer c) => (c.Enabled || !current.ExcludeDisabled) && !exclusions.Excludes(c.Name, c.DistinguishedName);

        var existing = await computers.ListAsync(ct);
        var known = existing.Select(c => ShortName(c.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newNames = found.Values.Where(c => Counts(c) && !known.Contains(c.Name)).Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var states = new Dictionary<int, (bool Present, bool Disabled, DirectoryComputer? Account)>();
        var missing = new List<Computer>();
        var delete = new List<Computer>();
        // Ein leeres Ergebnis ist fast immer eine falsche Suchwurzel oder fehlende Leserechte, kein leeres Verzeichnis.
        if (found.Count > 0)
        {
            foreach (var computer in existing)
            {
                var account = found.GetValueOrDefault(ShortName(computer.Name));
                if (account is not null && Counts(account))
                {
                    states[computer.Id] = (true, !account.Enabled, account);
                }
                else if (account is not null || computer.DirectorySeenUtc is not null)
                {
                    // Nur Rechner, die das Verzeichnis kennt oder kannte; von Hand angelegte bleiben unberührt.
                    states[computer.Id] = (false, account is { Enabled: false }, account);
                    missing.Add(computer);
                    if (current.MissingAction == AdMissingAction.Delete && computer.DirectoryMissingSinceUtc is { } since && now - since >= TimeSpan.FromDays(current.MissingDays))
                        delete.Add(computer);
                }
            }
        }

        var managed = existing.Count(c => c.DirectorySeenUtc is not null || found.ContainsKey(ShortName(c.Name)));
        var blocked = delete.Count > MassDeleteCount && delete.Count > managed * MassDeleteShare;
        foreach (var computer in blocked ? [] : delete)
            states.Remove(computer.Id);

        // Mit den Anmeldewerten, die dieser Lauf übernimmt; gelöschte Rechner zählen nicht mehr.
        var deleting = blocked ? [] : delete.Select(c => c.Id).ToHashSet();
        var inactive = existing.Where(c => !deleting.Contains(c.Id)).Count(c =>
        {
            var logon = states.TryGetValue(c.Id, out var state) && state.Account is { } account ? account.LastLogonUtc : c.DirectoryLastLogonUtc;
            return Computer.IsInactive(new Computer { Name = c.Name, DirectoryLastLogonUtc = logon, LastSeenUtc = c.LastSeenUtc, AgentLastSeenUtc = c.AgentLastSeenUtc }, current.InactiveDays, now);
        }) + newNames.Count(n => Computer.IsInactive(new Computer { Name = n, DirectoryLastLogonUtc = found[n].LastLogonUtc }, current.InactiveDays, now));
        var preview = new AdSyncPreview(found.Count, newNames, disabled, missing.Select(c => c.Name).ToList(), delete.Select(c => c.Name).ToList(), blocked, excluded, inactive);
        return new Plan(preview, states, delete.Select(c => c.Id).ToList(), now, found);
    }

    private static string ShortName(string name) => name.Split('.')[0];

    private async Task<List<DirectoryComputer>> QueryAsync(AdSyncSettings current, CancellationToken ct)
    {
        var baseDns = (current.BaseDns ?? "").Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Ohne ausdrücklich gewählte Zugangsdaten fragt der Server mit seinem eigenen (Dienst-)Konto.
        var credential = current.CredentialId is null ? null : await credentials.GetAsync(current.CredentialId, ct);
        return await directory.GetComputersAsync(new DirectoryQuery(current.Server, baseDns, credential), ct);
    }
}
