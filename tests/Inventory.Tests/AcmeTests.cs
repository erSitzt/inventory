using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting.Acme;
using Inventory.Server.Https;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.Tests;

/// <summary>
/// Let's Encrypt über die DNS-Prüfung. Der ACME-Ablauf läuft gegen Pebble, den Testserver von Let's Encrypt, mit dessen
/// DNS-Testserver statt Azure (in der CI als Container, lokal mit denselben Umgebungsvariablen). Azure DNS prüft ein
/// nachgebildeter HTTP-Handler; gegen Azure selbst läuft kein Test.
/// </summary>
public sealed class AcmeTests
{
    /// <summary>Adresse des ACME-Verzeichnisses von Pebble, z. B. https://localhost:14000/dir.</summary>
    internal static string? Directory => Environment.GetEnvironmentVariable("INVENTORY_ACME_DIRECTORY");

    /// <summary>Verwaltung des DNS-Testservers von Pebble, z. B. http://localhost:8055.</summary>
    internal static string? DnsApi => Environment.GetEnvironmentVariable("INVENTORY_CHALLTESTSRV");

    [Fact]
    public async Task A_certificate_for_a_name_and_its_wildcard_is_issued_through_the_dns_check()
    {
        if (Directory is null || DnsApi is null)
            return;
        using var http = PebbleClient();
        using var account = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var client = new AcmeClient(http, new Uri(Directory), account);
        using var key = RSA.Create(2048);
        var dns = new DnsTestServer(http, DnsApi);
        var steps = new List<string>();

        var pem = await client.OrderAsync(["inventory.example.test", "*.inventory.example.test"], key, dns, TimeSpan.Zero, "admin@example.test",
            new Progress<string>(steps.Add));

        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(pem);
        Assert.True(chain.Count >= 2, "The chain should contain the intermediate certificate.");
        using var certificate = chain[0].CopyWithPrivateKey(key);
        Assert.True(ServerCertificates.Covers(certificate, "inventory.example.test"));
        Assert.True(ServerCertificates.Covers(certificate, "web.inventory.example.test"));
        // Beide Namen teilen sich einen TXT-Eintrag; danach ist er wieder entfernt.
        Assert.Equal(["_acme-challenge.inventory.example.test"], dns.Set.Distinct());
        Assert.Equal(["_acme-challenge.inventory.example.test"], dns.Removed);

        // Mit demselben Kontoschlüssel findet ein neuer Client dasselbe Konto wieder.
        using var second = RSA.Create(2048);
        var again = await new AcmeClient(http, new Uri(Directory), account).OrderAsync(["inventory.example.test"], second, dns, TimeSpan.Zero, null);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", again);
    }

    [Fact]
    public async Task A_failed_dns_check_is_reported_with_the_message_of_the_certification_authority()
    {
        if (Directory is null || DnsApi is null)
            return;
        using var http = PebbleClient();
        using var account = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var key = RSA.Create(2048);
        var error = await Assert.ThrowsAsync<AcmeException>(() =>
            new AcmeClient(http, new Uri(Directory), account).OrderAsync(["wrong.example.test"], key, new WrongDns(), TimeSpan.Zero, null));
        Assert.Contains("wrong.example.test", error.Message);
    }

    [Fact]
    public async Task The_server_requests_uses_and_renews_its_certificate()
    {
        if (Directory is null || DnsApi is null)
            return;
        using var db = new TestDb();
        var settings = new SettingsService(db);
        var audit = new AuditService(db);
        var data = System.IO.Directory.CreateTempSubdirectory("inventory-acme-").FullName;
        var protection = new EphemeralDataProtectionProvider();
        var clock = new Clock(DateTimeOffset.UtcNow);
        using var http = PebbleClient();
        ServerCertificateService Certificates() =>
            new(settings, new ReversingProtector(), protection, audit, clock, NullLogger<ServerCertificateService>.Instance, data);
        try
        {
            var certificates = Certificates();
            await certificates.InitializeAsync();
            var acme = new AcmeService(certificates, settings, new ReversingProtector(), protection, audit, clock, NullLogger<AcmeService>.Instance, data,
                Directory, http: http, dns: _ => new DnsTestServer(http, DnsApi)) { Propagation = TimeSpan.Zero };

            Assert.Equal("Enter the names of the certificate.", await acme.RequestAsync("admin"));
            await acme.SaveAsync(new AcmeSettings(AcmeSettings.ParseNames("Inventory.Example.Test., inventory.example.test"), null, false,
                new AzureDnsSettings("tenant", "client", "", "sub", "rg", "example.test")));
            Assert.Equal("Enter Client secret for Azure DNS.", await acme.RequestAsync("admin"));
            await acme.SaveAsync((await acme.ReadAsync()) with { Azure = new AzureDnsSettings("tenant", "client", "geheim", "sub", "rg", "example.test.") });
            Assert.Equal("mieheg", await settings.GetAsync(SettingKeys.ServerAcmeAzureSecret));
            Assert.Equal("example.test", await settings.GetAsync(SettingKeys.ServerAcmeAzureZone));

            Assert.Null(await acme.RequestAsync("admin"));
            Assert.Equal(ServerCertificateSource.Acme, certificates.Source);
            var issued = certificates.Certificate!;
            Assert.Equal(["inventory.example.test"], ServerCertificates.Names(issued));
            Assert.StartsWith("Issued for inventory.example.test", (await acme.LastAsync()).Result);
            await using (var check = db.CreateDbContext())
                Assert.Contains(await check.AuditEntries.Select(a => a.Action).ToListAsync(), a => a == "Let's Encrypt certificate issued");

            // Nach einem Neustart wieder da; noch nicht fällig, also kein neuer Antrag.
            var restarted = Certificates();
            await restarted.InitializeAsync();
            Assert.Equal((ServerCertificateSource.Acme, issued.Thumbprint), (restarted.Source, restarted.Certificate!.Thumbprint));
            await acme.RenewIfDueAsync();
            Assert.Equal(issued.Thumbprint, certificates.Certificate!.Thumbprint);

            // Ein Drittel der Laufzeit übrig: erneuert.
            var notAfter = new DateTimeOffset(issued.NotAfter.ToUniversalTime());
            clock.Now = notAfter - (issued.NotAfter - issued.NotBefore) / 3 + TimeSpan.FromDays(1);
            await acme.RenewIfDueAsync();
            Assert.NotEqual(issued.Thumbprint, certificates.Certificate!.Thumbprint);
            Assert.Equal(ServerCertificateSource.Acme, certificates.Source);
        }
        finally
        {
            System.IO.Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void A_certificate_is_due_when_a_third_of_its_lifetime_is_left()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=inventory.example.test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(now.AddDays(-60), now.AddDays(30));
        Assert.False(AcmeService.IsDue(certificate, now.UtcDateTime.AddDays(-1)));
        Assert.True(AcmeService.IsDue(certificate, now.UtcDateTime.AddHours(1)));
        Assert.Equal(["inventory.example.test", "*.example.test"], AcmeSettings.ParseNames(" Inventory.Example.Test.,*.example.test;inventory.example.test "));
    }

    [Fact]
    public async Task Azure_dns_gets_a_token_sets_and_removes_the_txt_record_of_the_zone()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host switch
        {
            "login.microsoftonline.com" => Json(new JsonObject { ["access_token"] = "token-1", ["expires_in"] = 3599 }),
            _ when request.Method == HttpMethod.Delete => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Json(new JsonObject()),
        });
        var dns = new AzureDnsChallenge(new HttpClient(handler),
            new AzureDnsSettings("contoso.onmicrosoft.com", "client-id", "secret", "sub-1", "rg-dns", "example.com."));

        await dns.SetTxtAsync("_acme-challenge.inventory.example.com", ["value-a", "value-b"], default);
        await dns.RemoveTxtAsync("_acme-challenge.inventory.example.com", default);

        var (tokenRequest, tokenBody) = handler.Requests[0];
        Assert.Equal("https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token", tokenRequest.RequestUri!.ToString());
        Assert.Contains("grant_type=client_credentials", tokenBody);
        Assert.Contains("scope=https%3A%2F%2Fmanagement.azure.com%2F.default", tokenBody);
        var record = "https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-dns/providers/Microsoft.Network/dnsZones/example.com/TXT/_acme-challenge.inventory?api-version=2018-05-01";
        var (put, putBody) = handler.Requests[1];
        Assert.Equal((HttpMethod.Put, record, "Bearer token-1"), (put.Method, put.RequestUri!.ToString(), put.Headers.Authorization!.ToString()));
        Assert.Equal("""{"properties":{"TTL":60,"TXTRecords":[{"value":["value-a"]},{"value":["value-b"]}]}}""", putBody);
        // Das Token gilt weiter; ein schon entfernter Eintrag ist kein Fehler.
        Assert.Equal((HttpMethod.Delete, record), (handler.Requests[2].Request.Method, handler.Requests[2].Request.RequestUri!.ToString()));
        Assert.Equal(3, handler.Requests.Count);

        Assert.Equal("@", AzureDnsChallenge.RelativeName("example.com", "example.com"));
        Assert.Throws<AcmeException>(() => AzureDnsChallenge.RelativeName("_acme-challenge.inventory.example.org", "example.com"));
        Assert.Equal("Client secret", new AzureDnsSettings("t", "c", " ", "s", "r", "z").Missing());
    }

    [Fact]
    public async Task Errors_of_azure_and_entra_reach_the_user_readably()
    {
        var entra = new AzureDnsChallenge(new HttpClient(new RecordingHandler(_ =>
                Json(new JsonObject { ["error"] = "invalid_client", ["error_description"] = "AADSTS7000215: Invalid client secret provided." }, HttpStatusCode.Unauthorized))),
            new AzureDnsSettings("t", "c", "wrong", "s", "r", "example.com"));
        Assert.Contains("AADSTS7000215", (await Assert.ThrowsAsync<AcmeException>(() => entra.SetTxtAsync("_acme-challenge.example.com", ["v"], default))).Message);

        var azure = new AzureDnsChallenge(new HttpClient(new RecordingHandler(request => request.RequestUri!.Host == "login.microsoftonline.com"
                ? Json(new JsonObject { ["access_token"] = "t" })
                : Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "AuthorizationFailed", ["message"] = "The client does not have authorization." } }, HttpStatusCode.Forbidden))),
            new AzureDnsSettings("t", "c", "s", "s", "r", "example.com"));
        Assert.Equal("Azure DNS: The client does not have authorization.",
            (await Assert.ThrowsAsync<AcmeException>(() => azure.SetTxtAsync("_acme-challenge.example.com", ["v"], default))).Message);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Pebble hat ein eigenes, unbekanntes Zertifikat für seine Schnittstelle; nur im Test wird jedes angenommen.</summary>
    internal static HttpClient PebbleClient() =>
        new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Der DNS-Testserver von Pebble als DNS-Anbieter.</summary>
    internal sealed class DnsTestServer(HttpClient http, string api) : IDnsChallenge
    {
        public List<string> Set { get; } = [];
        public List<string> Removed { get; } = [];

        public async Task SetTxtAsync(string name, IReadOnlyList<string> values, CancellationToken ct)
        {
            Set.Add(name);
            foreach (var value in values)
                (await http.PostAsJsonAsync($"{api}/set-txt", new { host = name + ".", value }, ct)).EnsureSuccessStatusCode();
        }

        public async Task RemoveTxtAsync(string name, CancellationToken ct)
        {
            Removed.Add(name);
            (await http.PostAsJsonAsync($"{api}/clear-txt", new { host = name + "." }, ct)).EnsureSuccessStatusCode();
        }
    }

    /// <summary>Setzt nichts: die Prüfung der Zertifizierungsstelle schlägt fehl.</summary>
    private sealed class WrongDns : IDnsChallenge
    {
        public Task SetTxtAsync(string name, IReadOnlyList<string> values, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveTxtAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
