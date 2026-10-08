using Inventory.Data.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Inventory.Server.Services;

/// <summary>Schreibt Protokolleinträge im Namen des gerade angemeldeten Benutzers der Blazor-Sitzung.</summary>
public sealed class Auditor(AuditService audit, AuthenticationStateProvider authentication)
{
    public async Task<string> UserNameAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return state.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "(ohne Anmeldung)";
    }

    public async Task LogAsync(string action, string? details = null) =>
        await audit.LogAsync(await UserNameAsync(), action, details);
}
