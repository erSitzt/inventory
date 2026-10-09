using System.Security.Cryptography;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("inventory-update-").FullName;
    private readonly FakeSource _source = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly List<(string Setup, string Log)> _launched = [];

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private UpdateService Create(string current = "1.0.0", string? cannotInstall = null, Func<string, string?>? verify = null)
    {
        var settings = new SettingsService(_db);
        var computers = new ComputerService(_db, new SimulatedHostProbe());
        var jobs = new RemoteJobService([], computers, new CredentialService(_db, new ReversingProtector()), settings);
        var deployments = new DeploymentService(_db, new PackageService(_db, new PackageRepository(Path.Combine(_dir, "repo"))), jobs, new InventoryStore(_db), new SimulatedHostProbe());
        return new UpdateService(_source, settings, new BackupService(_db, Path.Combine(_dir, "backups"), _clock), deployments, new AuditService(_db), _clock,
            new UpdateOptions
            {
                CurrentVersion = current, UpdatesDirectory = Path.Combine(_dir, "updates"), CannotInstallReason = cannotInstall,
                Launch = (setup, log) => _launched.Add((setup, log)), VerifySignature = verify,
            });
    }

    [Theory]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("1.2.0-rc1", "1.2.0", -1)]
    [InlineData("1.2.0", "1.2.0-rc2", 1)]
    [InlineData("1.2.0-rc1", "1.2.0-rc2", -1)]
    [InlineData("v1.2.0", "1.2.0+abc123", 0)]
    public void Versions_are_ordered_with_prereleases_before_the_final_release(string left, string right, int expected)
    {
        Assert.Equal(expected, SemVer.Compare(left, right));
    }

    [Fact]
    public void Release_json_is_read_including_setup_and_checksum_assets()
    {
        var release = GitHubReleaseSource.Parse("""
            {"tag_name":"v1.3.0","html_url":"https://example.test/r","body":"Neu: X","published_at":"2026-10-01T10:00:00Z",
             "assets":[{"name":"Inventory-1.3.0-win-x64.zip","browser_download_url":"https://example.test/zip"},
                       {"name":"Inventory-Setup-1.3.0.exe","browser_download_url":"https://example.test/setup"},
                       {"name":"SHA256SUMS.txt","browser_download_url":"https://example.test/sums"}]}
            """);

        Assert.Equal(("1.3.0", "Inventory-Setup-1.3.0.exe", "https://example.test/setup", "https://example.test/sums"),
            (release.Version, release.SetupName, release.SetupUrl, release.ChecksumsUrl));
    }

    [Fact]
    public async Task Newer_release_is_offered_but_not_for_development_builds_or_older_releases()
    {
        _source.Publish("1.1.0");

        var updates = Create("1.0.0");
        await updates.CheckAsync();
        Assert.True(updates.UpdateAvailable);

        var same = Create("1.1.0");
        await same.CheckAsync();
        Assert.False(same.UpdateAvailable);

        var dev = Create("0.0.0-dev");
        await dev.CheckAsync();
        Assert.False(dev.UpdateAvailable);
        Assert.Null(dev.Latest);

        // Ohne Zwang wird nicht bei jedem Aufruf erneut gefragt.
        await updates.CheckAsync();
        Assert.Equal(1 + 1, _source.Requests);
    }

    [Fact]
    public async Task Apply_verifies_backs_up_logs_and_launches_the_setup()
    {
        _source.Publish("1.1.0");
        var updates = Create();
        await updates.CheckAsync();

        Assert.Null(await updates.ApplyAsync(@"CORP\anna"));

        var (setup, log) = Assert.Single(_launched);
        Assert.Equal("Inventory-Setup-1.1.0.exe", Path.GetFileName(setup));
        Assert.True(File.Exists(setup));
        Assert.EndsWith("setup-1.1.0.log", log);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "backups")));
        Assert.Contains(await new AuditService(_db).ListAsync(), e => e is { Action: "Update started", Details: "1.0.0 → 1.1.0" });

        // Nach dem Neustart mit der neuen Version wird der Erfolg festgehalten.
        var restarted = Create("1.1.0");
        await restarted.ReportPreviousAsync(startup: true);
        Assert.Contains(await new AuditService(_db).ListAsync(), e => e.Action == "Update completed");
        Assert.Null(restarted.LastError);
    }

    [Fact]
    public async Task Tampered_download_or_wrong_signature_is_discarded_and_nothing_is_launched()
    {
        _source.Publish("1.1.0");
        _source.CorruptSetup = true;
        var updates = Create();
        await updates.CheckAsync();

        Assert.Contains("checksum", await updates.ApplyAsync("anna"));
        Assert.Empty(_launched);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "updates"), "*.exe"));

        _source.CorruptSetup = false;
        var strict = Create(verify: _ => "Falscher Herausgeber");
        await strict.CheckAsync();
        Assert.Equal("Falscher Herausgeber", await strict.ApplyAsync("anna"));
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task Apply_is_refused_when_the_server_cannot_install_or_the_release_has_no_checksums()
    {
        _source.Publish("1.1.0");
        var locked = Create(cannotInstall: "Das Dienstkonto hat keine Administratorrechte.");
        await locked.CheckAsync();
        Assert.Equal("Das Dienstkonto hat keine Administratorrechte.", await locked.ApplyAsync("anna"));

        _source.Publish("1.2.0", withChecksums: false);
        var updates = Create();
        await updates.CheckAsync();
        Assert.Contains("no checksums", await updates.ApplyAsync("anna"));
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task Automatic_update_runs_only_inside_the_window_and_tries_each_version_once()
    {
        _source.Publish("1.1.0");
        var updates = Create();
        await updates.CheckAsync();

        Assert.False(await updates.ApplyIfDueAsync());
        await updates.SaveSettingsAsync(automatic: true, windowStart: new TimeSpan(23, 0, 0));
        Assert.False(await updates.ApplyIfDueAsync());

        _clock.Now = new DateTimeOffset(2026, 10, 9, 0, 30, 0, TimeSpan.Zero);
        Assert.True(await updates.ApplyIfDueAsync());
        Assert.Single(_launched);

        // Das Setup ist gescheitert (es läuft weiter 1.0.0): kein zweiter automatischer Versuch für dieselbe Version.
        var again = Create();
        await again.CheckAsync();
        await again.ReportPreviousAsync(startup: true);
        Assert.Contains("failed", again.LastError);
        Assert.False(await again.ApplyIfDueAsync());
        Assert.Single(_launched);
    }

    [Fact]
    public void Checksum_list_is_matched_by_file_name()
    {
        var file = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant();

        Assert.Null(UpdateService.VerifyChecksum(file, $"0000  b.bin\n{hash}  a.bin\n"));
        Assert.Null(UpdateService.VerifyChecksum(file, $"{hash.ToUpperInvariant()} *a.bin\r\n"));
        Assert.NotNull(UpdateService.VerifyChecksum(file, $"{hash[..^1]}0  a.bin"));
        Assert.NotNull(UpdateService.VerifyChecksum(file, $"{hash}  b.bin"));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeSource : IReleaseSource
    {
        private static readonly byte[] SetupBytes = [77, 90, 1, 2, 3];
        private ReleaseInfo? _release;
        public int Requests { get; private set; }
        public bool CorruptSetup { get; set; }

        public void Publish(string version, bool withChecksums = true) => _release = new ReleaseInfo(version, "Notizen", "https://example.test/r", null,
            $"Inventory-Setup-{version}.exe", "setup", withChecksums ? "sums" : null);

        public Task<ReleaseInfo?> GetLatestAsync(string repository, CancellationToken ct = default)
        {
            Requests++;
            return Task.FromResult(_release);
        }

        public Task DownloadAsync(string url, string path, CancellationToken ct = default)
        {
            if (url == "sums")
                File.WriteAllText(path, $"{Convert.ToHexString(SHA256.HashData(SetupBytes)).ToLowerInvariant()}  {_release!.SetupName}\n");
            else
                File.WriteAllBytes(path, CorruptSetup ? [.. SetupBytes, 9] : SetupBytes);
            return Task.CompletedTask;
        }
    }
}
