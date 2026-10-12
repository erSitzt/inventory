using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class AlertTests
{
    private sealed class Rig : IDisposable
    {
        public TestDb Db { get; } = new();
        // In der Zukunft: Datensätze, die Dienste mit der echten Uhr anlegen, liegen damit sicher vor dem Beginn der Regeln.
        public Clock Time { get; } = new(new DateTimeOffset(2040, 1, 1, 8, 0, 0, TimeSpan.Zero));
        public RecordingWebhook Webhook { get; } = new();
        public AlertQueue Queue { get; } = new();
        public ComputerService Computers { get; }
        public CollectionService Collections { get; }
        public NotificationService Notifications { get; }
        public AlertService Alerts { get; }

        public Rig()
        {
            Computers = new ComputerService(Db, new SimulatedHostProbe());
            Collections = new CollectionService(Db);
            Notifications = new NotificationService(Db, new ReversingProtector(), new CredentialService(Db, new ReversingProtector()), new NoMail(), Webhook, Time);
            Alerts = new AlertService(Db, Collections, Notifications, Queue, new SettingsService(Db), Time);
        }

        public Task<NotificationChannel> ChannelAsync(string name = "Chat") =>
            Notifications.SaveAsync(new NotificationChannel { Name = name, Kind = NotificationKind.Webhook }, $"https://hooks.example.test/{name}");

        /// <summary>Ein Durchlauf nach Ablauf des Abfrageabstands.</summary>
        public async Task<int> NextAsync()
        {
            Time.Advance(AlertService.PollInterval);
            return await Alerts.EvaluateAsync();
        }

        public List<string> Titles => Webhook.Sent.Select(s => System.Text.Json.JsonDocument.Parse(s.Json).RootElement.GetProperty("title").GetString()!).ToList();

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Entering_a_collection_is_reported_once_and_existing_members_are_the_starting_point()
    {
        using var rig = new Rig();
        await rig.ChannelAsync();
        var added = await rig.Computers.ImportAsync("pc-01 pc-02 pc-03");
        var collection = await rig.Collections.SaveAsync(null, "Ohne Virenschutz", null, false, true, []);
        await rig.Collections.AddMembersAsync(collection.Id, [added[0].Id]);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "AV", Kind = AlertKind.CollectionEntered, CollectionId = collection.Id }, []);

        Assert.Equal(0, await rig.Alerts.EvaluateAsync());
        Assert.Equal(0, await rig.NextAsync());

        await rig.Collections.AddMembersAsync(collection.Id, [added[1].Id, added[2].Id]);
        // Vor Ablauf des Abstands wird der Bestand nicht erneut gelesen.
        Assert.Equal(0, await rig.Alerts.EvaluateAsync());
        Assert.Equal(1, await rig.NextAsync());
        Assert.Equal(0, await rig.NextAsync());

        Assert.Equal(["AV: 2 new in \"Ohne Virenschutz\""], rig.Titles);
        Assert.Contains("pc-02, pc-03", rig.Webhook.Sent.Single().Json);

        // Wer die Sammlung verlässt und wiederkommt, wird erneut gemeldet.
        await rig.Collections.RemoveMembersAsync(collection.Id, [added[1].Id]);
        await rig.NextAsync();
        await rig.Collections.AddMembersAsync(collection.Id, [added[1].Id]);
        Assert.Equal(1, await rig.NextAsync());
        var rule = (await rig.Alerts.ListAsync()).Single();
        Assert.Equal(("AV: 1 new in \"Ohne Virenschutz\"".Replace("AV: ", ""), rig.Time.GetUtcNow().UtcDateTime), (rule.LastMessage, rule.LastFiredUtc));
    }

    [Fact]
    public async Task Thresholds_report_when_they_are_reached_not_while_they_stay_reached()
    {
        using var rig = new Rig();
        await rig.ChannelAsync();
        var added = await rig.Computers.ImportAsync("pc-01 pc-02 pc-03");
        var collection = await rig.Collections.SaveAsync(null, "Alt", null, false, true, []);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Viele", Kind = AlertKind.CollectionCount, CollectionId = collection.Id, Threshold = 2 }, []);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Scans", Kind = AlertKind.ScanErrors, Threshold = 1 }, []);

        Assert.Equal(0, await rig.NextAsync());
        await rig.Collections.AddMembersAsync(collection.Id, [added[0].Id, added[1].Id]);
        Assert.Equal(1, await rig.NextAsync());
        await rig.Collections.AddMembersAsync(collection.Id, [added[2].Id]);
        Assert.Equal(0, await rig.NextAsync());
        await rig.Collections.RemoveMembersAsync(collection.Id, [added[0].Id, added[1].Id]);
        Assert.Equal(0, await rig.NextAsync());
        await rig.Collections.AddMembersAsync(collection.Id, [added[0].Id]);
        Assert.Equal(1, await rig.NextAsync());

        await using (var db = rig.Db.CreateDbContext())
            await db.Computers.Where(c => c.Id == added[0].Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastScanError, "Zugriff verweigert"));
        Assert.Equal(1, await rig.NextAsync());

        Assert.Equal(["Viele: 2 computers in \"Alt\"", "Viele: 2 computers in \"Alt\"", "Scans: 1 computers with scan errors"], rig.Titles);
    }

    [Fact]
    public async Task Finished_deployments_with_failures_and_new_computers_are_reported_from_the_rule_on()
    {
        using var rig = new Rig();
        await rig.ChannelAsync();
        await rig.Computers.ImportAsync("alt-01");
        await AddDeploymentAsync(rig, "Vorher", TargetStatus.Failed);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Verteilung", Kind = AlertKind.DeploymentFailed }, []);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Neu", Kind = AlertKind.NewComputer }, []);

        // Was vor der Regel lag, wird nicht nachgemeldet.
        Assert.Equal(0, await rig.NextAsync());

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await AddDeploymentAsync(rig, "Gut", TargetStatus.Succeeded, TargetStatus.AlreadyInstalled);
        await AddDeploymentAsync(rig, "Schlecht", TargetStatus.Succeeded, TargetStatus.Failed, TargetStatus.Failed);
        await AddDeploymentAsync(rig, "Läuft", finished: false, TargetStatus.Failed, TargetStatus.Running);
        await using (var db = rig.Db.CreateDbContext())
        {
            db.Computers.Add(new Computer { Name = "neu-01", CreatedUtc = rig.Time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await rig.NextAsync());
        Assert.Equal(0, await rig.NextAsync());
        Assert.Equal(2, rig.Titles.Count);
        Assert.Contains(rig.Titles, t => t.StartsWith("Verteilung: Deployment #") && t.EndsWith("\"Schlecht\": 2 of 3 failed"));
        Assert.Contains("Neu: 1 new computers", rig.Titles);
    }

    [Fact]
    public async Task Raised_events_reach_only_rules_of_their_kind_and_respect_the_pause_and_the_channels()
    {
        using var rig = new Rig();
        var chat = await rig.ChannelAsync("Chat");
        var ops = await rig.ChannelAsync("Betrieb");
        await new SettingsService(rig.Db).SetAsync(SettingKeys.AgentServerUrl, "https://inventory.example.local:5100/");
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Zeitplan", Kind = AlertKind.ScheduleFailed, CooldownMinutes = 60 }, [ops.Id]);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Aus", Kind = AlertKind.ScheduleFailed, Enabled = false }, []);

        rig.Queue.Raise(new AlertEvent(AlertKind.ScheduleFailed, "Schedule \"Nachts\" failed", "Package not approved", "schedules"));
        rig.Queue.Raise(new AlertEvent(AlertKind.AdSyncFailed, "AD", "LDAP unreachable"));
        // Gemeldete Ereignisse gehen sofort hinaus, ohne auf den Abfrageabstand zu warten.
        Assert.Equal(1, await rig.Alerts.EvaluateAsync());
        var (url, json) = rig.Webhook.Sent.Single();
        Assert.Equal("https://hooks.example.test/Betrieb", url);
        Assert.Contains("https://inventory.example.local:5100/schedules", json);
        Assert.Contains("\"severity\":\"error\"", json);

        rig.Time.Advance(TimeSpan.FromMinutes(30));
        rig.Queue.Raise(new AlertEvent(AlertKind.ScheduleFailed, "Noch einmal", "…"));
        Assert.Equal(0, await rig.Alerts.EvaluateAsync());
        rig.Time.Advance(TimeSpan.FromMinutes(31));
        rig.Queue.Raise(new AlertEvent(AlertKind.ScheduleFailed, "Später", "…"));
        Assert.Equal(1, await rig.Alerts.EvaluateAsync());
        Assert.Equal(["Zeitplan: Schedule \"Nachts\" failed", "Zeitplan: Später"], rig.Titles);
        Assert.NotNull(chat);
    }

    [Fact]
    public async Task What_no_channel_accepts_is_tried_again_instead_of_being_lost()
    {
        using var rig = new Rig();
        await rig.ChannelAsync();
        var added = await rig.Computers.ImportAsync("pc-01");
        var collection = await rig.Collections.SaveAsync(null, "Sammlung", null, false, true, []);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Sammlung", Kind = AlertKind.CollectionEntered, CollectionId = collection.Id }, []);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Abgleich", Kind = AlertKind.AdSyncFailed }, []);
        await rig.NextAsync();

        rig.Webhook.Fail = "HTTP 503";
        await rig.Collections.AddMembersAsync(collection.Id, [added[0].Id]);
        rig.Queue.Raise(new AlertEvent(AlertKind.AdSyncFailed, "AD", "LDAP unreachable"));
        Assert.Equal(0, await rig.NextAsync());
        Assert.All(await rig.Alerts.ListAsync(), rule => Assert.Null(rule.LastFiredUtc));

        rig.Webhook.Fail = null;
        Assert.Equal(2, await rig.NextAsync());
        Assert.Equal(0, await rig.NextAsync());
        Assert.Equal(["Sammlung: 1 new in \"Sammlung\"", "Abgleich: AD"], rig.Titles);

        // Ein Ereignis wird nicht endlos mitgeschleppt, wenn der Kanal dauerhaft ausfällt.
        rig.Webhook.Fail = "HTTP 503";
        rig.Queue.Raise(new AlertEvent(AlertKind.AdSyncFailed, "AD", "LDAP unreachable"));
        for (var i = 0; i < AlertService.MaxEventAttempts + 2; i++)
            await rig.Alerts.EvaluateAsync();
        Assert.Empty(rig.Queue.Drain());
    }

    [Fact]
    public async Task A_threshold_that_is_already_reached_and_events_before_the_first_check_are_handled_as_documented()
    {
        using var rig = new Rig();
        await rig.ChannelAsync();
        var added = await rig.Computers.ImportAsync("pc-01 pc-02");
        var collection = await rig.Collections.SaveAsync(null, "Voll", null, false, true, []);
        await rig.Collections.AddMembersAsync(collection.Id, [added[0].Id]);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Schwelle", Kind = AlertKind.CollectionCount, CollectionId = collection.Id, Threshold = 1 }, []);
        await rig.Alerts.SaveAsync(new AlertRule { Name = "Verteilung", Kind = AlertKind.DeploymentFailed }, []);

        // Zwischen dem Anlegen der Regel und ihrem ersten Durchlauf abgeschlossen: wird gemeldet.
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await AddDeploymentAsync(rig, "Früh", TargetStatus.Failed);

        // Die Schwelle war beim Anlegen schon erreicht: Ausgangsstand, keine Meldung.
        Assert.Equal(1, await rig.Alerts.EvaluateAsync());
        Assert.Equal(0, await rig.NextAsync());
        Assert.Single(rig.Titles, t => t.EndsWith("\"Früh\": 1 of 1 failed"));

        await rig.Collections.RemoveMembersAsync(collection.Id, [added[0].Id]);
        Assert.Equal(0, await rig.NextAsync());
        await rig.Collections.AddMembersAsync(collection.Id, [added[1].Id]);
        Assert.Equal(1, await rig.NextAsync());
        Assert.Equal("Schwelle: 1 computers in \"Voll\"", rig.Titles[^1]);
    }

    [Fact]
    public async Task Rules_need_their_collection_and_threshold_and_restart_when_their_subject_changes()
    {
        using var rig = new Rig();
        var added = await rig.Computers.ImportAsync("pc-01");
        var first = await rig.Collections.SaveAsync(null, "A", null, false, true, []);
        var second = await rig.Collections.SaveAsync(null, "B", null, false, true, []);
        await rig.Collections.AddMembersAsync(second.Id, [added[0].Id]);
        await rig.ChannelAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Alerts.SaveAsync(new AlertRule { Name = "X", Kind = AlertKind.CollectionEntered }, []));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Alerts.SaveAsync(new AlertRule { Name = "X", Kind = AlertKind.ScanErrors, Threshold = 0 }, []));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Alerts.SaveAsync(new AlertRule { Name = " ", Kind = AlertKind.NewComputer }, []));

        var rule = await rig.Alerts.SaveAsync(new AlertRule { Name = "Regel", Kind = AlertKind.CollectionEntered, CollectionId = first.Id }, []);
        await rig.NextAsync();
        // Andere Sammlung: deren Mitglieder sind der neue Ausgangsstand, kein Alarm.
        await rig.Alerts.SaveAsync(new AlertRule { Id = rule.Id, Name = "Regel", Kind = AlertKind.CollectionEntered, CollectionId = second.Id }, []);
        Assert.Equal(0, await rig.NextAsync());
        Assert.Equal(0, await rig.NextAsync());

        // Eine gelöschte Sammlung lässt die Regel ruhen, statt den Durchlauf zu stören.
        await rig.Collections.DeleteAsync(second.Id);
        Assert.Equal(0, await rig.NextAsync());
        Assert.Null((await rig.Alerts.ListAsync()).Single().Collection);
    }

    [Fact]
    public async Task Services_raise_failures_into_the_queue()
    {
        using var rig = new Rig();
        var settings = new SettingsService(rig.Db);
        await settings.SetAsync(SettingKeys.AdSyncEnabled, bool.TrueString);
        await settings.SetAsync(SettingKeys.AdSyncServer, "dc.example.local");
        var sync = new AdSyncService(new FailingDirectory(), settings, new CredentialService(rig.Db, new ReversingProtector()), rig.Computers, rig.Time, alerts: rig.Queue);

        await sync.SyncAsync();

        var raised = rig.Queue.Drain().Single();
        Assert.Equal((AlertKind.AdSyncFailed, "LDAP unreachable"), (raised.Kind, raised.Text));
        Assert.Empty(rig.Queue.Drain());
    }

    private static async Task AddDeploymentAsync(Rig rig, string name, params TargetStatus[] targets) => await AddDeploymentAsync(rig, name, true, targets);

    private static async Task AddDeploymentAsync(Rig rig, string name, bool finished, params TargetStatus[] targets)
    {
        await using var db = rig.Db.CreateDbContext();
        db.Deployments.Add(new Deployment
        {
            PackageName = name, CreatedUtc = rig.Time.GetUtcNow().UtcDateTime, FinishedUtc = finished ? rig.Time.GetUtcNow().UtcDateTime : null,
            Targets = targets.Select((status, i) => new DeploymentTarget { ComputerName = $"pc-{i}", Status = status }).ToList(),
        });
        await db.SaveChangesAsync();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoMail : IMailSender
    {
        public Task SendAsync(MailServer server, MailMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingWebhook : IWebhookSender
    {
        public List<(string Url, string Json)> Sent { get; } = [];
        public string? Fail { get; set; }

        public Task PostAsync(string url, string json, CancellationToken ct = default)
        {
            if (Fail is not null)
                throw new RemoteException(Fail);
            Sent.Add((url, json));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDirectory : IDirectorySource
    {
        public Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default) => throw new RemoteException("LDAP unreachable");
    }
}
