using Inventory.Contracts;
using System.Text;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class ScheduleTests
{
    private static readonly DateTime Wednesday = new(2026, 10, 7, 10, 30, 0);

    [Fact]
    public void Daily_runs_today_if_time_is_still_ahead_else_tomorrow()
    {
        var schedule = new Schedule { Name = "t", Trigger = ScheduleTrigger.Daily, TimeOfDay = new TimeSpan(12, 0, 0) };

        Assert.Equal(new DateTime(2026, 10, 7, 12, 0, 0), ScheduleCalculator.Next(schedule, Wednesday));
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0), ScheduleCalculator.Next(schedule, Wednesday.AddHours(1.5)));
    }

    [Fact]
    public void Weekly_picks_next_selected_weekday_including_same_day_next_week()
    {
        var mondayAndWednesday = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday);
        var schedule = new Schedule { Name = "t", Trigger = ScheduleTrigger.Weekly, TimeOfDay = new TimeSpan(8, 0, 0), DaysOfWeek = mondayAndWednesday };

        Assert.Equal(new DateTime(2026, 10, 12, 8, 0, 0), ScheduleCalculator.Next(schedule, Wednesday));
        schedule.DaysOfWeek = 1 << (int)DayOfWeek.Wednesday;
        Assert.Equal(new DateTime(2026, 10, 14, 8, 0, 0), ScheduleCalculator.Next(schedule, Wednesday));
        schedule.DaysOfWeek = 0;
        Assert.Null(ScheduleCalculator.Next(schedule, Wednesday));
    }

    [Fact]
    public void Once_runs_a_single_time_even_if_missed_and_interval_repeats()
    {
        var once = new Schedule { Name = "t", Trigger = ScheduleTrigger.Once, StartLocal = Wednesday.AddDays(-1) };
        Assert.Equal(Wednesday.AddDays(-1), ScheduleCalculator.Next(once, Wednesday));
        once.LastRunUtc = DateTime.UtcNow;
        Assert.Null(ScheduleCalculator.Next(once, Wednesday));

        var interval = new Schedule { Name = "t", Trigger = ScheduleTrigger.Interval, IntervalMinutes = 90, StartLocal = Wednesday.AddHours(2) };
        Assert.Equal(Wednesday.AddHours(2), ScheduleCalculator.Next(interval, Wednesday));
        interval.LastRunUtc = DateTime.UtcNow;
        Assert.Equal(Wednesday.AddMinutes(90), ScheduleCalculator.Next(interval, Wednesday));
    }

    [Fact]
    public async Task Due_schedules_scan_their_collection_once_and_get_a_new_next_run()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var collections = new CollectionService(db);
        var jobs = new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], computers, new CredentialService(db, new ReversingProtector()), new SettingsService(db));
        var store = new InventoryStore(db);
        var repoDir = Directory.CreateTempSubdirectory("inventory-repo-").FullName;
        var deployments = new DeploymentService(db, new PackageService(db, new PackageRepository(repoDir)), jobs, store, new SimulatedHostProbe());
        var schedules = new ScheduleService(db, collections, new ScanService(jobs, store), deployments, clock);
        var added = await computers.ImportAsync("pc-01 pc-02 pc-03");
        var collection = await collections.SaveAsync(null, "Büro", null, false, true, []);
        await collections.AddMembersAsync(collection.Id, [added[0].Id, added[1].Id]);

        await schedules.SaveAsync(new Schedule { Name = "Stündlich", Action = ScheduleAction.Scan, CollectionId = collection.Id, Trigger = ScheduleTrigger.Interval, IntervalMinutes = 60 });
        await schedules.SaveAsync(new Schedule { Name = "Aus", Enabled = false, Action = ScheduleAction.Scan, Trigger = ScheduleTrigger.Interval, IntervalMinutes = 1 });

        Assert.Equal(1, await schedules.RunDueAsync());
        Assert.Equal(0, await schedules.RunDueAsync());

        var stored = (await schedules.ListAsync()).Single(s => s.Name == "Stündlich");
        Assert.Equal("2 computers scanned", stored.LastResult);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(60), stored.NextRunUtc);
        Assert.Null((await schedules.ListAsync()).Single(s => s.Name == "Aus").NextRunUtc);
        Assert.Equal([true, true, false], (await computers.ListAsync()).Select(c => c.LastScanUtc is not null));

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, await schedules.RunDueAsync());

        // Ein Zeitplan verteilt nur freigegebene Pakete.
        var packages = new PackageService(db, new PackageRepository(repoDir));
        var package = await packages.SaveAsync(null, "In Arbeit", null, null);
        await packages.SaveStepAsync(package.Id, null, new JobStep { Name = "Setup", Type = StepType.Command, Script = "echo" }, null);
        var deploy = new Schedule { Name = "Verteilen", Action = ScheduleAction.Deploy, PackageId = package.Id, CollectionId = collection.Id, Trigger = ScheduleTrigger.Interval, IntervalMinutes = 60 };
        await schedules.SaveAsync(deploy);
        Assert.Contains("not approved", await schedules.RunNowAsync(deploy.Id));
        await packages.ApproveAsync(package.Id, "anna");
        Assert.Contains("started on 2 computers", await schedules.RunNowAsync(deploy.Id));
        Directory.Delete(repoDir, recursive: true);
    }

    [Fact]
    public async Task With_dns_names_new_computers_get_their_fqdn_and_short_entries_are_renamed()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var directory = new FakeDirectory
        {
            Computers =
            [
                new("PC-01", "pc-01.corp.example", null, null), new("PC-02", "pc-02.corp.example", null, null),
                // Ohne DNS-Namen im Verzeichnis, und mit einem, der nicht zum Konto passt: es bleibt beim kurzen Namen.
                new("PC-03", null, null, null), new("PC-04", "anders.corp.example", null, null),
                new("PC-05", "pc-05.corp.example", null, null), new("PC-06", "pc-06.corp.example", null, null),
            ],
        };
        var sync = new AdSyncService(directory, new SettingsService(db), new CredentialService(db, new ReversingProtector()), computers, clock);
        // pc-05 trägt von Hand einen anderen DNS-Namen, "manuell" kennt das Verzeichnis nicht.
        var before = await computers.ImportAsync("pc-01 pc-04 pc-05.other.example manuell");
        await sync.SaveSettingsAsync(true, null, null, null, 24);

        // Ohne die Einstellung ändert sich an den Namen nichts.
        Assert.Empty((await sync.PreviewAsync()).Renames!);
        Assert.Equal(["PC-02", "PC-03", "PC-06"], (await sync.PreviewAsync()).NewNames);

        await sync.SaveSettingsAsync(true, null, null, null, 24, useDnsNames: true);
        var preview = await sync.PreviewAsync();
        Assert.Equal([("pc-01", "pc-01.corp.example")], preview.Renames);
        Assert.Equal(["pc-02.corp.example", "PC-03", "pc-06.corp.example"], preview.NewNames);

        var result = await sync.SyncAsync();
        Assert.Contains("1 renamed", result);
        var after = await computers.ListAsync();
        Assert.Equal(["manuell", "pc-01.corp.example", "pc-02.corp.example", "PC-03", "pc-04", "pc-05.other.example", "pc-06.corp.example"],
            after.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase));
        // Derselbe Eintrag, nicht ein neuer: Inventar und Verlauf hängen an der ID.
        Assert.Equal(before.Single(c => c.Name == "pc-01").Id, after.Single(c => c.Name == "pc-01.corp.example").Id);
        Assert.All(after.Where(c => c.Name != "manuell"), c => Assert.NotNull(c.DirectorySeenUtc));

        // Der nächste Lauf findet nichts mehr zu tun.
        var again = await sync.PreviewAsync();
        Assert.Empty(again.Renames!);
        Assert.Empty(again.NewNames);
    }

    [Fact]
    public async Task Ad_sync_adds_only_unknown_computers_and_respects_interval()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var settings = new SettingsService(db);
        var directory = new FakeDirectory { Computers = [new("PC-01", null, null, null), new("PC-02", null, null, null)] };
        var sync = new AdSyncService(directory, settings, new CredentialService(db, new ReversingProtector()), computers, clock);
        await computers.ImportAsync("pc-01.corp.local");

        Assert.False(await sync.SyncIfDueAsync());
        await sync.SaveSettingsAsync(true, "dc01", "OU=Clients,DC=corp,DC=local; OU=Server,DC=corp,DC=local", null, 12);
        Assert.Equal(["PC-02"], (await sync.PreviewAsync()).NewNames);
        Assert.True(await sync.SyncIfDueAsync());
        Assert.False(await sync.SyncIfDueAsync());

        Assert.Equal(["pc-01.corp.local", "PC-02"], (await computers.ListAsync()).Select(c => c.Name));
        Assert.Equal("2 computer accounts found, 1 newly created", (await sync.GetSettingsAsync()).LastResult);
        Assert.Equal(["OU=Clients,DC=corp,DC=local", "OU=Server,DC=corp,DC=local"], directory.LastQuery!.BaseDns);

        clock.Advance(TimeSpan.FromHours(13));
        directory.Fail = "Server nicht erreichbar";
        Assert.True(await sync.SyncIfDueAsync());
        Assert.Equal("Error: Server nicht erreichbar", (await sync.GetSettingsAsync()).LastResult);
    }

    [Fact]
    public async Task Ad_sync_marks_missing_and_disabled_computers_and_deletes_them_after_the_grace_period()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var directory = new FakeDirectory { Computers = [new("PC-01", null, null, null), new("PC-02", null, null, null), new("PC-03", null, null, null, Enabled: false)] };
        var sync = new AdSyncService(directory, new SettingsService(db), new CredentialService(db, new ReversingProtector()), computers, clock, new AuditService(db));
        await computers.ImportAsync("handarbeit pc-03");
        await sync.SaveSettingsAsync(true, null, null, null, 24, excludeDisabled: true, AdMissingAction.Delete, missingDays: 30);

        // Deaktivierte Konten werden nicht angelegt; ein vorhandener Rechner mit deaktiviertem Konto gilt als fehlend.
        Assert.Equal("3 computer accounts found, 2 newly created, 1 disabled, 1 not in AD", await sync.SyncAsync());
        var list = await computers.ListAsync();
        Assert.Equal(["handarbeit", "PC-01", "PC-02", "pc-03"], list.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["pc-03"], list.Where(c => c.MissingInDirectory).Select(c => c.Name));
        Assert.True(list.Single(c => c.Name == "pc-03").DirectoryDisabled);
        Assert.NotNull(list.Single(c => c.Name == "PC-01").DirectorySeenUtc);

        // PC-02 verschwindet aus dem Verzeichnis: erst gekennzeichnet, nach der Frist gelöscht. Der von Hand angelegte Rechner bleibt.
        directory.Computers = [new("PC-01", null, null, null), new("PC-03", null, null, null, Enabled: false)];
        clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(("PC-02,pc-03", ""), await Preview());
        await sync.SyncAsync();
        clock.Advance(TimeSpan.FromDays(25));
        Assert.Equal(("PC-02,pc-03", "pc-03"), await Preview());
        Assert.EndsWith("2 not in AD, 1 deleted", await sync.SyncAsync());
        clock.Advance(TimeSpan.FromDays(10));
        // Taucht das Konto wieder auf, verschwindet die Kennzeichnung und die Frist beginnt neu.
        directory.Computers = [new("PC-01", null, null, null), new("PC-02", null, null, null)];
        await sync.SyncAsync();
        Assert.Equal(["handarbeit", "PC-01", "PC-02"], (await computers.ListAsync()).Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(await computers.ListAsync(), c => c.MissingInDirectory);

        async Task<(string, string)> Preview()
        {
            var preview = await sync.PreviewAsync();
            return (string.Join(",", preview.MissingNames!.Order(StringComparer.OrdinalIgnoreCase)), string.Join(",", preview.DeleteNames!));
        }
    }

    [Fact]
    public async Task Ad_sync_keeps_everything_when_the_directory_answers_empty_or_too_many_would_be_deleted()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var all = Enumerable.Range(1, 10).Select(i => new DirectoryComputer($"PC-{i:D2}", null, null, null)).ToList();
        var directory = new FakeDirectory { Computers = all };
        var sync = new AdSyncService(directory, new SettingsService(db), new CredentialService(db, new ReversingProtector()), computers, clock);
        await sync.SaveSettingsAsync(true, null, null, null, 24, excludeDisabled: false, AdMissingAction.Delete, missingDays: 1);
        await sync.SyncAsync();

        // Leere Antwort (falsche Suchwurzel, fehlende Rechte): nichts wird gekennzeichnet.
        directory.Computers = [];
        await sync.SyncAsync();
        Assert.DoesNotContain(await computers.ListAsync(), c => c.MissingInDirectory);

        // Fast alles fehlt auf einmal: gekennzeichnet, aber nicht gelöscht.
        directory.Computers = [.. all.Take(2)];
        await sync.SyncAsync();
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Contains("conspicuously many", await sync.SyncAsync());
        Assert.Equal(10, (await computers.ListAsync()).Count);
        Assert.Equal(8, (await computers.ListAsync()).Count(c => c.MissingInDirectory));

        // Deaktivierte Konten zählen als vorhanden, wenn sie nicht ausgeschlossen sind.
        directory.Computers = [.. all.Select(c => c with { Enabled = false })];
        await sync.SyncAsync();
        Assert.DoesNotContain(await computers.ListAsync(), c => c.MissingInDirectory);
        Assert.All(await computers.ListAsync(), c => Assert.True(c.DirectoryDisabled));
    }

    [Fact]
    public async Task Ad_sync_stores_ou_groups_description_and_last_logon_and_keeps_them_when_the_account_disappears()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var logon = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var laptop = new DirectoryComputer("PC-01", null, null, "Empfang", DistinguishedName: @"CN=PC-01,OU=Laptops,OU=Clients\, Nord,DC=corp,DC=example",
            Groups: ["Pilot-Updates", "VPN"], LastLogonUtc: logon);
        var directory = new FakeDirectory { Computers = [laptop, new DirectoryComputer("PC-02", null, null, null, DistinguishedName: "CN=PC-02,CN=Computers,DC=corp,DC=example")] };
        var sync = new AdSyncService(directory, new SettingsService(db), new CredentialService(db, new ReversingProtector()), computers, clock);
        await sync.SaveSettingsAsync(true, null, null, null, 24);
        // Schon der Lauf, der den Rechner anlegt, trägt die Angaben ein.
        await sync.SyncAsync();

        var first = (await computers.ListAsync()).Single(c => c.Name == "PC-01");
        Assert.Equal(@"OU=Laptops,OU=Clients\, Nord,DC=corp,DC=example", first.DirectoryPath);
        Assert.Equal("Clients, Nord/Laptops", first.DirectoryOu);
        Assert.Equal(["Pilot-Updates", "VPN"], first.DirectoryGroupNames);
        Assert.Equal("Empfang", first.DirectoryDescription);
        Assert.Equal(logon, first.DirectoryLastLogonUtc);
        Assert.Equal("Computers", (await computers.ListAsync()).Single(c => c.Name == "PC-02").DirectoryOu);

        static bool Matches(RuleField field, RuleOperator op, string value, Computer computer) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, computer);
        Assert.True(Matches(RuleField.DirectoryOu, RuleOperator.Contains, "laptops", first));
        Assert.True(Matches(RuleField.DirectoryOu, RuleOperator.Equals, "Clients, Nord/Laptops", first));
        Assert.True(Matches(RuleField.DirectoryOu, RuleOperator.StartsWith, "OU=Laptops,", first));
        Assert.True(Matches(RuleField.DirectoryOu, RuleOperator.NotContains, "Server", first));
        Assert.True(Matches(RuleField.DirectoryGroup, RuleOperator.Equals, "vpn", first));
        Assert.True(Matches(RuleField.DirectoryGroup, RuleOperator.NotEquals, "Kiosk", first));
        Assert.False(Matches(RuleField.DirectoryGroup, RuleOperator.NotEquals, "VPN", first));
        Assert.True(Matches(RuleField.DirectoryDescription, RuleOperator.Contains, "empf", first));
        Assert.False(Matches(RuleField.DaysSinceDirectoryLogon, RuleOperator.GreaterOrEqual, "0", new Computer { Name = "x" }));

        // Verschoben und aus einer Gruppe entfernt: der nächste Lauf zieht nach.
        directory.Computers = [laptop with { DistinguishedName = "CN=PC-01,OU=Desktops,DC=corp,DC=example", Groups = ["VPN"] }, directory.Computers[1]];
        await sync.SyncAsync();
        first = (await computers.ListAsync()).Single(c => c.Name == "PC-01");
        Assert.Equal("Desktops", first.DirectoryOu);
        Assert.Equal(["VPN"], first.DirectoryGroupNames);

        // Fehlt das Konto, bleibt stehen, was zuletzt bekannt war.
        directory.Computers = [directory.Computers[1]];
        await sync.SyncAsync();
        first = (await computers.ListAsync()).Single(c => c.Name == "PC-01");
        Assert.True(first.MissingInDirectory);
        Assert.Equal("Desktops", first.DirectoryOu);
    }

    [Theory]
    [InlineData("CN=PC-01,OU=Laptops,OU=Clients,DC=corp,DC=example", "OU=Laptops,OU=Clients,DC=corp,DC=example", "Clients/Laptops")]
    [InlineData("CN=PC-01,DC=corp,DC=example", "DC=corp,DC=example", "")]
    [InlineData(null, null, null)]
    public void Distinguished_names_are_split_into_container_and_readable_path(string? dn, string? parent, string? path)
    {
        Assert.Equal(parent, DirectoryNames.Parent(dn));
        Assert.Equal(path, DirectoryNames.Path(DirectoryNames.Parent(dn)));
        Assert.Equal(@"Vertrieb, Nord", DirectoryNames.CommonName(@"CN=Vertrieb\, Nord,OU=Groups,DC=corp,DC=example"));
        // Dieselben Zeichen als Hex-Maskierung, auch für Umlaute (zwei Bytes in UTF-8).
        Assert.Equal("Ops, EMEA", DirectoryNames.CommonName(@"CN=Ops\2C EMEA,OU=Groups,DC=corp,DC=example"));
        Assert.Equal("Büro/Süd", DirectoryNames.Path(@"OU=S\C3\BCd,OU=B\C3\BCro,DC=corp,DC=example"));
    }

    [Fact]
    public async Task Heartbeat_probes_only_computers_without_name_issues_and_respects_the_interval()
    {
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var probed = new List<string>();
        // "alt" ist ein veralteter DNS-Eintrag: unter dem Namen antwortet PC-02.
        var computers = new ComputerService(db, new FixedProbe(host => { probed.Add(host); return new ProbeResult(true, "10.1.1.1", MachineName: host == "alt" ? "PC-02" : host.ToUpperInvariant()); }));
        var heartbeat = new HeartbeatService(computers, new SettingsService(db), clock);
        await computers.ImportAsync("pc-01 alt pc-03");

        // Standard: aus.
        Assert.False(await heartbeat.RunIfDueAsync());
        Assert.Empty(probed);

        await heartbeat.SaveSettingsAsync(true, 5);
        Assert.True(await heartbeat.RunIfDueAsync());
        // Beim ersten Lauf ist noch keine Auffälligkeit bekannt; sie wird dabei entdeckt.
        Assert.Equal(["alt", "pc-01", "pc-03"], probed.Order());
        Assert.Equal("3 computers checked, 2 online", (await heartbeat.GetSettingsAsync()).LastResult);
        Assert.False((await computers.ListAsync()).Single(c => c.Name == "alt").IsOnline);

        // Innerhalb des Intervalls passiert nichts; danach wird der auffällige Rechner übersprungen.
        probed.Clear();
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(await heartbeat.RunIfDueAsync());
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await heartbeat.RunIfDueAsync());
        Assert.Equal(["pc-01", "pc-03"], probed.Order());
        Assert.Equal("2 computers checked, 2 online, 1 skipped due to name anomaly", (await heartbeat.GetSettingsAsync()).LastResult);
    }

    [Fact]
    public async Task Example_schedules_are_created_once_disabled_and_not_recreated_after_deletion()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var examples = new ExampleData(db, settings);
        var schedules = new ScheduleService(db, new CollectionService(db), null!, null!, TimeProvider.System);

        Assert.Equal(4, await examples.SeedSchedulesAsync());
        var list = await schedules.ListAsync();
        Assert.All(list, s => Assert.False(s.Enabled));
        Assert.All(list, s => Assert.Null(s.NextRunUtc));
        Assert.All(list, s => Assert.StartsWith(ExampleData.Prefix, s.Name));
        // Jedes Beispiel ergibt einen gültigen nächsten Termin, sobald es eingeschaltet wird.
        foreach (var schedule in list)
        {
            schedule.Enabled = true;
            await schedules.SaveAsync(schedule);
        }
        Assert.All(await schedules.ListAsync(), s => Assert.NotNull(s.NextRunUtc));

        Assert.Equal(0, await examples.SeedSchedulesAsync());
        foreach (var schedule in list)
            await schedules.DeleteAsync(schedule.Id);
        Assert.Equal(0, await examples.SeedSchedulesAsync());
        Assert.Empty(await schedules.ListAsync());
    }

    [Fact]
    public void Csv_rows_are_escaped_and_formulas_neutralised()
    {
        var csv = new StringBuilder();

        ReportExport.Row(csv, "PC;01", "sagt \"hallo\"", null, "=SUMME(A1)", "normal");

        Assert.Equal("\"PC;01\";\"sagt \"\"hallo\"\"\";;'=SUMME(A1);normal\r\n", csv.ToString());
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeDirectory : IDirectorySource
    {
        public List<DirectoryComputer> Computers { get; set; } = [];
        public DirectoryQuery? LastQuery { get; private set; }
        public string? Fail { get; set; }

        public Task<List<DirectoryComputer>> GetComputersAsync(DirectoryQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Fail is null ? Task.FromResult(Computers) : throw new RemoteException(Fail);
        }
    }
}
