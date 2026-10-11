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
public sealed class Access(AuthenticationStateProvider authentication, IAuthorizationService authorization, ISnackbar snackbar, IStringLocalizer<SharedResource> L)
{
    public Task<bool> IsAdminAsync() => HasAsync(AccessControl.AdminPolicy);

    public Task<bool> IsOperatorAsync() => HasAsync(AccessControl.OperatorPolicy);

    /// <summary>false und ein Hinweis an den Benutzer, wenn die Anmeldung kein Administrator ist.</summary>
    public Task<bool> DemandAdminAsync() => DemandAsync(AccessControl.AdminPolicy);

    /// <summary>false und ein Hinweis an den Benutzer, wenn die Anmeldung weder Operator noch Administrator ist.</summary>
    public Task<bool> DemandOperatorAsync() => DemandAsync(AccessControl.OperatorPolicy);

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
