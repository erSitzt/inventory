using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Remoting.Smb;

namespace Inventory.Tests;

public class FrameworkInstallTests
{
    [Fact]
    public async Task Installs_over_the_address_with_the_resolved_credential()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var settings = new SettingsService(db);
        var installer = new RecordingInstaller();
        var service = new FrameworkInstallService(computers, credentials, settings, new FixedSource("C:\\netfx\\ndp48.exe"), installer);
        var computer = (await computers.ImportAsync("pc-01"))[0];
        var credential = await credentials.SaveAsync(null, "Domain admin", @"CORP\deploy", "secret", null);
        await credentials.SetDefaultAsync(credential.Id);

        var result = await service.InstallAsync(computer.Id);

        Assert.True(result.Success);
        Assert.Equal(("pc-01", @"CORP\deploy", "C:\\netfx\\ndp48.exe"), (installer.Target!.Host, installer.Target.Credential?.UserName, installer.Installer));
    }

    [Fact]
    public async Task Problems_end_up_in_the_result()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var credentials = new CredentialService(db, new ReversingProtector());
        var settings = new SettingsService(db);
        var computer = (await computers.ImportAsync("pc-01"))[0];
        var installer = new RecordingInstaller();

        // Ohne Download (kein Internet, Signatur ungültig) wird nichts angefasst.
        var noSource = new FrameworkInstallService(computers, credentials, settings, new FixedSource(null), installer);
        Assert.Equal((false, "no download"), ((await noSource.InstallAsync(computer.Id)).Success, (await noSource.InstallAsync(computer.Id)).Message));
        Assert.Null(installer.Target);

        var service = new FrameworkInstallService(computers, credentials, settings, new FixedSource("ndp48.exe"), installer);
        installer.Fail = "ADMIN$ unreachable";
        Assert.Equal("ADMIN$ unreachable", (await service.InstallAsync(computer.Id)).Message);
        Assert.False((await service.InstallAsync(999)).Success);

        await settings.SetAgentlessPolicyAsync(AgentlessPolicy.Never);
        installer.Target = null;
        Assert.Contains("switched off", (await service.InstallAsync(computer.Id)).Message);
        Assert.Null(installer.Target);

        // Ohne agentenlosen Zugriff (Server nicht unter Windows) gibt es die Aktion nicht.
        var unavailable = new FrameworkInstallService(computers, credentials, settings, new FixedSource("ndp48.exe"));
        Assert.False(unavailable.Available);
        Assert.False((await unavailable.InstallAsync(computer.Id)).Success);
    }

    [Theory]
    [InlineData(0, true, false, "installed")]
    [InlineData(3010, true, true, "restart")]
    [InlineData(1641, true, true, "restart")]
    [InlineData(5100, false, false, "requirements")]
    [InlineData(1602, false, false, "cancelled")]
    [InlineData(1603, false, false, "exit code 1603")]
    public void Installer_exit_codes_are_explained(int code, bool success, bool reboot, string text)
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, Inventory.Contracts.JobFiles.RunnerExe), "x");
#pragma warning disable CA1416
            var result = new SmbServiceTransport(new RunnerPackage(root)).Interpret(code, "4.8");
#pragma warning restore CA1416
            Assert.Equal((success, reboot), (result.Success, result.RebootRequired));
            Assert.Contains(text, result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedSource(string? path) : IFrameworkSource
    {
        public Task<string> GetInstallerAsync(CancellationToken ct) =>
            path is null ? throw new InvalidOperationException("no download") : Task.FromResult(path);
    }

    private sealed class RecordingInstaller : IFrameworkInstaller
    {
        public RemoteTarget? Target { get; set; }
        public string? Installer { get; private set; }
        public string? Fail { get; set; }

        public Task<FrameworkInstallResult> InstallAsync(RemoteTarget target, string installer, CancellationToken ct)
        {
            Target = target;
            Installer = installer;
            if (Fail is not null)
                throw new RemoteException(Fail);
            return Task.FromResult(new FrameworkInstallResult(true, false, "installed"));
        }
    }
}
