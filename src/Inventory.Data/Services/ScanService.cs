using Inventory.Contracts;

namespace Inventory.Data.Services;

public sealed record ScanOutcome(int ComputerId, bool Success, string? Error);

/// <summary>Führt Inventar-Scans aus und speichert die Ergebnisse.</summary>
public sealed class ScanService(RemoteJobService jobs, InventoryStore store)
{
    private const int MaxParallelScans = 16;

    public async Task<ScanOutcome> ScanAsync(int computerId, CancellationToken ct = default)
    {
        var job = new JobManifest { Steps = [new JobStep { Name = "Inventar-Scan", Type = StepType.InventoryScan, TimeoutSeconds = 300 }] };
        var result = await jobs.RunAsync(computerId, job, null, ct);

        if (result is { Success: true, Inventory: not null })
        {
            await store.SaveAsync(computerId, result.Inventory, ct);
            return new ScanOutcome(computerId, true, null);
        }

        var error = result.Error ?? result.Steps.FirstOrDefault(s => s.Error is not null)?.Error ?? "Der Scan lieferte keine Daten";
        await store.SaveErrorAsync(computerId, error, CancellationToken.None);
        return new ScanOutcome(computerId, false, error);
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
