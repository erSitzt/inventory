using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Ein Schritt, wie ihn ein KI-Assistent für ein neues Paket beschreibt.</summary>
/// <param name="Type">PowerShell, Command, Install, Reboot oder Sleep.</param>
/// <param name="File">Install: Datei im Paket (z. B. setup.msi), vorher mit <see cref="PackageService.Repository"/> abgelegt.</param>
public sealed record AiStep(StepType Type, string? Name = null, string? Script = null, string? File = null, string? Arguments = null, int? TimeoutSeconds = null,
    StepRunAs RunAs = StepRunAs.System, bool ContinueOnError = false);

/// <summary>
/// Pakete und Deployments, die KI-Assistenten über MCP anlegen. Solche Pakete sind gekennzeichnet und nie freigegeben; will
/// ein Assistent ein nicht freigegebenes Paket verteilen, entsteht eine Anfrage, die ein angemeldeter Administrator in der
/// Oberfläche freigibt (Paket freigeben und Deployment starten) oder ablehnt. Ein freigegebenes, unverändertes Paket
/// verteilt der Assistent selbst.
/// </summary>
public sealed class DeploymentRequestService(IDbContextFactory<InventoryDbContext> dbFactory, PackageService packages, DeploymentService deployments,
    IStringLocalizer<SharedResource>? localizer = null, IAlertSink? alerts = null, TimeProvider? clock = null)
{
    /// <summary>Schritttypen, die ein Assistent anlegen darf; Dateikopie, Registry und Dienste bleiben der Oberfläche vorbehalten.</summary>
    public static readonly StepType[] AllowedSteps = [StepType.PowerShell, StepType.Command, StepType.Install, StepType.Reboot, StepType.Sleep];
    public const int MaxSteps = 50;
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Legt ein gekennzeichnetes, nicht freigegebenes Paket an; den Namen bekommt es mit dem Präfix "MCP: ".</summary>
    /// <exception cref="ArgumentException">Ungültige Schritte.</exception>
    /// <exception cref="InvalidOperationException">Ein Paket dieses Namens gibt es schon.</exception>
    public async Task<Package> CreatePackageAsync(string name, string? version, string? description, IReadOnlyList<AiStep> steps, string createdBy, CancellationToken ct = default)
    {
        Validate(steps);
        var fullName = name.Trim().StartsWith("MCP: ", StringComparison.OrdinalIgnoreCase) ? name.Trim() : $"MCP: {name.Trim()}";
        if (fullName.Length <= 5)
            throw new ArgumentException(L["A name is required."], nameof(name));
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            if (await db.Packages.AnyAsync(p => p.Name == fullName, ct))
                throw new InvalidOperationException(L["A package named \"{0}\" already exists.", fullName]);
        var package = await packages.SaveAsync(null, fullName, version, description, ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AiCreatedBy, createdBy), ct);
        await ReplaceStepsAsync(package.Id, steps, ct);
        return (await packages.GetAsync(package.Id, ct))!;
    }

    /// <summary>Ersetzt alle Schritte eines KI-Pakets; hebt damit eine Freigabe auf.</summary>
    /// <exception cref="InvalidOperationException">Das Paket hat ein Mensch angelegt; Assistenten ändern es nicht.</exception>
    public async Task SetStepsAsync(int packageId, IReadOnlyList<AiStep> steps, CancellationToken ct = default)
    {
        Validate(steps);
        await DemandAiPackageAsync(packageId, ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            foreach (var id in await db.PackageSteps.Where(s => s.PackageId == packageId).Select(s => s.Id).ToListAsync(ct))
                await packages.DeleteStepAsync(packageId, id, ct);
        await ReplaceStepsAsync(packageId, steps, ct);
    }

    private async Task ReplaceStepsAsync(int packageId, IReadOnlyList<AiStep> steps, CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            await packages.SaveStepAsync(packageId, null, new JobStep
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"{s.Type} {i + 1}" : s.Name.Trim(), Type = s.Type, Script = s.Script ?? "", File = s.File,
                Arguments = s.Arguments, TimeoutSeconds = Math.Clamp(s.TimeoutSeconds ?? 600, 10, 7200), RunAs = s.RunAs, ContinueOnError = s.ContinueOnError,
            }, null, ct);
        }
    }

    private void Validate(IReadOnlyList<AiStep> steps)
    {
        if (steps.Count is 0 or > MaxSteps)
            throw new ArgumentException(L["Between 1 and {0} steps.", MaxSteps]);
        foreach (var step in steps)
        {
            if (!AllowedSteps.Contains(step.Type))
                throw new ArgumentException(L["Step type {0} cannot be created by an AI assistant; allowed: {1}.", step.Type, string.Join(", ", AllowedSteps)]);
            if (step.Type is StepType.PowerShell or StepType.Command && string.IsNullOrWhiteSpace(step.Script))
                throw new ArgumentException(L["A {0} step needs a script.", step.Type]);
            if (step.Type == StepType.Install && string.IsNullOrWhiteSpace(step.File))
                throw new ArgumentException(L["An Install step needs a file of the package."]);
        }
    }

    /// <exception cref="InvalidOperationException">Kein KI-Paket.</exception>
    public async Task<Package> DemandAiPackageAsync(int packageId, CancellationToken ct = default)
    {
        var package = await packages.GetAsync(packageId, ct) ?? throw new KeyNotFoundException(L["Package {0} not found", packageId]);
        return package.IsAiGenerated ? package
            : throw new InvalidOperationException(L["\"{0}\" was created by a person; AI assistants can only change packages they created themselves.", package.Name]);
    }

    /// <summary>Fragt das Verteilen eines nicht freigegebenen Pakets an; meldet sich über die Alarme (falls eingerichtet).</summary>
    public async Task<DeploymentRequest> RequestAsync(int packageId, IReadOnlyCollection<int> computerIds, string requestedBy, string? reason,
        bool retryOffline = false, bool respectWindow = false, CancellationToken ct = default)
    {
        if (computerIds.Count == 0)
            throw new ArgumentException(L["No computers to deploy to."]);
        var package = await packages.GetAsync(packageId, ct) ?? throw new KeyNotFoundException(L["Package {0} not found", packageId]);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = new DeploymentRequest
        {
            PackageId = packageId, ComputerIds = [.. computerIds.Distinct()], RequestedBy = requestedBy, RequestedUtc = Now,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), RetryOffline = retryOffline, RespectWindow = respectWindow,
        };
        db.DeploymentRequests.Add(request);
        await db.SaveChangesAsync(ct);
        alerts?.Raise(new AlertEvent(AlertKind.ApprovalRequested, L["Deployment waiting for approval"],
            L["{0} wants to deploy \"{1}\" to {2} computers.", requestedBy, package.Name, request.ComputerIds.Count], "deployments"));
        return request;
    }

    public async Task<List<DeploymentRequest>> ListAsync(bool pendingOnly = false, int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentRequests.AsNoTracking().Include(r => r.Package)
            .Where(r => !pendingOnly || r.Status == DeploymentRequestStatus.Pending)
            .OrderByDescending(r => r.Id).Take(take).ToListAsync(ct);
    }

    public async Task<DeploymentRequest?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentRequests.AsNoTracking().Include(r => r.Package).FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>
    /// Gibt das Paket frei und startet das Deployment. <paramref name="reviewedModifiedUtc"/> ist der Stand, den der
    /// Administrator gesehen hat: hat sich das Paket seither geändert, wird nichts freigegeben.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nicht mehr offen, oder das Paket hat sich seit dem Ansehen geändert.</exception>
    public async Task<Deployment> ApproveAsync(int id, string user, DateTime reviewedModifiedUtc, CancellationToken ct = default)
    {
        DeploymentRequest request;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            request = await db.DeploymentRequests.Include(r => r.Package).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException(L["Request {0} not found", id]);
            if (request.Status != DeploymentRequestStatus.Pending)
                throw new InvalidOperationException(L["The request has already been decided."]);
            if (request.Package!.ModifiedUtc != reviewedModifiedUtc)
                throw new InvalidOperationException(L["The package has changed since you opened it. Please review it again."]);
        }
        if (!request.Package.IsApproved)
            await packages.ApproveAsync(request.PackageId, user, ct);
        var deployment = await deployments.StartAsync(request.PackageId, request.ComputerIds,
            new DeploymentOptions(request.ScanAfter, request.RetryOffline, request.RespectWindow) { RequireApproved = true }, ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.DeploymentRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, DeploymentRequestStatus.Started).SetProperty(r => r.DecidedBy, user)
                .SetProperty(r => r.DecidedUtc, Now).SetProperty(r => r.DeploymentId, deployment.Id), ct);
        return deployment;
    }

    public async Task RejectAsync(int id, string user, string? note, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.DeploymentRequests.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException(L["Request {0} not found", id]);
        if (request.Status != DeploymentRequestStatus.Pending)
            throw new InvalidOperationException(L["The request has already been decided."]);
        (request.Status, request.DecidedBy, request.DecidedUtc, request.Note) = (DeploymentRequestStatus.Rejected, user, Now, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        await db.SaveChangesAsync(ct);
    }
}
