namespace Inventory.Data.Services;

/// <summary>Begrenzt gleichzeitige Arbeiten wie ein Semaphor, dessen Obergrenze sich im Betrieb ändern lässt.</summary>
internal sealed class ParallelGate(int limit)
{
    private readonly object _sync = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = new();
    private int _limit = Math.Max(limit, 1);
    private int _running;

    public int Limit
    {
        get { lock (_sync) return _limit; }
        set
        {
            lock (_sync)
            {
                _limit = Math.Max(value, 1);
                // Eine höhere Grenze lässt Wartende sofort nachrücken; eine niedrigere greift, sobald Laufende fertig sind.
                Admit();
            }
        }
    }

    public async Task EnterAsync(CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (_sync)
        {
            if (_running < _limit)
            {
                _running++;
                return;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.AddLast(waiter);
        }
        await using var registration = ct.Register(() =>
        {
            // Nur wer noch wartet, wird abgebrochen; ein schon vergebener Platz bleibt vergeben und wird regulär freigegeben.
            lock (_sync)
            {
                if (_waiting.Remove(waiter))
                    waiter.TrySetCanceled(ct);
            }
        });
        await waiter.Task;
    }

    public void Exit()
    {
        lock (_sync)
        {
            _running--;
            Admit();
        }
    }

    private void Admit()
    {
        while (_running < _limit && _waiting.First is { } next)
        {
            _waiting.RemoveFirst();
            _running++;
            next.Value.SetResult();
        }
    }
}
