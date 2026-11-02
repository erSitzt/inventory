using Inventory.Contracts;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <param name="ReportedName">Gesetzt, wenn der Rechner sich mit einem anderen Namen gemeldet hat als eingetragen.</param>
public sealed record ScanOutcome(int ComputerId, bool Success, string? Error, string? MismatchedName = null);

/// <summary>Führt Inventar-Scans aus und speichert die Ergebnisse.</summary>
/// <param name="custom">Eigene Inventarfelder der Plugins; ihre Skripte laufen nach einem erfolgreichen Scan.</param>
public sealed class ScanService(RemoteJobService jobs, InventoryStore store, IStringLocalizer<SharedResource>? localizer = null, CustomValueService? custom = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    private const int MaxParallelScans = 16;

    public async Task<ScanOutcome> ScanAsync(int computerId, CancellationToken ct = default)
    {
        var job = new JobManifest { Steps = [new JobStep { Name = "Inventar-Scan", Type = StepType.InventoryScan, TimeoutSeconds = 300 }] };
        var result = await jobs.RunAsync(computerId, job, null, ct);

        if (result is { Success: true, Inventory: not null })
        {
            var mismatch = await store.SaveAsync(computerId, result.Inventory, ct);
            // Nur auf dem richtigen Rechner: meldet sich ein anderer, gehören seine Werte nicht zu diesem Eintrag.
            if (!mismatch)
                await RunScriptsAsync(computerId, ct);
            return new ScanOutcome(computerId, true, null, mismatch ? result.Inventory.ComputerName : null);
        }

        var error = result.Error ?? result.Steps.FirstOrDefault(s => s.Error is not null)?.Error ?? L["The scan returned no data"];
        await store.SaveErrorAsync(computerId, error, CancellationToken.None);
        return new ScanOutcome(computerId, false, error);
    }

    /// <summary>
    /// Die Inventarskripte der Plugins als zweiter Auftrag, ein PowerShell-Schritt je Skript. Als normale Schritte gilt für sie
    /// die Prüfung des Zielnamens (RemoteJobService); ein fehlgeschlagenes Skript hält die übrigen nicht auf.
    /// </summary>
    private async Task RunScriptsAsync(int computerId, CancellationToken ct)
    {
        if (custom is null || custom.Scripts.Count == 0)
            return;
        var scripts = custom.Scripts;
        var job = new JobManifest
        {
            Steps = [.. scripts.Select(s => new JobStep { Name = s.Name, Type = StepType.PowerShell, Script = s.Script, TimeoutSeconds = s.TimeoutSeconds, ContinueOnError = true })],
        };
        var result = await jobs.RunAsync(computerId, job, null, ct);
        for (var i = 0; i < scripts.Count; i++)
        {
            var step = result.Steps.FirstOrDefault(s => s.Index == i);
            var error = step is null ? result.Error ?? L["The script did not run"]
                : step.Status != StepStatus.Succeeded ? step.Error ?? L["The script failed (exit code {0})", step.ExitCode ?? -1]
                : null;
            await custom.ApplyScriptAsync(computerId, scripts[i], step?.Output, error, ct);
        }
    }

    /// <param name="onCompleted">Wird je Rechner aus einem Hintergrund-Thread aufgerufen.</param>
    public async Task<List<ScanOutcome>> ScanAsync(IEnumerable<int> computerIds, Action<ScanOutcome>? onCompleted = null, CancellationToken ct = default)
    {
        var outcomes = new List<ScanOutcome>();
        await Parallel.ForEachAsync(computerIds, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelScans, CancellationToken = ct },
            async (id, token) =>
            {
                var outcome = await ScanAsync(id, token);
                lock (outcomes)
                    outcomes.Add(outcome);
                onCompleted?.Invoke(outcome);
            });
        return outcomes;
    }
}
