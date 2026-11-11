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
/// wird die App-Registrierung mit ihrem Geheimnis (Client Credentials).
/// </summary>
public sealed class AzureDnsChallenge(HttpClient http, AzureDnsSettings settings) : IDnsChallenge
{
    private const string ApiVersion = "2018-05-01";
    private const string Management = "https://management.azure.com";
    private string? _token;
    private DateTime _tokenUntilUtc;

    public async Task SetTxtAsync(string name, IReadOnlyList<string> values, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["TTL"] = 60,
                ["TXTRecords"] = new JsonArray([.. values.Select(v => (JsonNode)new JsonObject { ["value"] = new JsonArray(v) })]),
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, RecordUrl(name)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        (await SendAsync(request, ct)).Dispose();
    }

    public async Task RemoveTxtAsync(string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, RecordUrl(name));
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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var response = await http.SendAsync(request, ct);
        // Löschen eines Eintrags, den es nicht (mehr) gibt, ist kein Fehler.
        if (response.IsSuccessStatusCode || (request.Method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound))
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
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { } token)
            throw new AcmeException($"Microsoft Entra ID: {json?["error_description"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? response.ReasonPhrase}");
        _token = token;
        var seconds = json["expires_in"] is { } expires && int.TryParse(expires.ToString(), out var s) ? s : 3600;
        _tokenUntilUtc = DateTime.UtcNow.AddSeconds(seconds - 120);
        return token;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (System.Text.Json.JsonException)
        {
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
    }
}
