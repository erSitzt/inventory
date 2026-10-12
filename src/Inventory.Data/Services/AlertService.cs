using System.Globalization;
using System.Text.Json;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Verwaltet die Alarmregeln und wertet sie aus: gemeldet wird nur, was sich seit dem letzten Durchlauf geändert hat.</summary>
public sealed class AlertService(IDbContextFactory<InventoryDbContext> dbFactory, CollectionService collections, NotificationService notifications,
    AlertQueue queue, SettingsService settings, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null, LicenseService? licenses = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    /// <summary>Regeln, die den Datenbestand ablesen, laufen in diesem Abstand; gemeldete Ereignisse gehen bei jedem Durchlauf hinaus.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    /// <summary>So viele Namen stehen höchstens in einer Nachricht.</summary>
    private const int MaxNames = 20;

    public async Task<List<AlertRule>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AlertRules.AsNoTracking().Include(r => r.Collection).Include(r => r.Channels).OrderBy(r => r.Name).ToListAsync(ct);
    }

    /// <summary>Legt eine Regel an (Id 0) oder ändert sie. Ändert sich, worauf sie achtet, beginnt sie mit einem neuen Ausgangsstand.</summary>
    /// <exception cref="ArgumentException">Pflichtangaben fehlen.</exception>
    public async Task<AlertRule> SaveAsync(AlertRule input, IReadOnlyCollection<int> channelIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException(L["Name is missing"]);
        if (AlertRule.NeedsCollection(input.Kind) && input.CollectionId is null)
            throw new ArgumentException(L["Please choose a collection."]);
        if (AlertRule.NeedsThreshold(input.Kind) && input.Threshold is not > 0)
            throw new ArgumentException(L["The threshold must be at least 1."]);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = input.Id == 0 ? null : await db.AlertRules.Include(r => r.Channels).FirstOrDefaultAsync(r => r.Id == input.Id, ct) ?? throw new KeyNotFoundException(L["Rule {0} not found", input.Id]);
        if (rule is null)
        {
            rule = new AlertRule { Name = input.Name.Trim(), CreatedUtc = time.GetUtcNow().UtcDateTime };
            db.AlertRules.Add(rule);
        }
        var collectionId = AlertRule.NeedsCollection(input.Kind) ? input.CollectionId : null;
        var threshold = AlertRule.NeedsThreshold(input.Kind) ? input.Threshold : null;
        if (input.Id == 0 || rule.Kind != input.Kind || rule.CollectionId != collectionId || rule.Threshold != threshold)
        {
            // Regeln, die auf Neues achten, sehen ab jetzt hin: nichts Älteres wird nachgemeldet, und nichts geht bis zum ersten Durchlauf verloren.
            rule.State = input.Kind is AlertKind.DeploymentFailed or AlertKind.NewComputer ? Stamp(time.GetUtcNow().UtcDateTime) : null;
            rule.LastCheckedUtc = null;
        }
        rule.Name = input.Name.Trim();
        rule.Kind = input.Kind;
        rule.Enabled = input.Enabled;
        rule.CollectionId = collectionId;
        rule.Threshold = threshold;
        rule.CooldownMinutes = Math.Clamp(input.CooldownMinutes, 0, 7 * 24 * 60);
        rule.Channels.Clear();
        rule.Channels.AddRange(await db.NotificationChannels.Where(c => channelIds.Contains(c.Id)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AlertRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>So oft wird ein gemeldetes Ereignis erneut versucht, wenn kein Kanal es annimmt (ein Versuch je Durchlauf).</summary>
    public const int MaxEventAttempts = 20;

    /// <summary>Ein Durchlauf: gemeldete Ereignisse verteilen und fällige Regeln am Datenbestand prüfen. Liefert die Zahl zugestellter Nachrichten.</summary>
    public async Task<int> EvaluateAsync(CancellationToken ct = default)
    {
        var events = queue.Drain();
        var now = time.GetUtcNow().UtcDateTime;
        List<AlertRule> rules;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            rules = await db.AlertRules.AsNoTracking().Include(r => r.Channels).Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync(ct);
        if (rules.Count == 0)
            return 0;

        var baseUrl = (await settings.GetAsync(SettingKeys.AgentServerUrl, ct))?.TrimEnd('/');
        var sent = 0;
        // Ereignisse, die eine Regel verschicken wollte, und ob irgendein Kanal sie angenommen hat.
        var attempted = new HashSet<AlertEvent>();
        var accepted = new HashSet<AlertEvent>();
        foreach (var rule in rules)
        {
            var isEvent = AlertRule.IsEvent(rule.Kind);
            if (!isEvent && rule.LastCheckedUtc is { } last && now - last < PollInterval)
                continue;
            var inCooldown = rule.LastFiredUtc is { } fired && now - fired < TimeSpan.FromMinutes(rule.CooldownMinutes);
            var channelIds = rule.Channels.Count == 0 ? null : rule.Channels.Select(c => c.Id).ToList();
            var prefix = $"{rule.Name}: ";

            if (isEvent)
            {
                // In der Pause verfällt, was geschieht; das ist ihr Zweck.
                if (inCooldown)
                    continue;
                string? lastTitle = null;
                foreach (var alert in events.Where(e => e.Kind == rule.Kind))
                {
                    attempted.Add(alert);
                    var message = new Notification(prefix + alert.Title, alert.Text, NotificationSeverity.Error, Link(baseUrl, alert.Path));
                    if (!Delivered(await notifications.SendAsync(message, channelIds, ct)))
                        continue;
                    accepted.Add(alert);
                    lastTitle = alert.Title;
                    sent++;
                }
                if (lastTitle is not null)
                    await StoreAsync(rule.Id, rule.State, checkedUtc: null, firedUtc: now, lastTitle, ct);
                continue;
            }

            var (messages, state) = await PollAsync(rule, now, baseUrl, ct);
            if (messages.Count == 0 || inCooldown)
            {
                await StoreAsync(rule.Id, state, now, firedUtc: null, null, ct);
                continue;
            }
            var delivered = 0;
            foreach (var message in messages)
            {
                if (Delivered(await notifications.SendAsync(message with { Title = prefix + message.Title }, channelIds, ct)))
                    delivered++;
            }
            sent += delivered;
            // Nahm kein Kanal etwas an, bleibt der Ausgangsstand stehen: der nächste Durchlauf findet dieselbe Veränderung wieder.
            if (delivered == 0)
                await StoreAsync(rule.Id, rule.State, now, firedUtc: null, null, ct);
            else
                await StoreAsync(rule.Id, state, now, now, messages[^1].Title, ct);
        }

        // Was niemand annehmen konnte (Mailserver nicht erreichbar), kommt beim nächsten Durchlauf noch einmal.
        foreach (var alert in attempted.Where(a => !accepted.Contains(a) && a.Attempts + 1 < MaxEventAttempts))
            queue.Raise(alert with { Attempts = alert.Attempts + 1 });
        return sent;
    }

    private static bool Delivered(List<NotificationResult> results) => results.Any(r => r.Error is null);

    private async Task StoreAsync(int ruleId, string? state, DateTime? checkedUtc, DateTime? firedUtc, string? lastMessage, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AlertRules.Where(r => r.Id == ruleId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.State, state)
            .SetProperty(r => r.LastCheckedUtc, r => checkedUtc ?? r.LastCheckedUtc)
            .SetProperty(r => r.LastFiredUtc, r => firedUtc ?? r.LastFiredUtc)
            .SetProperty(r => r.LastMessage, r => firedUtc != null ? lastMessage : r.LastMessage), ct);
    }

    /// <summary>Liest den Datenbestand für eine Regel ab und liefert die Nachrichten samt neuem Ausgangsstand.</summary>
    private async Task<(List<Notification> Messages, string? State)> PollAsync(AlertRule rule, DateTime now, string? baseUrl, CancellationToken ct)
    {
        var messages = new List<Notification>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        switch (rule.Kind)
        {
            case AlertKind.CollectionEntered or AlertKind.CollectionCount:
            {
                var collection = rule.CollectionId is null ? null : await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == rule.CollectionId, ct);
                if (collection is null)
                    return (messages, rule.State);
                var members = await collections.GetMemberIdsAsync(collection.Id, ct);
                var link = Link(baseUrl, $"computers?collection={collection.Id}");
                if (rule.Kind == AlertKind.CollectionCount)
                {
                    var above = members.Count >= rule.Threshold;
                    // Gemeldet wird der Übergang von "darunter" zu "erreicht"; der erste Durchlauf legt nur den Stand fest.
                    if (above && rule.State == bool.FalseString)
                        messages.Add(new Notification(L["{0} computers in \"{1}\"", members.Count, collection.Name],
                            L["The collection \"{0}\" now has {1} members (threshold {2}).", collection.Name, members.Count, rule.Threshold!], NotificationSeverity.Warning, link));
                    return (messages, above.ToString());
                }

                // Der erste Durchlauf legt nur den Ausgangsstand fest: wer schon Mitglied ist, ist nicht "neu hinzugekommen".
                if (rule.State is not null)
                {
                    var known = JsonSerializer.Deserialize<HashSet<int>>(rule.State) ?? [];
                    var entered = members.Where(id => !known.Contains(id)).ToList();
                    if (entered.Count > 0)
                    {
                        var names = await db.Computers.Where(c => entered.Contains(c.Id)).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(ct);
                        messages.Add(new Notification(L["{0} new in \"{1}\"", names.Count, collection.Name],
                            L["New members of the collection \"{0}\": {1}", collection.Name, Names(names)], NotificationSeverity.Warning, link));
                    }
                }
                return (messages, JsonSerializer.Serialize(members.Order()));
            }
            case AlertKind.ScanErrors:
            {
                var count = await db.Computers.CountAsync(c => c.LastScanError != null, ct);
                var above = count >= rule.Threshold;
                if (above && rule.State == bool.FalseString)
                    messages.Add(new Notification(L["{0} computers with scan errors", count],
                        L["{0} computers failed their last scan (threshold {1}).", count, rule.Threshold!], NotificationSeverity.Warning, Link(baseUrl, "computers")));
                return (messages, above.ToString());
            }
            case AlertKind.DeploymentFailed:
            {
                var since = Since(rule, now);
                var finished = await db.Deployments.AsNoTracking().Where(d => d.FinishedUtc != null && d.FinishedUtc > since)
                    .Select(d => new { d.Id, d.PackageName, d.FinishedUtc, Total = d.Targets.Count, Failed = d.Targets.Count(t => t.Status == TargetStatus.Failed) })
                    .OrderBy(d => d.FinishedUtc).ToListAsync(ct);
                foreach (var deployment in finished.Where(d => d.Failed > 0))
                    messages.Add(new Notification(L["Deployment #{0} \"{1}\": {2} of {3} failed", deployment.Id, deployment.PackageName, deployment.Failed, deployment.Total],
                        L["The deployment of \"{0}\" has finished; {1} of {2} computers failed.", deployment.PackageName, deployment.Failed, deployment.Total],
                        NotificationSeverity.Error, Link(baseUrl, $"deployments/{deployment.Id}")));
                return (messages, Stamp(finished.Count == 0 ? since : finished[^1].FinishedUtc!.Value));
            }
            case AlertKind.NewComputer:
            {
                var since = Since(rule, now);
                var created = await db.Computers.AsNoTracking().Where(c => c.CreatedUtc > since).OrderBy(c => c.CreatedUtc).Select(c => new { c.Name, c.CreatedUtc }).ToListAsync(ct);
                if (created.Count > 0)
                    messages.Add(new Notification(L["{0} new computers", created.Count], L["New computers: {0}", Names(created.Select(c => c.Name).ToList())],
                        NotificationSeverity.Info, Link(baseUrl, "computers")));
                return (messages, Stamp(created.Count == 0 ? since : created[^1].CreatedUtc));
            }
            case AlertKind.LicenseExceeded:
            {
                if (licenses is null)
                    return (messages, rule.State);
                var exceeded = (await licenses.UsageAsync(ct)).Where(u => u.Exceeded).ToList();
                // Gemeldet wird eine Lizenz, wenn sie neu überschritten ist; der erste Durchlauf legt nur den Stand fest.
                if (rule.State is not null)
                {
                    var known = JsonSerializer.Deserialize<HashSet<int>>(rule.State) ?? [];
                    foreach (var usage in exceeded.Where(u => !known.Contains(u.License.Id)))
                        messages.Add(new Notification(L["Licence \"{0}\" exceeded: {1} installed, {2} owned", usage.License.Name, usage.Installed, usage.License.Owned],
                            L["\"{0}\" is installed on {1} computers, but only {2} licences are recorded.", usage.License.Name, usage.Installed, usage.License.Owned],
                            NotificationSeverity.Warning, Link(baseUrl, "licenses")));
                }
                return (messages, JsonSerializer.Serialize(exceeded.Select(u => u.License.Id).Order()));
            }
            default:
                return (messages, rule.State);
        }
    }

    /// <summary>Ab wann eine Regel hinsieht: ab dem zuletzt Gesehenen; <see cref="SaveAsync"/> setzt den Beginn auf den Zeitpunkt des Anlegens.</summary>
    private static DateTime Since(AlertRule rule, DateTime now) =>
        DateTime.TryParse(rule.State, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since) ? since : now;

    private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private string Names(IReadOnlyList<string> names) =>
        names.Count <= MaxNames ? string.Join(", ", names) : string.Join(", ", names.Take(MaxNames)) + " " + L["and {0} more", names.Count - MaxNames];

    private static string? Link(string? baseUrl, string? path) => string.IsNullOrEmpty(baseUrl) || path is null ? null : $"{baseUrl}/{path}";
}
