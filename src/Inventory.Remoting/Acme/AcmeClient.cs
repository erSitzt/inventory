using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inventory.Remoting.Acme;

/// <summary>Eine Meldung der Zertifizierungsstelle oder des DNS-Anbieters, für die Oberfläche.</summary>
public sealed class AcmeException(string message) : Exception(message);

/// <summary>Setzt und entfernt die TXT-Einträge der DNS-Prüfung (dns-01) beim DNS-Anbieter.</summary>
public interface IDnsChallenge
{
    /// <param name="name">Voller Name des Eintrags ohne Punkt am Ende, z. B. "_acme-challenge.inventory.firma.de".</param>
    Task SetTxtAsync(string name, IReadOnlyList<string> values, CancellationToken ct);

    Task RemoveTxtAsync(string name, CancellationToken ct);
}

/// <summary>
/// Schlanker ACME-Client (RFC 8555) für Let's Encrypt und andere ACME-Zertifizierungsstellen: Konto, Auftrag, DNS-Prüfung,
/// Abschluss mit eigenem Schlüssel, Abruf der Kette. Nur dns-01 – der Server muss dafür aus dem Internet nicht erreichbar sein.
/// </summary>
/// <param name="accountKey">Schlüssel des ACME-Kontos (P-256); derselbe Schlüssel findet dasselbe Konto wieder.</param>
public sealed class AcmeClient(HttpClient http, Uri directory, ECDsa accountKey)
{
    public static readonly Uri LetsEncrypt = new("https://acme-v02.api.letsencrypt.org/directory");
    public static readonly Uri LetsEncryptStaging = new("https://acme-staging-v02.api.letsencrypt.org/directory");
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(3);
    /// <summary>RFC 8555 verlangt einen User-Agent; Let's Encrypt und Pebble lehnen Anfragen ohne ab.</summary>
    private static readonly ProductInfoHeaderValue UserAgent = new("Inventory-ACME", "1");

    private JsonNode? _directory;
    private string? _nonce;
    private string? _account;

    /// <summary>Wartezeit zwischen zwei Abfragen; in Tests ersetzbar.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>
    /// Beantragt ein Zertifikat für die Namen und liefert die Kette als PEM (das Zertifikat zuerst). Die TXT-Einträge stehen
    /// nur während der Prüfung und werden danach wieder entfernt.
    /// </summary>
    /// <param name="key">Schlüssel des neuen Zertifikats.</param>
    /// <param name="propagation">So lange nach dem Setzen der TXT-Einträge warten, bis die Nameserver des Anbieters sie kennen.</param>
    public async Task<string> OrderAsync(IReadOnlyList<string> names, RSA key, IDnsChallenge dns, TimeSpan propagation, string? email,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (names.Count == 0)
            throw new ArgumentException("At least one name is needed.", nameof(names));
        await EnsureAccountAsync(email, ct);

        progress?.Report("Order");
        var (orderUrl, order) = await PostAsync(Url("newOrder"),
            new JsonObject { ["identifiers"] = new JsonArray([.. names.Select(n => (JsonNode)new JsonObject { ["type"] = "dns", ["value"] = n })]) }, ct);
        if (orderUrl is null)
            throw new AcmeException("The certification authority did not return the order address.");

        // Je Name die offene Prüfung; Name und Platzhalter (*.x) teilen sich einen TXT-Eintrag.
        var pending = new List<(string Authorization, string Challenge, string Name)>();
        var records = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var authorization in order["authorizations"]!.AsArray().Select(a => a!.GetValue<string>()))
        {
            var (_, auth) = await PostAsync(authorization, null, ct);
            if (auth["status"]?.GetValue<string>() == "valid")
                continue;
            var name = auth["identifier"]!["value"]!.GetValue<string>();
            var challenge = auth["challenges"]!.AsArray().FirstOrDefault(c => c!["type"]?.GetValue<string>() == "dns-01")
                            ?? throw new AcmeException($"The certification authority offers no DNS check for {name}.");
            var token = challenge["token"]!.GetValue<string>();
            var value = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(token + "." + Thumbprint())));
            var record = "_acme-challenge." + name.TrimStart('*', '.');
            if (!records.TryGetValue(record, out var values))
                records[record] = values = [];
            values.Add(value);
            pending.Add((authorization, challenge["url"]!.GetValue<string>(), name));
        }

        try
        {
            foreach (var (record, values) in records)
            {
                progress?.Report($"DNS: {record}");
                await dns.SetTxtAsync(record, values, ct);
            }
            if (records.Count > 0 && propagation > TimeSpan.Zero)
            {
                progress?.Report($"Wait {propagation.TotalSeconds:0} s");
                await Delay(propagation, ct);
            }
            foreach (var (authorization, challenge, name) in pending)
            {
                await PostAsync(challenge, new JsonObject(), ct);
                await WaitAsync(authorization, "valid", ct, name);
            }
        }
        finally
        {
            foreach (var record in records.Keys)
            {
                try
                {
                    await dns.RemoveTxtAsync(record, CancellationToken.None);
                }
                catch (Exception ex) when (ex is AcmeException or HttpRequestException)
                {
                    // Ein übrig gebliebener TXT-Eintrag stört nicht; die nächste Prüfung überschreibt ihn.
                    progress?.Report($"DNS: {record} not removed: {ex.Message}");
                }
            }
        }

        progress?.Report("Finalize");
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + names[0].TrimStart('*', '.')), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternative = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
            alternative.AddDnsName(name);
        request.CertificateExtensions.Add(alternative.Build());
        await PostAsync(order["finalize"]!.GetValue<string>(), new JsonObject { ["csr"] = Base64Url(request.CreateSigningRequest()) }, ct);
        var done = await WaitAsync(orderUrl, "valid", ct);
        var certificate = done["certificate"]?.GetValue<string>() ?? throw new AcmeException("The order has no certificate.");
        progress?.Report("Download");
        return await DownloadAsync(certificate, ct);
    }

    /// <summary>Thumbprint des Kontoschlüssels nach RFC 7638 (Teil der Antwort auf die Prüfung).</summary>
    public string Thumbprint()
    {
        var p = accountKey.ExportParameters(false);
        // Feste Reihenfolge und ohne Leerzeichen, wie RFC 7638 sie vorschreibt.
        var json = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(p.Q.X!)}\",\"y\":\"{Base64Url(p.Q.Y!)}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private async Task EnsureAccountAsync(string? email, CancellationToken ct)
    {
        if (_account is not null)
            return;
        var request = new JsonObject { ["termsOfServiceAgreed"] = true };
        if (!string.IsNullOrWhiteSpace(email))
            request["contact"] = new JsonArray("mailto:" + email.Trim());
        var (location, _) = await PostAsync(Url("newAccount"), request, ct, useKey: true);
        _account = location ?? throw new AcmeException("The certification authority did not return the account address.");
    }

    /// <summary>Fragt ab, bis der Status erreicht ist; "invalid" bricht mit der Meldung der Zertifizierungsstelle ab.</summary>
    /// <param name="subject">Wofür (z. B. der Name der Prüfung); steht vor einer Fehlermeldung.</param>
    private async Task<JsonNode> WaitAsync(string url, string wanted, CancellationToken ct, string? subject = null)
    {
        var until = DateTime.UtcNow + PollLimit;
        while (true)
        {
            var (_, state) = await PostAsync(url, null, ct);
            var status = state["status"]?.GetValue<string>();
            if (status == wanted)
                return state;
            if (status == "invalid")
                throw new AcmeException($"{subject ?? url}: {ErrorOf(state) ?? "invalid"}");
            if (DateTime.UtcNow > until)
                throw new AcmeException($"{subject ?? url}: still {status} after {PollLimit.TotalMinutes:0} minutes.");
            await Delay(PollInterval, ct);
        }
    }

    /// <summary>Die erste Fehlermeldung einer Prüfung oder eines Auftrags.</summary>
    private static string? ErrorOf(JsonNode state) =>
        state["error"]?["detail"]?.GetValue<string>()
        ?? state["challenges"]?.AsArray().Select(c => c?["error"]?["detail"]?.GetValue<string>()).FirstOrDefault(d => d is not null);

    private async Task<string> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, null, useKey: false, accept: "application/pem-certificate-chain", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Signierte Anfrage; <paramref name="payload"/> null = "POST-as-GET". Liefert Location und den Inhalt.</summary>
    private async Task<(string? Location, JsonNode Body)> PostAsync(string url, JsonObject? payload, CancellationToken ct, bool useKey = false)
    {
        using var response = await SendAsync(url, payload, useKey, null, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.Headers.Location?.ToString(), string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, JsonObject? payload, bool useKey, string? accept, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var nonce = _nonce ?? await NewNonceAsync(ct);
            _nonce = null;
            var header = new JsonObject { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url };
            if (useKey)
            {
                var p = accountKey.ExportParameters(false);
                header["jwk"] = new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = Base64Url(p.Q.X!), ["y"] = Base64Url(p.Q.Y!) };
            }
            else
                header["kid"] = _account ?? throw new InvalidOperationException("No account yet.");
            var protectedHeader = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
            var body = payload is null ? "" : Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            var signature = Base64Url(accountKey.SignData(Encoding.ASCII.GetBytes(protectedHeader + "." + body), HashAlgorithmName.SHA256));
            var content = new StringContent(new JsonObject { ["protected"] = protectedHeader, ["payload"] = body, ["signature"] = signature }.ToJsonString());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.UserAgent.Add(UserAgent);
            if (accept is not null)
                request.Headers.Accept.ParseAdd(accept);
            var response = await http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Replay-Nonce", out var nonces))
                _nonce = nonces.FirstOrDefault();
            if (response.IsSuccessStatusCode)
                return response;

            var problem = await ProblemAsync(response, ct);
            response.Dispose();
            // Ein verbrauchter Nonce ist kein Fehler der Anfrage: mit dem neuen aus der Antwort noch einmal.
            if (problem.Type == "urn:ietf:params:acme:error:badNonce" && attempt < 2)
                continue;
            throw new AcmeException(problem.Detail ?? $"{url}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static async Task<(string? Type, string? Detail)> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var detail = problem?["detail"]?.GetValue<string>();
            var sub = problem?["subproblems"]?.AsArray().Select(s => s?["detail"]?.GetValue<string>()).Where(d => d is not null).ToList();
            return (problem?["type"]?.GetValue<string>(), sub is { Count: > 0 } ? $"{detail} ({string.Join("; ", sub)})" : detail);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task<string> NewNonceAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Url("newNonce"));
        request.Headers.UserAgent.Add(UserAgent);
        using var response = await http.SendAsync(request, ct);
        return response.Headers.TryGetValues("Replay-Nonce", out var nonces) ? nonces.First() : throw new AcmeException("The certification authority returned no nonce.");
    }

    private string Url(string name) =>
        (_directory ??= LoadDirectory())[name]?.GetValue<string>() ?? throw new AcmeException($"The directory has no \"{name}\".");

    private JsonNode LoadDirectory()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, directory);
            request.Headers.UserAgent.Add(UserAgent);
            using var response = http.Send(request);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return JsonNode.Parse(reader.ReadToEnd())!;
        }
        catch (HttpRequestException ex)
        {
            throw new AcmeException($"{directory}: {ex.Message}");
        }
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
