using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Inventory.Server.Services;

/// <summary>Schreibt Protokolleinträge im Namen des gerade angemeldeten Benutzers der Blazor-Sitzung.</summary>
public sealed class Auditor(AuditService audit, AuthenticationStateProvider authentication, IStringLocalizer<SharedResource> L)
{
    public async Task<string> UserNameAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return state.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : L["(not signed in)"];
    }

    /// <summary>Name, Rolle und Herkunft der Sitzung – für Dienste, die selbst protokollieren.</summary>
    public async Task<AuditActor> ActorAsync() =>
        AuditActors.Actor((await authentication.GetAuthenticationStateAsync()).User, AuditSource.Web, L["(not signed in)"]);

    public async Task LogAsync(string action, string? details = null) =>
        await audit.LogAsync(await ActorAsync(), action, details);
}
