using System.Security.Claims;
using System.Text.Encodings.Web;
using Inventory.Data.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Inventory.Server.Services;

/// <summary>Anmeldung per API-Token im Kopf "Authorization: Bearer inv_…".</summary>
public sealed class ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    private const string HeaderPrefix = "Bearer ";

    /// <summary>true, wenn die Anfrage ein Bearer-Token mitbringt (gleich ob gültig).</summary>
    public static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = await tokens.ValidateAsync(header[HeaderPrefix.Length..].Trim(), Context.RequestAborted);
        if (token is null)
            return AuthenticateResult.Fail("Unbekanntes oder abgelaufenes API-Token");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, $"API-Token „{token.Name}“ ({token.CreatedBy})"),
            new Claim(AccessControl.AccessClaim, "api-token"),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
