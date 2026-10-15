using Inventory.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Inventory.Server.Services;

/// <summary>
/// Rollenprüfung für die Oberfläche. Schaltflächen ohne Berechtigung werden gar nicht erst angeboten; diese Prüfung steht
/// zusätzlich am Anfang jeder ändernden Aktion, damit die Grenze nicht allein davon abhängt, was gerade gerendert ist.
/// Hintergrundläufe (Zeitplaner) rufen die Dienste direkt auf und sind davon nicht betroffen.
/// </summary>
public sealed class Access(AuthenticationStateProvider authentication, IAuthorizationService authorization, ISnackbar snackbar, IStringLocalizer<SharedResource> L,
    ComputerScope scope)
{
    public Task<bool> IsAdminAsync() => HasAsync(AccessControl.AdminPolicy);

    public Task<bool> IsOperatorAsync() => HasAsync(AccessControl.OperatorPolicy);

    /// <summary>false und ein Hinweis an den Benutzer, wenn die Anmeldung kein Administrator ist.</summary>
    public Task<bool> DemandAdminAsync() => DemandAsync(AccessControl.AdminPolicy);

    /// <summary>false und ein Hinweis an den Benutzer, wenn die Anmeldung weder Operator noch Administrator ist.</summary>
    public Task<bool> DemandOperatorAsync() => DemandAsync(AccessControl.OperatorPolicy);

    /// <summary>
    /// Wie <see cref="DemandOperatorAsync"/>, und alle Rechner liegen im Bereich des Operators. Sonst false und ein Hinweis,
    /// welche Rechner nicht erlaubt sind.
    /// </summary>
    public async Task<bool> DemandComputersAsync(IEnumerable<int> computerIds, Func<int, string>? nameOf = null)
    {
        if (!await DemandOperatorAsync())
            return false;
        var outside = await scope.OutsideAsync((await authentication.GetAuthenticationStateAsync()).User, computerIds);
        if (outside.Count == 0)
            return true;
        var names = string.Join(", ", outside.Take(5).Select(id => nameOf?.Invoke(id) ?? $"#{id}")) + (outside.Count > 5 ? " …" : "");
        snackbar.Add(L["Outside your area (collections you may act on): {0}", names], Severity.Error);
        return false;
    }

    /// <summary>Rechner, auf denen die Anmeldung jetzt handeln darf; null = keine Beschränkung.</summary>
    public async Task<HashSet<int>?> AllowedComputersAsync() => await scope.AllowedAsync((await authentication.GetAuthenticationStateAsync()).User);

    /// <summary>Wie <see cref="AllowedComputersAsync"/>, aber einige Sekunden zwischengespeichert: nur zum Anzeigen, nie zum Erlauben.</summary>
    public async Task<HashSet<int>?> AllowedComputersForDisplayAsync() =>
        await scope.AllowedAsync((await authentication.GetAuthenticationStateAsync()).User, forDisplay: true);

    private async Task<bool> HasAsync(string policy) =>
        (await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, policy)).Succeeded;

    private async Task<bool> DemandAsync(string policy)
    {
        if (await HasAsync(policy))
            return true;
        snackbar.Add(L["Your role does not allow this action."], Severity.Error);
        return false;
    }
}
