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

        // Zeitangaben erscheinen in der Zeitzone des Lesers (hier fest UTC+5:30, ohne Sommerzeit).
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+0530", TimeSpan.FromMinutes(330), "Test", "Test");
        var utc = (await reports.TableAsync(new ReportQuery(ReportSource.Deployments, Columns: ["started"], TimeZone: TimeZoneInfo.Utc))).Rows.Single()[0]!;
        var local = (await reports.TableAsync(new ReportQuery(ReportSource.Deployments, Columns: ["started"], TimeZone: zone))).Rows.Single()[0]!;
        Assert.Equal(TimeSpan.FromMinutes(330), DateTime.ParseExact(local, "yyyy-MM-dd HH:mm", null) - DateTime.ParseExact(utc, "yyyy-MM-dd HH:mm", null));

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
    public async Task The_pdf_repeats_the_header_and_continues_wide_tables_on_further_pages()
    {
        var (db, _, _) = await SeedAsync();
        using var _d = db;
        var reports = new ReportService(db);

        var narrow = await reports.TableAsync(new ReportQuery(ReportSource.Computers, Columns: ["name", "memoryGb", "location", "notes"]));
        narrow.Rows[1][3] = "Umlaute äöüß – „Zitat“ € und ein Zeichen außerhalb: 😀";
        var bytes = ReportPdf.Write(narrow, "Hardware: Büro", "Rechner · 3 Zeilen", "Seite {0} von {1}");
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes))
        {
            Assert.Equal(1, pdf.NumberOfPages);
            var text = pdf.GetPage(1).Text;
            Assert.Contains("Hardware: Büro", text);
            Assert.Contains("Memory (GB)", text);
            Assert.Contains("pc-01", text);
            Assert.Contains("äöüß – „Zitat“ €", text);
            Assert.Contains("Seite 1 von 1", text);
            Assert.Equal((842, 595), ((int)Math.Round(pdf.GetPage(1).Width), (int)Math.Round(pdf.GetPage(1).Height)));
        }

        // Alle Spalten passen nicht nebeneinander: weitere Seiten, jeweils mit dem Namen vorn.
        var wide = await reports.TableAsync(new ReportQuery(ReportSource.Computers));
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(ReportPdf.Write(wide, "Alles", "")))
        {
            Assert.True(pdf.NumberOfPages > 1);
            Assert.All(pdf.GetPages(), page => Assert.Contains("pc-01", page.Text));
            Assert.Contains("Notes", pdf.GetPage(pdf.NumberOfPages).Text);
            Assert.Contains($"Page {pdf.NumberOfPages} of {pdf.NumberOfPages}", pdf.GetPage(pdf.NumberOfPages).Text);
        }

        // Viele Zeilen: mehrere Seiten mit Kopfzeile, und ab der Obergrenze ein Hinweis statt tausender Seiten.
        var many = new ReportTable([new ReportColumn("n", "Nummer", true), new ReportColumn("t", "Text")],
            Enumerable.Range(1, ReportPdf.MaxRows + 10).Select(i => new string?[] { i.ToString(), $"Zeile {i}" }).ToList());
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(ReportPdf.Write(many, "Viele", "Stand heute · " + new string('x', 400))))
        {
            Assert.True(pdf.NumberOfPages > 50);
            Assert.All(pdf.GetPages().Take(5), page => Assert.Contains("Nummer", page.Text));
            Assert.Contains($"First {ReportPdf.MaxRows} of {ReportPdf.MaxRows + 10} rows.", pdf.GetPage(1).Text);
            Assert.DoesNotContain($"Zeile {ReportPdf.MaxRows + 1}", pdf.GetPage(pdf.NumberOfPages).Text);
        }

        using var empty = UglyToad.PdfPig.PdfDocument.Open(ReportPdf.Write(new ReportTable([], []), "Leer", ""));
        Assert.Equal(1, empty.NumberOfPages);
        Assert.Equal("Sehr lange…", ReportPdf.Fit("Sehr langer Text, der nicht passt", 45, 7.5, bold: false));

        if (Environment.GetEnvironmentVariable("INVENTORY_PDF_OUT") is { Length: > 0 } directory)
        {
            File.WriteAllBytes(Path.Combine(directory, "narrow.pdf"), bytes);
            File.WriteAllBytes(Path.Combine(directory, "wide.pdf"), ReportPdf.Write(wide, "Alles", "Rechner · alle Spalten"));
        }
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

        // Als PDF: derselbe Bericht, anderes Format des Anhangs.
        schedule.ReportFormat = ReportFormat.Pdf;
        await schedules.SaveAsync(schedule);
        await schedules.RunNowAsync(schedule.Id);
        var pdfMail = mail.Sent[^1].Attachment!;
        Assert.Matches(@"^Hardware \d{4}-\d{2}-\d{2}\.pdf$", pdfMail.FileName);
        Assert.Equal("application/pdf", pdfMail.ContentType);
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfMail.Content))
        {
            Assert.Contains("Computers", pdf.GetPage(1).Text);
            Assert.Contains("3 rows", pdf.GetPage(1).Text);
        }
        mail.Sent.RemoveAt(mail.Sent.Count - 1);

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

    [Fact]
    public async Task Example_reports_are_created_once_use_only_known_columns_and_are_not_restored_after_deletion()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var reports = new ReportService(db);
        var saved = new SavedReportService(db, reports);
        var examples = new ExampleData(db, settings);

        // Ein eigener Bericht – auch einer, der wie ein Beispiel heißt – hindert die übrigen Beispiele nicht.
        await saved.SaveAsync(new Report { Name = "Eigener", Source = ReportSource.Software });
        await saved.SaveAsync(new Report { Name = ExampleData.Prefix + "Patch status", Source = ReportSource.Changes });
        Assert.Equal(10, await examples.SeedReportsAsync());
        Assert.Equal(0, await examples.SeedReportsAsync());
        var stored = await saved.ListAsync();
        Assert.Equal(12, stored.Count);
        Assert.Equal(ReportSource.Changes, stored.Single(r => r.Name == ExampleData.Prefix + "Patch status").Source);
        Assert.All(stored, report =>
        {
            var known = reports.ColumnsOf(report.Source).Select(c => c.Key).ToHashSet();
            Assert.All(report.ColumnKeys, key => Assert.Contains(key, known));
            Assert.True(report.SortColumn is null || known.Contains(report.SortColumn));
        });
        // Jedes Beispiel läuft, auch auf einem leeren Bestand, und lässt sich in jedes Format bringen.
        foreach (var report in stored)
        {
            foreach (var format in Enum.GetValues<ReportFormat>())
                Assert.NotEmpty((await saved.ExportAsync(report, format, DateTimeOffset.Now)).Content);
        }

        foreach (var report in stored)
            await saved.DeleteAsync(report.Id);
        Assert.Equal(0, await new ExampleData(db, settings).SeedReportsAsync());
        Assert.Empty(await saved.ListAsync());
    }

    [Fact]
    public async Task Existing_installations_get_the_decommissioned_report_once_with_its_collection()
    {
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var reports = new ReportService(db);
        var saved = new SavedReportService(db, reports);
        var collections = new CollectionService(db);
        // Stand vor diesem Bericht: die ersten Beispiele sind längst angelegt (und womöglich gelöscht).
        await settings.SetAsync(SettingKeys.ExampleReportsCreated, bool.TrueString);
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var added = await computers.ImportAsync("pc-old pc-gone pc-fine");
        await using (var context = db.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var old = context.Computers.Single(c => c.Name == "pc-old");
            (old.DirectoryInactive, old.DirectoryLastLogonUtc) = (true, now.AddDays(-200));
            // Nicht mehr im AD und ohne Anmeldewert, aber vor 300 Tagen zuletzt gesehen: am längsten still, steht also vorn.
            var gone = context.Computers.Single(c => c.Name == "pc-gone");
            (gone.DirectoryMissingSinceUtc, gone.LastSeenUtc) = (now.AddDays(-3), now.AddDays(-300));
            context.Computers.Single(c => c.Name == "pc-fine").DirectoryLastLogonUtc = now.AddDays(-1);
            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await new ExampleData(db, settings).SeedReportsAsync());
        Assert.Equal(0, await new ExampleData(db, settings).SeedReportsAsync());
        var report = Assert.Single(await saved.ListAsync());
        Assert.Equal(ExampleData.Prefix + "Possibly decommissioned", report.Name);
        var (collection, members) = Assert.Single(await collections.ListAsync());
        Assert.Equal(collection.Id, report.CollectionId);
        Assert.True(collection.IsDynamic);
        Assert.Equal(2, members);

        var table = await reports.TableAsync(new ReportQuery(report.Source, report.CollectionId, report.Days, report.ColumnKeys, report.SortColumn, report.SortDescending));
        Assert.Equal(["pc-gone", "pc-old"], table.Rows.Select(r => r[0]));
        Assert.Equal(["no", "yes"], table.Rows.Select(r => r[1]));
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
