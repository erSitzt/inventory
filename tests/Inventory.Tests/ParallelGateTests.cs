using Inventory.Data.Services;

namespace Inventory.Tests;

public class ParallelGateTests
{
    [Fact]
    public async Task Waiters_enter_in_order_as_places_free_up_or_the_limit_rises()
    {
        var gate = new ParallelGate(1);
        await gate.EnterAsync(default);
        var second = gate.EnterAsync(default);
        var third = gate.EnterAsync(default);
        Assert.False(second.IsCompleted);

        gate.Exit();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted);

        gate.Limit = 2;
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cancelled_waiter_gives_up_without_taking_a_place_and_a_lower_limit_applies_to_new_entries()
    {
        var gate = new ParallelGate(2);
        await gate.EnterAsync(default);
        await gate.EnterAsync(default);
        gate.Limit = 1;

        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        // Wer schon abgebrochen ist, bekommt auch bei freiem Platz keinen.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ParallelGate(5).EnterAsync(cts.Token));

        // Zwei laufen noch bei Grenze 1: erst wenn beide fertig sind, darf der nächste hinein.
        var next = gate.EnterAsync(default);
        gate.Exit();
        Assert.False(next.IsCompleted);
        gate.Exit();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
