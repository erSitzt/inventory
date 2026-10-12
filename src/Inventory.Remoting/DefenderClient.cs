using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Gibt die Runner-Datei in Microsoft Defender for Endpoint per Datei-Hash frei (Indikator "Zulassen").
/// Angemeldet wird mit dem Konto eines Menschen über den Gerätecode-Ablauf; der Server bekommt nie ein Kennwort
/// und bewahrt das Token nicht auf.
/// </summary>
public sealed partial class DefenderClient(HttpClient http)
{
    /// <summary>An diesem Titel erkennt der Server die eigenen Indikatoren wieder, um veraltete zu entfernen.</summary>
    public const string IndicatorTitle = "Inventory runner";
    private const string Resource = "https://api.securitycenter.microsoft.com";
    private const string Api = "https://api.security.microsoft.com/api/indicators";
    private const int MaxPages = 50;

    /// <summary>Wartezeit zwischen zwei Abfragen; in Tests ersetzbar.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int IntervalSeconds, DateTime ExpiresUtc);

    public sealed record AllowResult(int Removed);

    /// <summary>Mandant als GUID oder Domänenname, Anwendung als GUID: beide landen in einer Adresse bzw. einem Formularfeld.</summary>
    public static bool IsValid(string? tenant, string? clientId) =>
        tenant is not null && TenantPattern().IsMatch(tenant) && Guid.TryParse(clientId, out _);

    /// <summary>Fordert den Code an, den der Mensch auf der Anmeldeseite von Microsoft eingibt.</summary>
    public async Task<DeviceCode> BeginSignInAsync(string tenant, string clientId, CancellationToken ct = default)
    {
        if (!IsValid(tenant, clientId))
            throw new RemoteException("Tenant or application ID is not valid.");
        using var doc = await PostFormAsync($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/devicecode", new()
        {
            ["client_id"] = clientId,
            ["scope"] = $"{Resource}/.default",
        }, ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("device_code", out var code))
            throw new RemoteException($"Sign-in at Microsoft could not be started: {ErrorOf(root)}");
        return new DeviceCode(code.GetString()!, root.GetProperty("user_code").GetString()!, root.GetProperty("verification_uri").GetString()!,
            root.TryGetProperty("interval", out var interval) ? Math.Max(1, interval.GetInt32()) : 5,
            DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900));
    }

    /// <summary>Wartet, bis die Anmeldung abgeschlossen, abgelehnt oder abgelaufen ist, und liefert das Zugriffstoken.</summary>
    public async Task<string> WaitForTokenAsync(string tenant, string clientId, DeviceCode code, CancellationToken ct = default)
    {
        if (!IsValid(tenant, clientId))
            throw new RemoteException("Tenant or application ID is not valid.");
        var interval = TimeSpan.FromSeconds(code.IntervalSeconds);
        while (DateTime.UtcNow < code.ExpiresUtc)
        {
            await Delay(interval, ct);
            using var doc = await PostFormAsync($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = code.Code,
            }, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("access_token", out var token))
                return token.GetString()!;
            switch (root.TryGetProperty("error", out var error) ? error.GetString() : null)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "authorization_declined":
                    throw new RemoteException("The sign-in was declined.");
                case "expired_token":
                    throw new RemoteException("The sign-in code has expired.");
                default:
                    throw new RemoteException($"Sign-in at Microsoft failed: {ErrorOf(root)}");
            }
        }
        throw new RemoteException("The sign-in code has expired.");
    }

    /// <summary>Legt den Indikator für diesen Runner an (oder aktualisiert ihn) und entfernt auf Wunsch die der früheren Runner.</summary>
    public async Task<AllowResult> AllowRunnerAsync(string token, string sha256, string description, bool removeOutdated, CancellationToken ct = default)
    {
        try
        {
            using (var request = Authorized(HttpMethod.Post, Api, token))
            {
                request.Content = JsonContent.Create(new
                {
                    indicatorValue = sha256,
                    indicatorType = "FileSha256",
                    action = "Allowed",
                    title = IndicatorTitle,
                    description,
                    generateAlert = false,
                });
                using var response = await http.SendAsync(request, ct);
                await EnsureAsync(response, ct);
            }

            var removed = 0;
            if (removeOutdated)
            {
                // Erst alle Seiten lesen, dann löschen: sonst verschiebt das Löschen die Seitengrenzen.
                var outdated = new List<string>();
                var url = $"{Api}?$filter={Uri.EscapeDataString($"title eq '{IndicatorTitle}'")}";
                for (var page = 0; url is not null && page < MaxPages; page++)
                {
                    using var list = Authorized(HttpMethod.Get, url, token);
                    using var response = await http.SendAsync(list, ct);
                    await EnsureAsync(response, ct);
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
                    {
                        // Der Filter ist nur eine Vorauswahl: gelöscht wird ausschließlich, was eindeutig von hier stammt.
                        if (Text(item, "title") == IndicatorTitle && Text(item, "indicatorType") == "FileSha256" && Text(item, "action") == "Allowed"
                            && !string.Equals(Text(item, "indicatorValue"), sha256, StringComparison.OrdinalIgnoreCase))
                            outdated.Add(item.GetProperty("id").ToString());
                    }
                    // Das Token geht nur an die Schnittstelle selbst, nie an eine Adresse aus der Antwort mit anderem Ziel.
                    var next = Text(doc.RootElement, "@odata.nextLink");
                    url = next is not null && next.StartsWith(Api, StringComparison.OrdinalIgnoreCase) ? next : null;
                }
                foreach (var id in outdated)
                {
                    using var delete = Authorized(HttpMethod.Delete, $"{Api}/{Uri.EscapeDataString(id)}", token);
                    using var deleted = await http.SendAsync(delete, ct);
                    await EnsureAsync(deleted, ct);
                    removed++;
                }
            }
            return new AllowResult(removed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException && !ct.IsCancellationRequested)
        {
            throw new RemoteException($"Microsoft Defender could not be reached: {ex.Message}", ex);
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new RemoteException("The signed-in account may not manage indicators in Microsoft Defender, or the application lacks the permission Ti.ReadWrite.");
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new RemoteException($"Microsoft Defender answered {(int)response.StatusCode}: {Shorten(body)}");
    }

    private async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), ct);
            // Auch Fehler kommen als JSON ("authorization_pending" ist der Normalfall während des Wartens).
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            throw new RemoteException($"Microsoft sign-in could not be reached: {ex.Message}", ex);
        }
    }

    /// <summary>Die erste Zeile der Fehlerbeschreibung; der Rest sind Ablaufkennungen.</summary>
    private static string ErrorOf(JsonElement root) =>
        Shorten((Text(root, "error_description") ?? Text(root, "error") ?? "unknown error").Split('\n')[0].Trim());

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex TenantPattern();
}
