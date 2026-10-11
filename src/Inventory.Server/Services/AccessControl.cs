using System.Security.Claims;
using Inventory.Data.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using Inventory.Core;
using Inventory.Core.Model;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Services;

/// <summary>
/// Anmeldung und Zugriffsregel. Windows-Anmeldung und OIDC lassen sich einzeln oder gemeinsam aktivieren;
/// API-Tokens funktionieren in jedem Fall. Die Agenten-Schnittstelle prüft ihre Schlüssel selbst.
/// </summary>
public static class AccessControl
{
    /// <summary>Kennzeichnet eine Anmeldung, deren Berechtigung bereits beim Anmelden geprüft wurde (OIDC, Windows per Cookie, API-Token).</summary>
    public const string AccessClaim = "inventory_access";
    /// <summary>Rolle der Anmeldung (<see cref="AccessRole"/> als Name).</summary>
    public const string RoleClaim = "inventory_role";
    /// <summary>Namen der Richtlinien; "Viewer" ist zugleich die Grundregel für alles ohne eigene Angabe.</summary>
    public const string ViewerPolicy = "Viewer";
    public const string OperatorPolicy = "Operator";
    public const string AdminPolicy = "Admin";

    /// <summary>Rolle einer Anmeldung; ohne Rollenangabe <see cref="AccessRole.None"/>.</summary>
    public static AccessRole RoleOf(ClaimsPrincipal user) =>
        Enum.TryParse<AccessRole>(user.FindFirst(RoleClaim)?.Value, out var role) ? role : AccessRole.None;
    private const string SelectorScheme = "Inventory";
    private const string PromptItem = "prompt";

    public static void AddInventoryAccess(this IServiceCollection services, InventoryOptions options)
    {
        var windows = options.ResolveWindowsAuthentication();
        var oidc = options.OidcEnabled;
        if (!windows && !oidc)
        {
            // Ohne Anmeldung darf jeder alles; die Richtlinien müssen trotzdem existieren, weil Seiten und Endpunkte sie nennen.
            services.AddAuthorizationBuilder()
                .AddPolicy(ViewerPolicy, policy => policy.RequireAssertion(_ => true))
                .AddPolicy(OperatorPolicy, policy => policy.RequireAssertion(_ => true))
                .AddPolicy(AdminPolicy, policy => policy.RequireAssertion(_ => true));
            return;
        }
        if (oidc)
            options.Oidc.Validate();

        var authentication = services.AddAuthentication(SelectorScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, null)
            // Wählt je Anfrage den passenden Weg: Token, Windows-Handshake oder Sitzungs-Cookie.
            .AddPolicyScheme(SelectorScheme, null, selector => selector.ForwardDefaultSelector = context =>
            {
                if (ApiTokenHandler.HasBearer(context.Request))
                    return ApiTokenHandler.SchemeName;
                if (!oidc)
                    return NegotiateDefaults.AuthenticationScheme;
                var header = context.Request.Headers.Authorization.ToString();
                return windows && (header.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase) || header.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                    ? NegotiateDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme;
            });

        if (windows)
            authentication.AddNegotiate();

        if (oidc)
        {
            authentication.AddCookie(cookie =>
            {
                cookie.Cookie.Name = "Inventory.Session";
                cookie.ExpireTimeSpan = TimeSpan.FromHours(10);
                cookie.SlidingExpiration = true;
                // Mit beiden Wegen entscheidet der Benutzer auf der Anmeldeseite, sonst geht es direkt zum Anbieter.
                cookie.LoginPath = windows ? "/login" : "/login/oidc";
                cookie.AccessDeniedPath = "/denied";
                cookie.Events.OnRedirectToLogin = context =>
                {
                    if (!context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.Redirect(context.RedirectUri);
                        return Task.CompletedTask;
                    }
                    // Skripte bekommen keinen Redirect auf eine Anmeldeseite, sondern einen klaren Statuscode.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    if (windows)
                        context.Response.Headers.WWWAuthenticate = "Negotiate";
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    else
                        context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

            authentication.AddOpenIdConnect(o =>
            {
                o.Authority = options.Oidc.ResolveAuthority();
                o.ClientId = options.Oidc.ClientId;
                o.ClientSecret = options.Oidc.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.RequireHttpsMetadata = !o.Authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Events.OnRedirectToIdentityProvider = context =>
                {
                    // "Mit anderem Konto anmelden": ohne das würde eine bestehende Sitzung beim Anbieter sofort wieder dasselbe Konto liefern.
                    if (context.Properties.Items.TryGetValue(PromptItem, out var prompt))
                        context.ProtocolMessage.Prompt = prompt;
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = async context =>
                {
                    var services = context.HttpContext.RequestServices;
                    var signIns = services.GetRequiredService<SignInLog>();
                    var L = services.GetRequiredService<IStringLocalizer<SharedResource>>();
                    var user = context.Principal!;
                    var name = user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value
                               ?? user.FindFirst("name")?.Value ?? user.FindFirst("sub")?.Value ?? "unbekannt";
                    var role = options.Oidc.RoleOf(user);
                    if (role == AccessRole.None)
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AccessControl))
                            .LogWarning("OIDC sign-in rejected: {User} is not in any allowed group or role", name);
                        await signIns.DeniedAsync(name, L["Sign-in denied"], options.Oidc.DisplayName);
                        context.HandleResponse();
                        context.Response.Redirect("/denied");
                        return;
                    }
                    // Nur das Nötige ins Sitzungs-Cookie: bei vielen Gruppen würde es sonst zu groß.
                    context.Principal = SessionPrincipal(name, "oidc", role);
                    await signIns.SignedInAsync(new AuditActor(name, role, AuditSource.Web), L["Signed in"], options.Oidc.DisplayName);
                };
            });
        }

        // Die direkte Windows-Anmeldung (ohne Sitzungs-Cookie) bringt nur Gruppen mit; daraus wird je Anfrage die Rolle.
        if (windows)
            services.AddSingleton<IClaimsTransformation>(new WindowsRoleTransformation(options));

        AuthorizationPolicy AtLeast(AccessRole role) => new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context => RoleOf(context.User) >= role)
            .Build();
        // Ohne Rolle gibt es weder Oberfläche noch API: der Server verwaltet Administrator-Zugangsdaten.
        var viewer = AtLeast(AccessRole.Viewer);
        services.AddAuthorizationBuilder()
            .AddPolicy(ViewerPolicy, viewer)
            .AddPolicy(OperatorPolicy, AtLeast(AccessRole.Operator))
            .AddPolicy(AdminPolicy, AtLeast(AccessRole.Admin))
            .SetFallbackPolicy(viewer);
    }

    /// <summary>Ergänzt die Rolle für Anmeldungen, die sie noch nicht tragen (Negotiate ohne Sitzungs-Cookie).</summary>
    private sealed class WindowsRoleTransformation(InventoryOptions options) : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == RoleClaim))
                return Task.FromResult(principal);
            var role = options.RoleOf(principal);
            if (role == AccessRole.None)
                return Task.FromResult(principal);
            // Eigene Identität: die Windows-Identität selbst bleibt unverändert und wird je Verbindung wiederverwendet.
            var result = new ClaimsPrincipal(principal.Identities);
            result.AddIdentity(new ClaimsIdentity([new Claim(RoleClaim, role.ToString())]));
            return Task.FromResult(result);
        }
    }

    public static void MapInventoryLogin(this IEndpointRouteBuilder app, InventoryOptions options)
    {
        if (!options.OidcEnabled)
            return;
        var windows = options.ResolveWindowsAuthentication();

        app.MapGet("/login/oidc", (string? returnUrl, bool? switchAccount) =>
        {
            var properties = new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) };
            if (switchAccount == true)
                properties.Items[PromptItem] = "login";
            return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous();

        if (windows)
        {
            app.MapGet("/login", (string? returnUrl, IStringLocalizer<SharedResource> L) => Results.Content(Page(L["Sign in"], $"""
                <p>{L["Please choose how to sign in."]}</p>
                <a class="button" href="/login/windows?returnUrl={Uri.EscapeDataString(LocalUrl(returnUrl))}">{L["Windows sign-in"]}</a>
                <a class="button" href="/login/oidc?returnUrl={Uri.EscapeDataString(LocalUrl(returnUrl))}">{System.Net.WebUtility.HtmlEncode(options.Oidc.DisplayName)}</a>
                """), "text/html; charset=utf-8")).AllowAnonymous();

            // Führt den Windows-Handshake einmal durch und stellt danach dasselbe Sitzungs-Cookie aus wie die OIDC-Anmeldung.
            app.MapGet("/login/windows", async (HttpContext http, string? returnUrl, SignInLog signIns, IStringLocalizer<SharedResource> L) =>
            {
                var result = await http.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
                if (!result.Succeeded)
                    return Results.Challenge(null, [NegotiateDefaults.AuthenticationScheme]);
                var role = options.RoleOf(result.Principal);
                var name = result.Principal.Identity?.Name ?? "unknown";
                if (role == AccessRole.None)
                {
                    await signIns.DeniedAsync(name, L["Sign-in denied"], "Windows");
                    return Results.Redirect("/denied");
                }
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, SessionPrincipal(name, "windows", role));
                await signIns.SignedInAsync(new AuditActor(name, role, AuditSource.Web), L["Signed in"], "Windows");
                return Results.Redirect(LocalUrl(returnUrl));
            }).AllowAnonymous();
        }

        app.MapGet("/logout", async (HttpContext http, IStringLocalizer<SharedResource> L, SignInLog signIns) =>
        {
            // Nur echte Sitzungen: der Aufruf ohne Anmeldung (z. B. ein zweites Mal) hinterlässt keinen Eintrag.
            var session = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (session is { Succeeded: true, Principal: { } principal })
                await signIns.SignedOutAsync(AuditActors.Actor(principal, AuditSource.Web), L["Signed out"]);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Content(Page(L["Signed out"], $"""<p>{L["You are signed out."]}</p><a class="button" href="/">{L["Sign in again"]}</a><a class="button secondary" href="/login/oidc?switchAccount=true">{L["Sign in with another account"]}</a>"""), "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapGet("/denied", (IStringLocalizer<SharedResource> L) => Results.Content(Page(L["No access"],
            $"""<p>{L["Your account is not a member of any group that may use Inventory."]}</p><a class="button" href="/login/oidc?switchAccount=true">{L["Sign in with another account"]}</a>"""),
            "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden)).AllowAnonymous();
    }

    private static ClaimsPrincipal SessionPrincipal(string name, string method, AccessRole role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, name), new Claim(AccessClaim, method), new Claim(RoleClaim, role.ToString())], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>Nur Ziele innerhalb der Anwendung; verhindert, dass ein präparierter Link nach der Anmeldung woandershin führt.</summary>
    private static string LocalUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/";

    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="{{System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}} – Inventory</title>
        <style>
          body { font-family: Roboto, Segoe UI, sans-serif; background: #f5f5f5; margin: 0; display: grid; place-items: center; min-height: 100vh; color: #212121; }
          main { background: #fff; padding: 32px 40px; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,.15); max-width: 360px; }
          h1 { font-size: 1.4rem; margin: 0 0 12px; }
          .button.secondary { background: #fff; color: #594ae2; border: 1px solid #594ae2; }
          .button { display: block; margin-top: 12px; padding: 10px 16px; border-radius: 4px; background: #594ae2; color: #fff; text-decoration: none; text-align: center; }
        </style></head>
        <body><main><h1>{{title}}</h1>{{body}}</main></body></html>
        """;
}
