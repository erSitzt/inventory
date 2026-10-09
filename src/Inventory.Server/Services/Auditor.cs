using Inventory.Core;
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

    public async Task LogAsync(string action, string? details = null) =>
        await audit.LogAsync(await UserNameAsync(), action, details);
}
