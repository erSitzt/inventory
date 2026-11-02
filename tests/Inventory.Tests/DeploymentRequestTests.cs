using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>Pakete von KI-Assistenten: gekennzeichnet, nie freigegeben, Verteilen nur über eine Anfrage, die ein Mensch freigibt.</summary>
public sealed class DeploymentRequestTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), $"inventory-requests-{Guid.NewGuid():N}");
    private readonly PackageService _packages;
    private readonly DeploymentService _deployments;
    private readonly DeploymentRequestService _requests;
    private readonly ComputerService _computers;
    private readonly List<AlertEvent> _alerts = [];

    private sealed class Alerts(List<AlertEvent> raised) : IAlertSink
    {
        public void Raise(AlertEvent alert) => raised.Add(alert);
    }

    public DeploymentRequestTests()
    {
        _computers = new ComputerService(_db, new SimulatedHostProbe());
        _packages = new PackageService(_db, new PackageRepository(_repoDir));
        _deployments = new DeploymentService(_db, _packages,
            new RemoteJobService([new SimulatedTransport(TransportKind.SmbService)], _computers, new CredentialService(_db, new ReversingProtector()), new SettingsService(_db)),
            new InventoryStore(_db), new SimulatedHostProbe());
        _requests = new DeploymentRequestService(_db, _packages, _deployments, alerts: new Alerts(_alerts));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_repoDir))
            Directory.Delete(_repoDir, recursive: true);
    }

    private static readonly AiStep Script = new(StepType.PowerShell, "Restart spooler", "Restart-Service Spooler");

    [Fact]
    public async Task An_ai_package_is_marked_unapproved_and_limited_to_safe_step_types()
    {
        var package = await _requests.CreatePackageAsync("Spooler fix", "1.0", "Restarts the spooler", [Script], "API token \"build\" (anna)");
        Assert.Equal(("MCP: Spooler fix", true, false), (package.Name, package.IsAiGenerated, package.IsApproved));
        Assert.Equal("API token \"build\" (anna)", package.AiCreatedBy);
        Assert.Equal("Restart-Service Spooler", Assert.Single(package.Steps).Definition.Script);

        await Assert.ThrowsAsync<ArgumentException>(() => _requests.CreatePackageAsync("Registry", null, null, [new AiStep(StepType.Registry)], "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => _requests.CreatePackageAsync("Empty script", null, null, [new AiStep(StepType.PowerShell)], "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => _requests.CreatePackageAsync("Install", null, null, [new AiStep(StepType.Install)], "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => _requests.CreatePackageAsync("None", null, null, [], "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _requests.CreatePackageAsync("Spooler fix", null, null, [Script], "x"));

        // Pakete von Menschen bleiben für Assistenten unantastbar.
        var human = await _packages.SaveAsync(null, "Office", null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _requests.SetStepsAsync(human.Id, [Script]));
    }

    [Fact]
    public async Task A_request_starts_only_after_approval_and_only_for_the_reviewed_content()
    {
        var computers = await _computers.ImportAsync("srv-01 srv-02");
        var package = await _requests.CreatePackageAsync("Spooler fix", null, null, [Script], "build");
        var request = await _requests.RequestAsync(package.Id, [.. computers.Select(c => c.Id)], "build", "Ticket 4711");
        Assert.Equal(DeploymentRequestStatus.Pending, request.Status);
        Assert.Equal(AlertKind.ApprovalRequested, Assert.Single(_alerts).Kind);
        Assert.Empty(await _deployments.ListAsync());

        // Der Administrator hat einen älteren Stand gesehen: nichts wird freigegeben.
        var reviewed = (await _packages.GetAsync(package.Id))!.ModifiedUtc;
        await Task.Delay(10);
        await _requests.SetStepsAsync(package.Id, [Script with { Script = "Stop-Computer -Force" }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _requests.ApproveAsync(request.Id, "admin", reviewed));
        Assert.False((await _packages.GetAsync(package.Id))!.IsApproved);

        var current = (await _packages.GetAsync(package.Id))!.ModifiedUtc;
        var deployment = await _requests.ApproveAsync(request.Id, "admin", current);
        Assert.Equal(2, deployment.Targets.Count);
        var approved = (await _packages.GetAsync(package.Id))!;
        Assert.Equal((true, "admin"), (approved.IsApproved, approved.ApprovedBy));
        var decided = (await _requests.GetAsync(request.Id))!;
        Assert.Equal((DeploymentRequestStatus.Started, "admin", deployment.Id), (decided.Status, decided.DecidedBy, decided.DeploymentId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _requests.ApproveAsync(request.Id, "admin", current));

        // Jede spätere Änderung zieht die Freigabe zurück.
        await _requests.SetStepsAsync(package.Id, [Script]);
        Assert.False((await _packages.GetAsync(package.Id))!.IsApproved);
    }

    [Fact]
    public async Task A_rejected_request_never_starts()
    {
        var computers = await _computers.ImportAsync("srv-01");
        var package = await _requests.CreatePackageAsync("Spooler fix", null, null, [Script], "build");
        var request = await _requests.RequestAsync(package.Id, [computers[0].Id], "build", null);
        await _requests.RejectAsync(request.Id, "admin", "  not on production  ");
        var rejected = (await _requests.GetAsync(request.Id))!;
        Assert.Equal((DeploymentRequestStatus.Rejected, "not on production"), (rejected.Status, rejected.Note));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _requests.ApproveAsync(request.Id, "admin", package.ModifiedUtc));
        Assert.Empty(await _requests.ListAsync(pendingOnly: true));
        Assert.Empty(await _deployments.ListAsync());

        // Mit dem Paket verschwinden auch seine Anfragen.
        await _packages.DeleteAsync(package.Id);
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.DeploymentRequests.ToListAsync());
    }
}
