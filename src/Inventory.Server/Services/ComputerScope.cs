using System.Security.Claims;
using Inventory.Data.Services;

namespace Inventory.Server.Services;

/// <summary>
/// Welche Rechner ein beschränkter Operator anfassen darf: die aktuellen Mitglieder der Sammlungen seines Bereichs
/// (<see cref="InventoryOptions.OperatorScopes"/>). Ausgewertet bei jeder Aktion, damit dynamische Sammlungen gelten, wie sie gerade sind.
/// </summary>
public sealed class ComputerScope(CollectionService collections)
{
    /// <summary>Erlaubte Rechner; null = keine Beschränkung.</summary>
    public async Task<HashSet<int>?> AllowedAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (AccessControl.ScopeOf(user) is not { } names)
            return null;
        // Je Sitzung bzw. Anfrage kurz merken: eine Liste fragt das für jede Zeile ab.
        var key = string.Join("\n", names);
        if (_cached is { } cached && cached.Key == key && DateTime.UtcNow - cached.Time < CacheTime)
            return cached.Allowed;
        var allowed = new HashSet<int>();
        foreach (var summary in await collections.ListAsync(ct))
        {
            if (names.Contains(summary.Collection.Name, StringComparer.OrdinalIgnoreCase))
                allowed.UnionWith(await collections.GetMemberIdsAsync(summary.Collection.Id, ct));
        }
        _cached = (key, DateTime.UtcNow, allowed);
        return allowed;
    }

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(15);
    private (string Key, DateTime Time, HashSet<int> Allowed)? _cached;

    /// <summary>Die Rechner aus <paramref name="computerIds"/>, die außerhalb des Bereichs liegen; leer = alle erlaubt.</summary>
    public async Task<List<int>> OutsideAsync(ClaimsPrincipal user, IEnumerable<int> computerIds, CancellationToken ct = default) =>
        await AllowedAsync(user, ct) is { } allowed ? [.. computerIds.Where(id => !allowed.Contains(id)).Distinct()] : [];

    /// <summary>Für die API: 403 mit den Rechnern außerhalb des Bereichs, sonst null.</summary>
    public static async Task<IResult?> ForbiddenAsync(HttpContext http, IEnumerable<int> computerIds)
    {
        var outside = await http.RequestServices.GetRequiredService<ComputerScope>().OutsideAsync(http.User, computerIds, http.RequestAborted);
        return outside.Count == 0 ? null
            : Results.Problem($"Outside the operator's area (collections in Inventory:OperatorScopes): computer IDs {string.Join(", ", outside)}", statusCode: StatusCodes.Status403Forbidden);
    }
}
