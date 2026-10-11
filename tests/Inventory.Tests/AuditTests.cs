using System.Security.Claims;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Localization;

namespace Inventory.Tests;

public class AuditTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AuditService _audit;

    public AuditTests() => _audit = new AuditService(_db);

    public void Dispose() => _db.Dispose();

    private async Task AddAsync(string user, string action, string? details, DateTime timeUtc)
    {
        await using var db = _db.CreateDbContext();
        db.AuditEntries.Add(new AuditEntry { User = user, Action = action, Details = details, TimeUtc = timeUtc });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Entry_keeps_role_and_source_of_the_actor()
    {
        await _audit.LogAsync(new AuditActor("anna", AccessRole.Operator, AuditSource.Web), "Deployment started", "#1");
        await _audit.LogAsync("old caller", "Something");
        await _audit.LogAsync(new AuditActor("nobody", AccessRole.None, AuditSource.Api), "Something");

        var entries = await _audit.ListAsync();

        Assert.Equal(AccessRole.Operator, entries[2].Role);
        Assert.Equal(AuditSource.Web, entries[2].Source);
        Assert.Null(entries[1].Role);
        Assert.Equal(AuditSource.Unknown, entries[1].Source);
        // "Keine Rolle" ist keine Rolle, die man im Protokoll ausweisen müsste.
        Assert.Null(entries[0].Role);
    }

    [Fact]
    public async Task Filter_narrows_by_user_action_period_and_text()
    {
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        await AddAsync("anna", "Signed in", "Windows", day.AddHours(8));
        await AddAsync("anna", "Package changed", "7-Zip 24 → 7-Zip 25", day.AddHours(9));
        await AddAsync("bert", "Package changed", "100%_fertig", day.AddDays(1));

        Assert.Equal(2, (await _audit.ListAsync(filter: new AuditFilter(User: "anna"))).Count);
        Assert.Equal(2, (await _audit.ListAsync(filter: new AuditFilter(Action: "Package changed"))).Count);
        Assert.Single(await _audit.ListAsync(filter: new AuditFilter(User: "anna", Action: "Package changed")));
        // Das Ende des Zeitraums gehört nicht mehr dazu.
        Assert.Equal(2, (await _audit.ListAsync(filter: new AuditFilter(FromUtc: day, ToUtc: day.AddDays(1)))).Count);
        Assert.Single(await _audit.ListAsync(filter: new AuditFilter(Search: "7-zip")));
        // Platzhalter der Datenbank gelten wörtlich.
        Assert.Single(await _audit.ListAsync(filter: new AuditFilter(Search: "100%_")));
        Assert.Empty(await _audit.ListAsync(filter: new AuditFilter(Search: "1__%")));

        var (users, actions) = await _audit.FilterValuesAsync();
        Assert.Equal(["anna", "bert"], users);
        Assert.Equal(["Package changed", "Signed in"], actions);
    }

    [Fact]
    public async Task Cleanup_removes_only_entries_older_than_the_retention()
    {
        await AddAsync("anna", "old", null, DateTime.UtcNow.AddDays(-800));
        await AddAsync("anna", "recent", null, DateTime.UtcNow.AddDays(-10));

        Assert.Equal(0, await _audit.CleanupAsync(0));
        Assert.Equal(2, (await _audit.ListAsync()).Count);

        Assert.Equal(1, await _audit.CleanupAsync(730));
        Assert.Equal("recent", Assert.Single(await _audit.ListAsync()).Action);
    }

    [Fact]
    public async Task Retention_setting_defaults_to_two_years_and_allows_unlimited()
    {
        var settings = new SettingsService(_db);
        Assert.Equal(730, await settings.GetAuditRetentionDaysAsync());

        await settings.SetAuditRetentionDaysAsync(0);
        Assert.Equal(0, await settings.GetAuditRetentionDaysAsync());

        await settings.SetAuditRetentionDaysAsync(1_000_000);
        Assert.Equal(SettingsService.MaxAuditRetentionDays, await settings.GetAuditRetentionDaysAsync());
    }

    [Fact]
    public async Task Csv_contains_header_and_filtered_rows()
    {
        await _audit.LogAsync(new AuditActor("anna", AccessRole.Admin, AuditSource.ApiToken), "Package approved", "7-Zip; \"x64\"");
        await _audit.LogAsync("bert", "Signed in");

        var csv = await _audit.CsvAsync(new PassThroughLocalizer(), new AuditFilter(User: "anna"));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Time;User;Role;Source;Action;Details", lines[0]);
        Assert.Contains(";anna;Admin;ApiToken;Package approved;\"7-Zip; \"\"x64\"\"\"", lines[1]);
    }

    [Fact]
    public void Actor_reflects_role_and_marks_api_tokens()
    {
        var session = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "anna"), new Claim(AccessControl.RoleClaim, nameof(AccessRole.Operator))], "test"));
        var token = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "API token \"ci\" (anna)"), new Claim(AccessControl.AccessClaim, "api-token"), new Claim(AccessControl.RoleClaim, nameof(AccessRole.Admin))], "test"));

        Assert.Equal(new AuditActor("anna", AccessRole.Operator, AuditSource.Web), AuditActors.Actor(session, AuditSource.Web));
        Assert.Equal(AuditSource.ApiToken, AuditActors.Actor(token, AuditSource.Api).Source);
        Assert.Equal(new AuditActor("(nobody)", null, AuditSource.Api), AuditActors.Actor(new ClaimsPrincipal(new ClaimsIdentity()), AuditSource.Api, "(nobody)"));
    }

    [Fact]
    public void Direct_windows_sign_in_is_logged_once_per_window()
    {
        var time = new Clock(new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero));
        var log = new SignInLog(_audit, time, NullLogger<SignInLog>.Instance);

        Assert.True(log.FirstInWindow(@"DOM\anna", allowed: true));
        Assert.False(log.FirstInWindow(@"dom\ANNA", allowed: true));
        // Eine abgelehnte Anmeldung ist ein eigenes Ereignis.
        Assert.True(log.FirstInWindow(@"DOM\anna", allowed: false));
        Assert.True(log.FirstInWindow(@"DOM\bert", allowed: true));

        time.Advance(SignInLog.Window);
        Assert.True(log.FirstInWindow(@"DOM\anna", allowed: true));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Gibt den englischen Quelltext unverändert zurück.</summary>
    private sealed class PassThroughLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
