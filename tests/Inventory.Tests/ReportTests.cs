using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class ReportTests
{
    private static async Task<(TestDb Db, List<Computer> Computers, CollectionService Collections)> SeedAsync()
    {
        var db = new TestDb();
        var computers = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-03 pc-01 pc-02");
        await using (var context = db.CreateDbContext())
        {
            var stored = await context.Computers.OrderBy(c => c.Name).ToListAsync();
            (stored[0].MemoryBytes, stored[0].Location, stored[0].Notes) = (8L << 30, "Halle 2", "=SUMME(A1)");
            (stored[1].MemoryBytes, stored[1].Location) = (32L << 30, "Büro");
            (stored[2].MemoryBytes, stored[2].Location) = (null, "Büro");
            context.Software.Add(new ComputerSoftware { ComputerId = stored[0].Id, Name = "Editor", Version = "1.0" });
            context.Software.Add(new ComputerSoftware { ComputerId = stored[1].Id, Name = "Editor", Version = "2.0" });
            context.InventoryChanges.Add(new InventoryChange { ComputerId = stored[0].Id, TimeUtc = DateTime.UtcNow.AddDays(-2), Category = "Software", Kind = ChangeKind.Added, Item = "Editor", NewValue = "1.0" });
            context.InventoryChanges.Add(new InventoryChange { ComputerId = stored[1].Id, TimeUtc = DateTime.UtcNow.AddDays(-90), Category = "Software", Kind = ChangeKind.Removed, Item = "Alt" });
            context.Deployments.Add(new Deployment
            {
                PackageName = "Editor", PackageVersion = "2.0", CreatedUtc = DateTime.UtcNow.AddDays(-1), FinishedUtc = DateTime.UtcNow.AddDays(-1),
                Targets =
                [
                    new DeploymentTarget { ComputerName = "pc-01", Status = TargetStatus.Succeeded }, new DeploymentTarget { ComputerName = "pc-02", Status = TargetStatus.AlreadyInstalled },
                    new DeploymentTarget { ComputerName = "pc-03", Status = TargetStatus.Failed }, new DeploymentTarget { ComputerName = "pc-04", Status = TargetStatus.WaitingOffline },
                ],
            });
            await context.SaveChangesAsync();
        }
        return (db, computers, new CollectionService(db));
    }

    [Fact]
    public async Task A_report_shows_the_chosen_columns_of_the_collection_in_the_chosen_order()
    {
        var (db, _, collections) = await SeedAsync();
        using var _d = db;
        var reports = new ReportService(db);
        var office = await collections.SaveAsync(null, "Büro", null, true, true, [new CollectionRule { Field = RuleField.Location, Operator = RuleOperator.Equals, Value = "Büro" }]);

        var all = await reports.TableAsync(new ReportQuery(ReportSource.Computers));
        Assert.Equal(reports.ColumnsOf(ReportSource.Computers).Select(c => c.Key), all.Columns.Select(c => c.Key));
        Assert.Equal(["pc-01", "pc-02", "pc-03"], all.Rows.Select(r => r[0]));

        // Sortiert wird als Zahl (8 vor 32), leere Werte stehen am Ende; unbekannte Spalten fallen weg.
        var byMemory = await reports.TableAsync(new ReportQuery(ReportSource.Computers, Columns: ["memoryGb", "gibtEsNicht", "name"], SortColumn: "memoryGb"));
        Assert.Equal(["memoryGb", "name"], byMemory.Columns.Select(c => c.Key));
        Assert.Equal(["8|pc-01", "32|pc-02", "|pc-03"], byMemory.Rows.Select(r => string.Join('|', r)));

        var filtered = await reports.TableAsync(new ReportQuery(ReportSource.Computers, office.Id, Columns: ["name"], SortColumn: "name", SortDescending: true));
        Assert.Equal(["pc-03", "pc-02"], filtered.Rows.Select(r => r[0]));
        Assert.Equal(["pc-02"], (await reports.TableAsync(new ReportQuery(ReportSource.Software, office.Id, Columns: ["computer"]))).Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Deployments_and_changes_cover_a_period()
    {
        var (db, _, _) = await SeedAsync();
        using var _d = db;
        var reports = new ReportService(db);

        var deployment = (await reports.TableAsync(new ReportQuery(ReportSource.Deployments, Columns: ["package", "computers", "succeeded", "failed", "cancelled", "open"]))).Rows.Single();
        Assert.Equal(["Editor", "4", "2", "1", "0", "1"], deployment);

        var recent = await reports.TableAsync(new ReportQuery(ReportSource.Changes, Columns: ["computer", "kind", "item"]));
        Assert.Equal(["pc-01|new|Editor"], recent.Rows.Select(r => string.Join('|', r)));
        Assert.Equal(2, (await reports.TableAsync(new ReportQuery(ReportSource.Changes, Days: 365))).Rows.Count);
        // Die Sammlung filtert die Änderungen, bevor irgendeine Obergrenze greift.
        var office = await new CollectionService(db).SaveAsync(null, "Büro", null, true, true, [new CollectionRule { Field = RuleField.Location, Operator = RuleOperator.Equals, Value = "Büro" }]);
        Assert.Equal(["pc-02"], (await reports.TableAsync(new ReportQuery(ReportSource.Changes, office.Id, Days: 365, Columns: ["computer"]))).Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Saved_reports_keep_their_definition_and_reject_columns_of_another_source()
    {
        var (db, _, _) = await SeedAsync();
        using var _d = db;
        var saved = new SavedReportService(db, new ReportService(db));

        await Assert.ThrowsAsync<ArgumentException>(() => saved.SaveAsync(new Report { Name = "X", Source = ReportSource.Software, Columns = "name,serial" }));
        await Assert.ThrowsAsync<ArgumentException>(() => saved.SaveAsync(new Report { Name = "X", Source = ReportSource.Software, SortColumn = "memoryGb" }));
        await Assert.ThrowsAsync<ArgumentException>(() => saved.SaveAsync(new Report { Name = " ", Source = ReportSource.Software }));
        await Assert.ThrowsAsync<ArgumentException>(() => saved.SaveAsync(new Report { Name = "X", Source = (ReportSource)999 }));

        var report = await saved.SaveAsync(new Report { Name = " Arbeitsspeicher: Büro/Halle ", Source = ReportSource.Computers, Columns = "name, memoryGb", SortColumn = "memoryGb", SortDescending = true, Days = 7 });
        Assert.Equal(("Arbeitsspeicher: Büro/Halle", "name,memoryGb", (int?)null), (report.Name, report.Columns, report.Days));
        Assert.Equal("Arbeitsspeicher_ Büro_Halle", SavedReportService.FileName(report));

        var table = await saved.RunAsync((await saved.GetAsync(report.Id))!);
        Assert.Equal(["pc-02|32", "pc-01|8", "pc-03|"], table.Rows.Select(r => string.Join('|', r)));

        await saved.DeleteAsync(report.Id);
        Assert.Empty(await saved.ListAsync());
    }

    [Fact]
    public async Task The_excel_workbook_is_valid_and_keeps_numbers_as_numbers_and_formulas_as_text()
    {
        var (db, _, _) = await SeedAsync();
        using var _d = db;
        var table = await new ReportService(db).TableAsync(new ReportQuery(ReportSource.Computers, Columns: ["name", "memoryGb", "notes"]));
        table.Rows[1][2] = "Steuerzeichen \u0001 & <Tags> \"x\" 😀";

        var bytes = ReportExport.Xlsx(table, "Rechner: Büro/Halle [alt] und noch viel länger als erlaubt");

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator().Validate(document).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        Assert.Equal("Rechner  Büro Halle  alt  und n", document.WorkbookPart!.Workbook.Descendants<Sheet>().Single().Name!.Value);
        var rows = document.WorkbookPart.WorksheetParts.Single().Worksheet.Descendants<Row>().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(["Name", "Memory (GB)", "Notes"], rows[0].Elements<Cell>().Select(c => c.InnerText));
        var first = rows[1].Elements<Cell>().ToList();
        Assert.Equal(("A2", "pc-01", CellValues.InlineString), (first[0].CellReference!.Value, first[0].InnerText, first[0].DataType!.Value));
        // Die Zahl steht als Zahl im Blatt, die Formel als Text.
        Assert.Equal(("8", true), (first[1].CellValue!.Text, first[1].DataType is null));
        Assert.Equal(("=SUMME(A1)", CellValues.InlineString), (first[2].InnerText, first[2].DataType!.Value));
        Assert.Null(first[2].CellFormula);
        Assert.Equal("Steuerzeichen  & <Tags> \"x\" 😀", rows[2].Elements<Cell>().Last().InnerText);
        // Leere Werte ergeben keine Zelle.
        Assert.Equal(["A4"], rows[3].Elements<Cell>().Select(c => c.CellReference!.Value));

        // Ein Name nur aus Steuerzeichen ergibt trotzdem ein gültiges Blatt.
        using (var odd = SpreadsheetDocument.Open(new MemoryStream(ReportExport.Xlsx(table, "\u0001\u0002")), false))
        {
            Assert.Empty(new OpenXmlValidator().Validate(odd));
            Assert.Equal("Report", odd.WorkbookPart!.Workbook.Descendants<Sheet>().Single().Name!.Value);
        }

        // Auch eine leere Tabelle ist eine gültige Arbeitsmappe.
        using var empty = SpreadsheetDocument.Open(new MemoryStream(ReportExport.Xlsx(new ReportTable([], []), "")), false);
        Assert.Empty(new OpenXmlValidator().Validate(empty));
        Assert.Equal(["A", "Z", "AA", "AZ", "BA", "ZZ", "AAA"], new[] { 0, 25, 26, 51, 52, 701, 702 }.Select(ReportExport.ColumnName));
    }

    [Fact]
    public async Task Csv_export_protects_against_formulas_and_matches_the_table()
    {
        var (db, _, _) = await SeedAsync();
        using var _d = db;
        var reports = new ReportService(db);

        var csv = ReportExport.Csv(await reports.TableAsync(new ReportQuery(ReportSource.Computers, Columns: ["name", "notes"])));

        Assert.Equal("Name;Notes\r\npc-01;'=SUMME(A1)\r\npc-02;\r\npc-03;\r\n", csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], ReportExport.CsvBytes(new ReportTable([], [])).Take(3));
        // Die festen Exporte laufen über dieselbe Abfrage.
        Assert.StartsWith("Name;Reported name;Online;", await reports.ComputersCsvAsync());
        Assert.StartsWith("Computer;Program;Version;", await reports.SoftwareCsvAsync());
    }

    [Fact]
    public async Task A_schedule_sends_the_report_as_an_excel_attachment_and_fails_loudly_without_a_channel()
    {
        var (db, _, _) = await SeedAsync();
        using var _d = db;
        var clock = TimeProvider.System;
        var mail = new RecordingMail();
        var settings = new SettingsService(db);
        var notifications = new NotificationService(db, new ReversingProtector(), new CredentialService(db, new ReversingProtector()), mail, new NoWebhook(), clock);
        var saved = new SavedReportService(db, new ReportService(db));
        var queue = new Inventory.Core.Abstractions.AlertQueue();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var jobs = new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], computers, new CredentialService(db, new ReversingProtector()), settings);
        var deployments = new DeploymentService(db, new PackageService(db, new PackageRepository(Directory.CreateTempSubdirectory().FullName)), jobs, store, new SimulatedHostProbe());
        var schedules = new ScheduleService(db, new CollectionService(db), new ScanService(jobs, store), deployments, clock, alerts: queue,
            reportMailer: new ReportMailer(saved, notifications, settings, clock));
        await settings.SetAsync(SettingKeys.AgentServerUrl, "https://inventory.example.local:5100");
        var report = await saved.SaveAsync(new Report { Name = "Hardware", Source = ReportSource.Computers, Columns = "name,memoryGb" });
        var schedule = new Schedule { Name = "Monatsbericht", Action = ScheduleAction.Report, ReportId = report.Id, CollectionId = 99, PackageId = 5, Trigger = ScheduleTrigger.Interval, IntervalMinutes = 60 };
        await schedules.SaveAsync(schedule);
        Assert.Equal((null, null), (schedule.CollectionId, schedule.PackageId));

        // Nur ein Webhook als Ziel: er bekommt den Hinweis, ein Anhang wird gar nicht erst erzeugt.
        var hook = await notifications.SaveAsync(new NotificationChannel { Name = "Chat", Kind = NotificationKind.Webhook }, "https://hooks.example.test/x");
        var built = 0;
        await notifications.SendAsync(new Notification("Titel", "Text"), [hook.Id], attachment: () => { built++; return new Inventory.Core.Abstractions.MailAttachment("a.xlsx", "x/y", []); });
        Assert.Equal(0, built);
        await notifications.DeleteAsync(hook.Id);

        // Ohne Kanal: der Lauf meldet den Grund, statt still nichts zu tun.
        Assert.Contains("No enabled notification channel", await schedules.RunNowAsync(schedule.Id));

        var channel = await notifications.SaveAsync(new NotificationChannel
        {
            Name = "Leitung", Kind = NotificationKind.Email, SmtpHost = "mail.example.local", Sender = "inventory@example.local", Recipients = "leitung@example.local",
        }, null);
        schedule.NotificationChannelId = channel.Id;
        await schedules.SaveAsync(schedule);

        Assert.Equal("Report with 3 rows sent via 1 channels", await schedules.RunNowAsync(schedule.Id));
        var message = mail.Sent.Single();
        Assert.Equal("[Inventory] Report \"Hardware\"", message.Subject);
        Assert.Contains("3 rows", message.Body);
        Assert.EndsWith($"https://inventory.example.local:5100/reports/{report.Id}", message.Body);
        Assert.Matches(@"^Hardware \d{4}-\d{2}-\d{2}\.xlsx$", message.Attachment!.FileName);
        using var workbook = SpreadsheetDocument.Open(new MemoryStream(message.Attachment.Content), false);
        Assert.Equal(4, workbook.WorkbookPart!.WorksheetParts.Single().Worksheet.Descendants<Row>().Count());

        // Ein regulärer Lauf, der nicht zustellen kann, geht an die Alarmregeln.
        mail.Fail = "Relay access denied";
        await using (var context = db.CreateDbContext())
            await context.Schedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunUtc, DateTime.UtcNow.AddMinutes(-1)));
        await schedules.RunDueAsync();
        var raised = queue.Drain().Single();
        Assert.Equal(AlertKind.ScheduleFailed, raised.Kind);
        Assert.Contains("Relay access denied", raised.Text);

        // Mit dem Bericht verschwindet auch sein Zeitplan.
        await saved.DeleteAsync(report.Id);
        Assert.Empty(await schedules.ListAsync());
    }

    private sealed class RecordingMail : Inventory.Core.Abstractions.IMailSender
    {
        public List<Inventory.Core.Abstractions.MailMessage> Sent { get; } = [];
        public string? Fail { get; set; }

        public Task SendAsync(Inventory.Core.Abstractions.MailServer server, Inventory.Core.Abstractions.MailMessage message, CancellationToken ct = default)
        {
            if (Fail is not null)
                throw new Inventory.Core.Abstractions.RemoteException(Fail);
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class NoWebhook : Inventory.Core.Abstractions.IWebhookSender
    {
        public Task PostAsync(string url, string json, CancellationToken ct = default) => Task.CompletedTask;
    }
}
