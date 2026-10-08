using System.Collections.Concurrent;
using System.Threading.Channels;
using Inventory.Contracts;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting.Agent;

public sealed class AgentJob(string host, JobManifest manifest, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress)
{
    public string Host { get; } = host;
    public JobManifest Manifest { get; } = manifest;
    public IReadOnlyList<JobFile> Files { get; } = files;
    public IProgress<ProgressEvent>? Progress { get; } = progress;
    internal TaskCompletionSource PickedUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<JobResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal volatile bool Withdrawn;
}

/// <summary>Briefkasten zwischen Server und Agenten: Jobs warten hier, bis der Agent des Rechners sie abholt.</summary>
public sealed class AgentHub
{
    private readonly ConcurrentDictionary<string, Channel<AgentJob>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentJob> _active = new();

    public AgentJob Enqueue(string host, JobManifest manifest, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress)
    {
        var job = new AgentJob(host, manifest, files, progress);
        Queue(host).Writer.TryWrite(job);
        return job;
    }

    /// <summary>Wartet bis zu <paramref name="wait"/> auf einen Job für den Rechner; null, wenn keiner kommt.</summary>
    public async Task<AgentJob?> DequeueAsync(string host, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var job = await Queue(host).Reader.ReadAsync(timeout.Token);
                if (job.Withdrawn)
                    continue;
                _active[job.Manifest.JobId] = job;
                job.PickedUp.TrySetResult();
                return job;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Laufender Job, sofern er dem angegebenen Rechner gehört.</summary>
    public AgentJob? Find(string jobId, string host) =>
        _active.TryGetValue(jobId, out var job) && job.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ? job : null;

    public void Complete(AgentJob job, JobResult result)
    {
        _active.TryRemove(job.Manifest.JobId, out _);
        job.Completion.TrySetResult(result);
    }

    internal void Withdraw(AgentJob job)
    {
        job.Withdrawn = true;
        _active.TryRemove(job.Manifest.JobId, out _);
    }

    private Channel<AgentJob> Queue(string host) => _queues.GetOrAdd(host, _ => Channel.CreateUnbounded<AgentJob>());
}
