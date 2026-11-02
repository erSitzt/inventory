using Inventory.Data.Services;
using Inventory.Plugins;

namespace Inventory.Server.Plugins;

/// <summary>Zugang der Plugins zu den Werten eigener Felder.</summary>
public sealed class CustomFieldStore(CustomValueService values) : ICustomFieldStore
{
    public Task SetAsync(int computerId, string key, string? value, CancellationToken ct = default) => values.SetAsync(computerId, key, value, ct);

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(int computerId, CancellationToken ct = default) =>
        (await values.GetAsync(computerId, ct)).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
