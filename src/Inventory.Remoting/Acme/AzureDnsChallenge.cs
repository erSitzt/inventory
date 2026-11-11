using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Inventory.Remoting.Acme;

/// <summary>Zugang zu einer DNS-Zone in Azure: App-Registrierung mit Geheimnis und der Rolle „DNS Zone Contributor“ auf der Zone.</summary>
public sealed record AzureDnsSettings(string TenantId, string ClientId, string ClientSecret, string SubscriptionId, string ResourceGroup, string Zone)
{
    /// <summary>null, wenn alles ausgefüllt ist, sonst das erste fehlende Feld (englisch, für die Übersetzung).</summary>
    public string? Missing() =>
        string.IsNullOrWhiteSpace(TenantId) ? "Tenant ID"
        : string.IsNullOrWhiteSpace(ClientId) ? "Client ID"
        : string.IsNullOrWhiteSpace(ClientSecret) ? "Client secret"
        : string.IsNullOrWhiteSpace(SubscriptionId) ? "Subscription ID"
        : string.IsNullOrWhiteSpace(ResourceGroup) ? "Resource group"
        : string.IsNullOrWhiteSpace(Zone) ? "DNS zone"
        : null;
}

/// <summary>
/// Setzt die TXT-Einträge der DNS-Prüfung in Azure DNS über die REST-Schnittstelle von Azure Resource Manager; angemeldet
/// wird die App-Registrierung mit ihrem Geheimnis (Client Credentials). Vorhandene Werte desselben Eintrags (etwa von einem
/// anderen ACME-Client) bleiben erhalten: ergänzt und entfernt werden nur die eigenen.
/// </summary>
public sealed class AzureDnsChallenge(HttpClient http, AzureDnsSettings settings) : IDnsChallenge
{
    private const string ApiVersion = "2018-05-01";
    private const string Management = "https://management.azure.com";
    private const int Ttl = 60;
    private readonly Dictionary<string, List<string>> _added = new(StringComparer.OrdinalIgnoreCase);
    private string? _token;
    private DateTime _tokenUntilUtc;

    public async Task SetTxtAsync(string name, IReadOnlyList<string> values, CancellationToken ct)
    {
        var existing = await ReadAsync(name, ct);
        var added = values.Where(v => !existing.Contains(v)).ToList();
        _added[name] = added;
        await WriteAsync(name, [.. existing, .. added], ct);
    }

    public async Task RemoveTxtAsync(string name, CancellationToken ct)
    {
        if (!_added.Remove(name, out var added) || added.Count == 0)
            return;
        var remaining = (await ReadAsync(name, ct)).Where(v => !added.Contains(v)).ToList();
        if (remaining.Count > 0)
        {
            await WriteAsync(name, remaining, ct);
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Delete, RecordUrl(name));
        (await SendAsync(request, ct)).Dispose();
    }

    /// <summary>Die Werte des TXT-Eintrags; leer, wenn es ihn nicht gibt. Je Wert zählt der erste Teil (ACME-Werte sind kurz).</summary>
    private async Task<List<string>> ReadAsync(string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RecordUrl(name));
        using var response = await SendAsync(request, ct, missingIsFine: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        var json = await ParseAsync(response, ct);
        return [.. json?["properties"]?["TXTRecords"]?.AsArray()
            .Select(r => string.Concat(r?["value"]?.AsArray().Select(v => v?.GetValue<string>()) ?? []))
            .Where(v => v.Length > 0) ?? []];
    }

    private async Task WriteAsync(string name, IReadOnlyList<string> values, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["TTL"] = Ttl,
                ["TXTRecords"] = new JsonArray([.. values.Select(v => (JsonNode)new JsonObject { ["value"] = new JsonArray(v) })]),
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, RecordUrl(name)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        (await SendAsync(request, ct)).Dispose();
    }

    /// <summary>Der Name relativ zur Zone, wie Azure ihn für den Eintrag erwartet ("@" für die Zone selbst).</summary>
    public static string RelativeName(string name, string zone)
    {
        name = name.Trim().TrimEnd('.');
        zone = zone.Trim().TrimEnd('.');
        if (name.Equals(zone, StringComparison.OrdinalIgnoreCase))
            return "@";
        if (!name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            throw new AcmeException($"{name} is not in the DNS zone {zone}.");
        return name[..^(zone.Length + 1)];
    }

    private string RecordUrl(string name) =>
        $"{Management}/subscriptions/{Uri.EscapeDataString(settings.SubscriptionId.Trim())}/resourceGroups/{Uri.EscapeDataString(settings.ResourceGroup.Trim())}" +
        $"/providers/Microsoft.Network/dnsZones/{Uri.EscapeDataString(settings.Zone.Trim().TrimEnd('.'))}/TXT/{Uri.EscapeDataString(RelativeName(name, settings.Zone))}?api-version={ApiVersion}";

    /// <param name="missingIsFine">Einen Eintrag, den es nicht (mehr) gibt, nicht als Fehler werten.</param>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool missingIsFine = false)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || ((missingIsFine || request.Method == HttpMethod.Delete) && response.StatusCode == HttpStatusCode.NotFound))
            return response;
        var message = await ErrorAsync(response, ct);
        response.Dispose();
        throw new AcmeException($"Azure DNS: {message}");
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenUntilUtc)
            return _token;
        using var response = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(settings.TenantId.Trim())}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.ClientId.Trim(),
                ["client_secret"] = settings.ClientSecret,
                ["scope"] = Management + "/.default",
            }), ct);
        // Ein Gateway antwortet bei Störungen auch mit HTML oder ohne Inhalt: dann zählt der Statuscode.
        var json = await ParseAsync(response, ct);
        if (!response.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { } token)
            throw new AcmeException($"Microsoft Entra ID: {json?["error_description"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}"}");
        _token = token;
        var seconds = json["expires_in"] is { } expires && int.TryParse(expires.ToString(), out var s) ? s : 3600;
        _tokenUntilUtc = DateTime.UtcNow.AddSeconds(seconds - 120);
        return token;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await ParseAsync(response, ct))?["error"]?["message"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";

    /// <summary>Der Inhalt als JSON; null, wenn er leer ist oder kein JSON (z. B. eine HTML-Fehlerseite).</summary>
    private static async Task<JsonNode?> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
