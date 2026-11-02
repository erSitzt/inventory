using System.Security.Claims;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;

namespace Inventory.Server.Mcp;

/// <summary>
/// Wer ein Werkzeug aufruft, mit denselben Prüfungen wie die REST-API: Rolle, Operator-Bereich, Protokoll. Dazu das
/// Auflösen von Rechnernamen, damit ein Assistent mit Namen statt IDs arbeiten kann.
/// </summary>
public sealed class McpCaller(IHttpContextAccessor http, IAuthorizationService authorization, ComputerScope scope, AuditService audit, ComputerService computers)
{
    /// <summary>So viele Einträge liefert eine Liste höchstens; mehr überfluten das Modell, ohne zu helfen.</summary>
    public const int MaxItems = 200;

    public ClaimsPrincipal User => http.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    public async Task<bool> IsAdminAsync() => (await authorization.AuthorizeAsync(User, AccessControl.AdminPolicy)).Succeeded;

    /// <summary>Zweite Prüfung neben dem Filter des SDK, damit ein Werkzeug auch bei direktem Aufruf nicht mehr darf als die Rolle.</summary>
    public async Task RequireOperatorAsync()
    {
        if (!(await authorization.AuthorizeAsync(User, AccessControl.OperatorPolicy)).Succeeded)
            throw new McpException("This needs an API token with the Operator or Administrator role.");
    }

    /// <summary>Wie in der API: Rechner außerhalb des Operator-Bereichs werden abgelehnt.</summary>
    public async Task RequireScopeAsync(IEnumerable<int> computerIds, CancellationToken ct)
    {
        var outside = await scope.OutsideAsync(User, computerIds, ct);
        if (outside.Count > 0)
            throw new McpException($"Outside the operator's area (Inventory:OperatorScopes): computer IDs {string.Join(", ", outside)}.");
    }

    public Task LogAsync(string action, string? details, CancellationToken ct) => audit.LogAsync(AuditActors.McpActor(User), action, details, ct);

    /// <summary>Ein Rechner nach Name, DNS-Name oder ID.</summary>
    public async Task<Computer> ComputerAsync(string nameOrId, CancellationToken ct) => (await ComputersAsync([nameOrId], ct))[0];

    /// <summary>Rechner nach Name (auch ohne Domäne), DNS-Name oder ID; unbekannte und mehrdeutige Angaben ergeben einen Fehler mit Kandidaten.</summary>
    public async Task<List<Computer>> ComputersAsync(IReadOnlyCollection<string> namesOrIds, CancellationToken ct)
    {
        if (namesOrIds.Count == 0)
            throw new McpException("No computers given.");
        var all = await computers.ListAsync(ct);
        var result = new List<Computer>();
        var problems = new List<string>();
        foreach (var raw in namesOrIds)
        {
            var key = raw.Trim();
            var matches = all.Where(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(c.DnsName, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = all.Where(c => string.Equals(c.Name.Split('.')[0], key.Split('.')[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && int.TryParse(key, out var id))
                matches = all.Where(c => c.Id == id).ToList();
            if (matches.Count == 1)
                result.Add(matches[0]);
            else if (matches.Count == 0)
                problems.Add($"'{key}' not found");
            else
                problems.Add($"'{key}' is ambiguous: {string.Join(", ", matches.Select(c => $"{c.Name} (id {c.Id})"))}");
        }
        if (problems.Count > 0)
            throw new McpException(string.Join("; ", problems) + ".");
        return [.. result.DistinctBy(c => c.Id)];
    }

    /// <summary>Ein Eintrag nach Name oder ID aus einer Liste (Pakete, Subnetze, Sammlungen).</summary>
    public static T Pick<T>(IEnumerable<T> items, string nameOrId, Func<T, int> id, Func<T, string> name, string what)
    {
        var key = nameOrId.Trim();
        var list = items.ToList();
        var matches = list.Where(i => string.Equals(name(i), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0 && int.TryParse(key, out var number))
            matches = list.Where(i => id(i) == number).ToList();
        if (matches.Count == 0)
            matches = list.Where(i => name(i).Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new McpException($"No {what} '{key}'. Known: {string.Join(", ", list.Take(30).Select(name))}{(list.Count > 30 ? ", …" : "")}."),
            _ => throw new McpException($"'{key}' matches several {what}s: {string.Join(", ", matches.Take(30).Select(i => $"{name(i)} (id {id(i)})"))}."),
        };
    }

    /// <summary>Eine Liste mit Obergrenze und dem Hinweis, wie viele es insgesamt sind.</summary>
    public static object Page<T>(IEnumerable<T> items, int limit = MaxItems)
    {
        var list = items.ToList();
        return new { total = list.Count, truncated = list.Count > limit, items = list.Take(limit) };
    }
}
