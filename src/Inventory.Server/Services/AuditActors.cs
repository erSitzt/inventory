using System.Collections.Concurrent;
using System.Security.Claims;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Services;

/// <summary>Leitet aus einer Anmeldung ab, wer im Protokoll steht: Name, Rolle und Weg (Oberfläche, API, API-Token).</summary>
public static class AuditActors
{
    private const string ApiTokenMethod = "api-token";

    public static AuditActor Actor(this HttpContext http) => Actor(http.User, AuditSource.Api);

    /// <param name="source">Weg der Aktion; ein API-Token wird unabhängig davon als solches vermerkt.</param>
    public static AuditActor Actor(ClaimsPrincipal user, AuditSource source, string? fallbackName = null)
    {
        var name = user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } n } ? n : fallbackName ?? "(not signed in)";
        var role = AccessControl.RoleOf(user);
        return new AuditActor(name, role == AccessRole.None ? null : role,
            user.FindFirst(AccessControl.AccessClaim)?.Value == ApiTokenMethod ? AuditSource.ApiToken : source);
    }
}

/// <summary>
/// Anmeldungen im Protokoll. Mit Sitzungs-Cookie (OIDC, Windows über die Anmeldeseite) gibt es ein eindeutiges Ereignis.
/// Die direkte Windows-Anmeldung authentifiziert dagegen jede Verbindung neu; dort wird je Benutzer höchstens ein Eintrag
/// pro Zeitfenster geschrieben, sonst bestünde das Protokoll nur noch aus Anmeldungen.
/// </summary>
public sealed class SignInLog(AuditService audit, TimeProvider time, ILogger<SignInLog> logger)
{
    /// <summary>Entspricht der Laufzeit des Sitzungs-Cookies.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(10);
    private const int MaxTracked = 10_000;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);

    public Task SignedInAsync(AuditActor actor, string action, string method) => WriteAsync(actor, action, method);

    public Task DeniedAsync(string user, string action, string method) => WriteAsync(new AuditActor(user, null, AuditSource.Web), action, method);

    public Task SignedOutAsync(AuditActor actor, string action) => WriteAsync(actor, action, null);

    /// <summary>true, wenn für diese Anmeldung im laufenden Zeitfenster noch kein Eintrag geschrieben wurde.</summary>
    public bool FirstInWindow(string user, bool allowed)
    {
        var now = time.GetUtcNow();
        var key = (allowed ? "+" : "-") + user;
        if (_seen.TryGetValue(key, out var last) && now - last < Window)
            return false;
        if (_seen.Count >= MaxTracked)
        {
            foreach (var old in _seen.Where(p => now - p.Value >= Window).Select(p => p.Key).ToList())
                _seen.TryRemove(old, out _);
            // Lieber einen Eintrag zu wenig als unbegrenzt wachsender Speicher durch frei wählbare Namen.
            if (_seen.Count >= MaxTracked)
                return false;
        }
        _seen[key] = now;
        return true;
    }

    private async Task WriteAsync(AuditActor actor, string action, string? details)
    {
        try
        {
            await audit.LogAsync(actor, action, details);
        }
        catch (Exception ex)
        {
            // Die Anmeldung selbst darf nicht daran scheitern, dass der Protokolleintrag misslingt.
            logger.LogError(ex, "Could not write the sign-in audit entry for {User}", actor.User);
        }
    }
}

/// <summary>Protokolliert direkte Windows-Anmeldungen (ohne Sitzungs-Cookie) einmal je Benutzer und Zeitfenster.</summary>
public sealed class WindowsSignInAuditMiddleware(RequestDelegate next, SignInLog log)
{
    public async Task InvokeAsync(HttpContext http, IStringLocalizer<SharedResource> L)
    {
        var user = http.User;
        // Anmeldungen mit Sitzungs-Cookie und API-Tokens tragen das Kennzeichen und werden an anderer Stelle bzw. gar nicht erfasst.
        if (user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } && !user.HasClaim(c => c.Type == AccessControl.AccessClaim))
        {
            var role = AccessControl.RoleOf(user);
            if (log.FirstInWindow(name, role != AccessRole.None))
            {
                var source = http.Request.Path.StartsWithSegments("/api") ? AuditSource.Api : AuditSource.Web;
                if (role == AccessRole.None)
                    await log.DeniedAsync(name, L["Sign-in denied"], "Windows");
                else
                    await log.SignedInAsync(new AuditActor(name, role, source), L["Signed in"], "Windows");
            }
        }
        await next(http);
    }
}
