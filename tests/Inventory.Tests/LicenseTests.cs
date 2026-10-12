using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

public class LicenseTests
{
    private static async Task<TestDb> SeedAsync()
    {
        var db = new TestDb();
        var computers = await new ComputerService(db, new SimulatedHostProbe()).ImportAsync("pc-01 pc-02 pc-03 pc-04");
        await using var context = db.CreateDbContext();
        void Install(int index, string name, string? version = null) => context.Software.Add(new ComputerSoftware { ComputerId = computers[index].Id, Name = name, Version = version });
        Install(0, "Acrobat Pro DC", "24.1");
        Install(0, "Acrobat Pro DC (64-bit)", "24.1");
        Install(1, "Acrobat Pro DC", "23.0");
        Install(2, "Acrobat Reader");
        Install(2, "Microsoft Office Standard 2021 - de-de");
        Install(3, "Microsoft Office Professional Plus 2021 - de-de");
        Install(3, "Microsoft 365 Apps for enterprise - de-de");
        await context.SaveChangesAsync();
        return db;
    }

    [Theory]
    [InlineData("acrobat pro", "Acrobat Pro DC (64-bit)", true)]
    [InlineData("Acrobat Pro", "Acrobat Reader", false)]
    [InlineData("Microsoft Office * 2021*", "Microsoft Office Standard 2021 - de-de", true)]
    [InlineData("Microsoft Office * 2021*", "Microsoft 365 Apps for enterprise - de-de", false)]
    [InlineData("Acrobat*", "Adobe Acrobat", false)]
    [InlineData("a.c (x)", "Xa.c (x)Y", true)]
    [InlineData("  ", "anything", false)]
    public void A_pattern_is_a_fragment_or_with_a_star_the_whole_name(string pattern, string program, bool matches) =>
        Assert.Equal(matches, SoftwareLicense.Matches(pattern, program));

    [Fact]
    public async Task Installations_are_counted_per_computer_and_compared_with_the_licences_owned()
    {
        using var db = await SeedAsync();
        var licenses = new LicenseService(db);
        await licenses.SaveAsync(new SoftwareLicense { Name = "Acrobat Pro", Pattern = "Acrobat Pro", Owned = 1, CostPerLicense = 250 });
        await licenses.SaveAsync(new SoftwareLicense { Name = "Office 2021", Pattern = "Microsoft Office * 2021*", Owned = 5 });
        await licenses.SaveAsync(new SoftwareLicense { Name = "Visio", Pattern = "Visio", Owned = 2 });

        var usage = await licenses.UsageAsync();

        var acrobat = usage[0];
        // Zwei Einträge auf pc-01 verbrauchen eine Lizenz; pc-02 die zweite.
        Assert.Equal((2, 1, true), (acrobat.Installed, acrobat.Missing, acrobat.Exceeded));
        Assert.Equal(["pc-01", "pc-02"], acrobat.Computers);
        Assert.Equal(["Acrobat Pro DC", "Acrobat Pro DC (64-bit)"], acrobat.Programs);
        Assert.Equal((2, -3, false), (usage[1].Installed, usage[1].Missing, usage[1].Exceeded));
        Assert.Equal((0, -2), (usage[2].Installed, usage[2].Missing));
        Assert.Empty(usage[2].Programs);

        await Assert.ThrowsAsync<ArgumentException>(() => licenses.SaveAsync(new SoftwareLicense { Name = "X", Pattern = " * " }));
        await Assert.ThrowsAsync<ArgumentException>(() => licenses.SaveAsync(new SoftwareLicense { Name = " ", Pattern = "x" }));
        var changed = await licenses.SaveAsync(new SoftwareLicense { Id = acrobat.License.Id, Name = "Acrobat Pro", Pattern = "Acrobat Pro", Owned = -4, CostPerLicense = -1 });
        Assert.Equal((0, (decimal?)null), (changed.Owned, changed.CostPerLicense));
        await licenses.DeleteAsync(changed.Id);
        Assert.Equal(2, (await licenses.UsageAsync()).Count);
    }

    [Fact]
    public async Task An_alert_rule_reports_a_licence_when_it_becomes_exceeded()
    {
        using var db = await SeedAsync();
        var clock = new Clock(new DateTimeOffset(2040, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var webhook = new RecordingWebhook();
        var licenses = new LicenseService(db);
        var notifications = new NotificationService(db, new ReversingProtector(), new CredentialService(db, new ReversingProtector()), new NoMail(), webhook, clock);
        var alerts = new AlertService(db, new CollectionService(db), notifications, new AlertQueue(), new SettingsService(db), clock, licenses: licenses);
        await notifications.SaveAsync(new NotificationChannel { Name = "Chat", Kind = NotificationKind.Webhook }, "https://hooks.example.test/x");
        var acrobat = await licenses.SaveAsync(new SoftwareLicense { Name = "Acrobat Pro", Pattern = "Acrobat Pro", Owned = 1 });
        var office = await licenses.SaveAsync(new SoftwareLicense { Name = "Office 2021", Pattern = "Microsoft Office * 2021*", Owned = 2 });
        await alerts.SaveAsync(new AlertRule { Name = "Lizenzen", Kind = AlertKind.LicenseExceeded }, []);

        async Task<int> NextAsync()
        {
            clock.Advance(AlertService.PollInterval);
            return await alerts.EvaluateAsync();
        }

        // Acrobat ist beim Anlegen der Regel schon überschritten: Ausgangsstand.
        Assert.Equal(0, await NextAsync());
        await licenses.SaveAsync(new SoftwareLicense { Id = office.Id, Name = office.Name, Pattern = office.Pattern, Owned = 1 });
        Assert.Equal(1, await NextAsync());
        Assert.Equal(0, await NextAsync());
        Assert.Contains("Office 2021", webhook.Sent.Single());
        Assert.Contains("2 installed, 1 owned", webhook.Sent.Single());

        // Nachgekauft und später wieder überschritten: erneut gemeldet.
        await licenses.SaveAsync(new SoftwareLicense { Id = acrobat.Id, Name = acrobat.Name, Pattern = acrobat.Pattern, Owned = 5 });
        Assert.Equal(0, await NextAsync());
        await licenses.SaveAsync(new SoftwareLicense { Id = acrobat.Id, Name = acrobat.Name, Pattern = acrobat.Pattern, Owned = 1 });
        Assert.Equal(1, await NextAsync());
    }

    [Fact]
    public async Task A_licence_whose_message_was_not_delivered_is_reported_again_while_delivered_ones_are_not()
    {
        using var db = await SeedAsync();
        var clock = new Clock(new DateTimeOffset(2040, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var webhook = new RecordingWebhook();
        var licenses = new LicenseService(db);
        var notifications = new NotificationService(db, new ReversingProtector(), new CredentialService(db, new ReversingProtector()), new NoMail(), webhook, clock);
        var alerts = new AlertService(db, new CollectionService(db), notifications, new AlertQueue(), new SettingsService(db), clock, licenses: licenses);
        await notifications.SaveAsync(new NotificationChannel { Name = "Chat", Kind = NotificationKind.Webhook }, "https://hooks.example.test/x");
        var acrobat = await licenses.SaveAsync(new SoftwareLicense { Name = "Acrobat Pro", Pattern = "Acrobat Pro", Owned = 9 });
        var office = await licenses.SaveAsync(new SoftwareLicense { Name = "Office 2021", Pattern = "Microsoft Office * 2021*", Owned = 9 });
        await alerts.SaveAsync(new AlertRule { Name = "Lizenzen", Kind = AlertKind.LicenseExceeded }, []);
        clock.Advance(AlertService.PollInterval);
        await alerts.EvaluateAsync();

        // Beide werden im selben Durchlauf überschritten; die Nachricht zu Office kommt nicht an.
        await licenses.SaveAsync(new SoftwareLicense { Id = acrobat.Id, Name = acrobat.Name, Pattern = acrobat.Pattern, Owned = 1 });
        await licenses.SaveAsync(new SoftwareLicense { Id = office.Id, Name = office.Name, Pattern = office.Pattern, Owned = 1 });
        webhook.FailWhenContains = "Office 2021";
        clock.Advance(AlertService.PollInterval);
        Assert.Equal(1, await alerts.EvaluateAsync());

        webhook.FailWhenContains = null;
        clock.Advance(AlertService.PollInterval);
        Assert.Equal(1, await alerts.EvaluateAsync());
        clock.Advance(AlertService.PollInterval);
        Assert.Equal(0, await alerts.EvaluateAsync());
        Assert.Equal(1, webhook.Sent.Count(json => json.Contains("Acrobat Pro")));
        Assert.Equal(1, webhook.Sent.Count(json => json.Contains("Office 2021")));
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
        public List<string> Sent { get; } = [];
        public string? FailWhenContains { get; set; }

        public Task PostAsync(string url, string json, CancellationToken ct = default)
        {
            if (FailWhenContains is not null && json.Contains(FailWhenContains))
                throw new RemoteException("HTTP 503");
            Sent.Add(json);
            return Task.CompletedTask;
        }
    }
}
