using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Https;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.Tests;

/// <summary>HTTPS des Servers: Zertifikatsquellen, Wechsel ohne Neustart, Port, Adresse, Umleitung und echtes TLS über Kestrel.</summary>
public sealed class HttpsTests : IDisposable
{
    private const string Name = "inventory.example.test";
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"inventory-https-{Guid.NewGuid():N}");
    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);
    private readonly Alerts _alerts = new();
    private readonly List<X509Certificate2> _store = [];

    public HttpsTests()
    {
        _settings = new SettingsService(_db);
        _audit = new AuditService(_db);
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var certificate in _store)
            certificate.Dispose();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_self_signed_certificate_covers_the_server_names_lasts_at_most_825_days_and_keeps_its_key()
    {
        var service = Service();
        await service.InitializeAsync();

        var first = service.Certificate!;
        Assert.Equal(ServerCertificateSource.SelfSigned, service.Source);
        Assert.NotNull(service.Context);
        Assert.Null(service.Problem);
        Assert.Contains("localhost", ServerCertificates.Names(first));
        Assert.Contains(Dns.GetHostName().ToLowerInvariant(), ServerCertificates.Names(first));
        Assert.True(first.NotAfter - first.NotBefore <= TimeSpan.FromDays(825));
        Assert.True(ServerCertificates.IsForServers(first));
        Assert.Null(ServerCertificates.KeyProblem(first));
        var key = PublicKey(first);

        // Eine neue Adresse kommt ins Zertifikat, der Schlüssel bleibt (ein Pin der Agenten bleibt gültig).
        await _settings.SetAsync(SettingKeys.AgentServerUrl, $"http://{Name}:5100");
        await service.RefreshAsync();
        var withName = service.Certificate!;
        Assert.NotEqual(first.Thumbprint, withName.Thumbprint);
        Assert.Contains(Name, ServerCertificates.Names(withName));
        Assert.Equal(key, PublicKey(withName));

        // Kurz vor dem Ablauf erneuert, wieder mit demselben Schlüssel.
        _clock.Now = new DateTimeOffset(withName.NotAfter.ToUniversalTime()).AddDays(-59);
        await service.RefreshAsync();
        var renewed = service.Certificate!;
        Assert.True(renewed.NotAfter > withName.NotAfter);
        Assert.Equal(key, PublicKey(renewed));
        Assert.Contains(await Audit(), a => a.Action == "HTTPS certificate replaced");
        Assert.Empty(_alerts.Raised);

        // Nach einem Neustart dasselbe Zertifikat.
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal(renewed.Thumbprint, restarted.Certificate!.Thumbprint);
        Assert.Equal(renewed.Export(X509ContentType.Cert), restarted.ExportPublic());
    }

    [Fact]
    public async Task From_the_store_the_newest_valid_trusted_certificate_for_the_name_is_used_and_a_renewal_takes_over()
    {
        var now = _clock.Now;
        _store.Add(Certificate(Name, now.AddDays(-300), now.AddDays(30)));
        var newest = Certificate(Name, now.AddDays(-10), now.AddDays(300));
        _store.Add(newest);
        _store.Add(Certificate("other.example.test", now.AddDays(-1), now.AddDays(900)));
        _store.Add(Certificate(Name, now.AddDays(-400), now.AddDays(-1)));
        _store.Add(Certificate(Name, now.AddDays(-1), now.AddDays(800), usage: "1.3.6.1.5.5.7.3.2"));
        _store.Add(WithoutKey(Certificate(Name, now.AddDays(-1), now.AddDays(850))));
        _store.Add(Certificate(Name, now.AddDays(-1), now.AddDays(820), subject: "untrusted"));
        var service = Service();
        await service.InitializeAsync();

        Assert.Null(await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.MachineStore, StoreName: " " + Name + " "), "admin"));
        Assert.Equal(ServerCertificateSource.MachineStore, service.Source);
        Assert.Equal(newest.Thumbprint, service.Certificate!.Thumbprint);
        Assert.Equal(nameof(ServerCertificateSource.MachineStore), await _settings.GetAsync(SettingKeys.ServerCertificateSource));
        Assert.Equal(Name, await _settings.GetAsync(SettingKeys.ServerCertificateStoreName));
        Assert.Contains(await Audit(), a => a.Action == "HTTPS certificate changed" && a.User == "admin");

        // Die Liste für die Oberfläche: gültige Serverzertifikate mit Schlüssel, nicht das abgelaufene und nicht das für Clients.
        var candidates = service.StoreCandidates();
        Assert.Equal(4, candidates.Count);
        Assert.All(candidates, c => Assert.Null(c.KeyProblem));

        // Autoenrollment legt ein erneuertes dazu: es gilt beim nächsten Durchlauf.
        var renewal = Certificate(Name, now.AddDays(-1), now.AddDays(400));
        _store.Add(renewal);
        await service.RefreshAsync();
        Assert.Equal(renewal.Thumbprint, service.Certificate!.Thumbprint);

        // Ein Name ohne Zertifikat wird abgelehnt; das bisherige bleibt.
        var error = await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.MachineStore, StoreName: "missing.example.test"), "admin");
        Assert.Contains("missing.example.test", error);
        Assert.Equal(renewal.Thumbprint, service.Certificate!.Thumbprint);
        Assert.Equal(Name, await _settings.GetAsync(SettingKeys.ServerCertificateStoreName));
    }

    [Fact]
    public async Task Files_with_chain_and_password_and_uploads_are_used_and_unsuitable_ones_are_refused()
    {
        var now = _clock.Now;
        using var authority = Certificate("Example CA", now.AddDays(-30), now.AddDays(3000), authority: true);
        using var leaf = Certificate(Name, now.AddDays(-1), now.AddDays(90), issuer: authority);
        var pem = Path.Combine(_dataDirectory, "inventory.pem");
        var keyFile = Path.Combine(_dataDirectory, "inventory.key");
        await File.WriteAllTextAsync(pem, leaf.ExportCertificatePem() + "\n" + authority.ExportCertificatePem());
        await File.WriteAllTextAsync(keyFile, leaf.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var service = Service();
        await service.InitializeAsync();

        Assert.Null(await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.File, File: pem, KeyFile: keyFile), "admin"));
        Assert.Equal(leaf.Thumbprint, service.Certificate!.Thumbprint);
        Assert.Null(ServerCertificates.KeyProblem(service.Certificate));

        // PFX mit Passwort: das Passwort steht verschlüsselt in den Einstellungen und wird beim nächsten Speichern weiterverwendet.
        using var second = Certificate(Name, now.AddDays(-1), now.AddDays(120));
        var pfx = Path.Combine(_dataDirectory, "inventory.pfx");
        await File.WriteAllBytesAsync(pfx, second.Export(X509ContentType.Pkcs12, "geheim"));
        Assert.NotNull(await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.File, File: pfx, Password: "falsch"), "admin"));
        Assert.Equal(leaf.Thumbprint, service.Certificate!.Thumbprint);
        Assert.Null(await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.File, File: pfx, Password: "geheim"), "admin"));
        Assert.Equal(second.Thumbprint, service.Certificate!.Thumbprint);
        Assert.Equal("mieheg", await _settings.GetAsync(SettingKeys.ServerCertificatePassword));
        Assert.Null(await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.File, File: pfx), "admin"));

        // Ein anderes Werkzeug ersetzt die Datei: beim nächsten Durchlauf gilt das neue Zertifikat.
        using var third = Certificate(Name, now.AddDays(-1), now.AddDays(150));
        await File.WriteAllBytesAsync(pfx, third.Export(X509ContentType.Pkcs12, "geheim"));
        await service.RefreshAsync();
        Assert.Equal(third.Thumbprint, service.Certificate!.Thumbprint);

        // Hochgeladen: verschlüsselt abgelegt, nach einem Neustart wieder da.
        using var uploaded = Certificate(Name, now.AddDays(-1), now.AddDays(200));
        Assert.Null(await service.UploadAsync(uploaded.Export(X509ContentType.Pkcs12, "pw"), "pw", "admin"));
        Assert.Equal(ServerCertificateSource.Upload, service.Source);
        Assert.True((await File.ReadAllBytesAsync(Path.Combine(_dataDirectory, "certificates", "upload.pfx"))).AsSpan().IndexOf(uploaded.RawData) < 0);
        var restarted = Service();
        await restarted.InitializeAsync();
        Assert.Equal(uploaded.Thumbprint, restarted.Certificate!.Thumbprint);

        // Abgelaufen, nur für Clients, ohne Schlüssel, falsches Passwort: abgelehnt, das bisherige bleibt.
        using var expired = Certificate(Name, now.AddDays(-100), now.AddDays(-1));
        Assert.Contains("expired", await service.UploadAsync(expired.Export(X509ContentType.Pkcs12, "pw"), "pw", "admin"));
        using var client = Certificate(Name, now.AddDays(-1), now.AddDays(100), usage: "1.3.6.1.5.5.7.3.2");
        Assert.Contains("server authentication", await service.UploadAsync(client.Export(X509ContentType.Pkcs12, "pw"), "pw", "admin"));
        Assert.NotNull(await service.UploadAsync(uploaded.Export(X509ContentType.Pkcs12, "pw"), "anders", "admin"));
        Assert.NotNull(await service.UploadAsync(uploaded.Export(X509ContentType.Cert), null, "admin"));
        Assert.Contains("does not exist", await service.ApplyAsync(new CertificateChoice(ServerCertificateSource.File, File: Path.Combine(_dataDirectory, "fehlt.pfx")), "admin"));
        Assert.Equal(uploaded.Thumbprint, service.Certificate!.Thumbprint);
    }

    [Fact]
    public async Task A_broken_choice_falls_back_to_the_self_signed_certificate_and_an_expiring_one_raises_an_alert_once_a_day()
    {
        await _settings.SetAsync(SettingKeys.ServerCertificateSource, nameof(ServerCertificateSource.File));
        await _settings.SetAsync(SettingKeys.ServerCertificateFile, Path.Combine(_dataDirectory, "weg.pfx"));
        var service = Service();
        await service.InitializeAsync();
        Assert.Equal(ServerCertificateSource.SelfSigned, service.Source);
        Assert.Contains("does not exist", service.Problem);
        Assert.Equal(AlertKind.ServerCertificate, Assert.Single(_alerts.Raised).Kind);

        using var soon = Certificate(Name, _clock.Now.AddDays(-80), _clock.Now.AddDays(10));
        Assert.Null(await service.UploadAsync(soon.Export(X509ContentType.Pkcs12, "pw"), "pw", "admin"));
        Assert.Null(service.Problem);
        _clock.Now = _clock.Now.AddDays(1).AddMinutes(1);
        await service.RefreshAsync();
        Assert.Equal(2, _alerts.Raised.Count);
        Assert.Equal("HTTPS certificate expires soon", _alerts.Raised[^1].Title);
        await service.RefreshAsync();
        Assert.Equal(2, _alerts.Raised.Count);
        _clock.Now = _clock.Now.AddDays(1).AddMinutes(1);
        await service.RefreshAsync();
        Assert.Equal(3, _alerts.Raised.Count);
    }

    [Fact]
    public async Task An_undecryptable_stored_password_does_not_stop_the_start()
    {
        using var certificate = Certificate(Name, _clock.Now.AddDays(-1), _clock.Now.AddDays(90));
        var pfx = Path.Combine(_dataDirectory, "inventory.pfx");
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pkcs12, "geheim"));
        await _settings.SetAsync(SettingKeys.ServerCertificateSource, nameof(ServerCertificateSource.File));
        await _settings.SetAsync(SettingKeys.ServerCertificateFile, pfx);
        await _settings.SetAsync(SettingKeys.ServerCertificatePassword, "von einem anderen Rechner");
        var service = new ServerCertificateService(_settings, new BrokenProtector(), _protection, _audit, _clock, NullLogger<ServerCertificateService>.Instance,
            _dataDirectory, _alerts);

        await service.InitializeAsync();
        Assert.Equal(ServerCertificateSource.SelfSigned, service.Source);
        Assert.NotNull(service.Problem);
        Assert.Null((await service.ReadChoiceAsync(includePassword: true)).Password);
    }

    [Fact]
    public async Task Port_address_and_redirect_follow_the_settings_and_scripts_are_never_redirected()
    {
        var port = FreePort();
        await _settings.SetAsync(SettingKeys.ServerHttpsPort, port.ToString());
        await _settings.SetAsync(SettingKeys.AgentServerUrl, "http://localhost:5100");
        var https = Endpoint(Service(), "http://*:5100");
        await https.PrepareAsync();

        Assert.True(https.Active);
        Assert.Null(https.Problem);
        Assert.Equal(port, https.Port);
        Assert.Equal($"https://localhost:{port}", https.Address);
        Assert.Equal(https.Address, await _settings.GetAsync(SettingKeys.ServerHttpsActiveUrl));
        Assert.Equal(https.Address, await _settings.GetLinkBaseAsync());
        // Selbstsigniert: ohne ausdrückliche Wahl keine Umleitung, die Browser würden warnen.
        Assert.False(https.RedirectBrowsers);
        Assert.Null(https.RedirectTarget(Request("GET", "/computers", html: true)));

        await _settings.SetAsync(SettingKeys.ServerHttpsAddress, HttpsEndpoint.NormalizeAddress("inventory.example.test:8443/ignored"));
        await _settings.SetAsync(SettingKeys.ServerHttpsRedirect, bool.TrueString);
        await https.UpdateAsync();
        Assert.Equal("https://inventory.example.test:8443", https.Address);
        Assert.Equal("https://inventory.example.test:8443/computers/5?tab=software", https.RedirectTarget(Request("GET", "/computers/5", html: true, query: "?tab=software")));
        Assert.Equal("https://inventory.example.test:8443/", https.RedirectTarget(Request("HEAD", "/", navigate: true)));
        Assert.Null(https.RedirectTarget(Request("POST", "/computers", html: true)));
        Assert.Null(https.RedirectTarget(Request("GET", "/computers", html: true, secure: true)));
        Assert.Null(https.RedirectTarget(Request("GET", "/computers")));
        foreach (var path in new[] { "/api/computers", "/agent/install.ps1", "/agent", "/mcp", "/download/agent-setup", "/_blazor" })
            Assert.Null(https.RedirectTarget(Request("GET", path, html: true)));

        Assert.Null(HttpsEndpoint.NormalizeAddress("http://inventory.example.test"));
        Assert.Null(HttpsEndpoint.NormalizeAddress("  "));
        Assert.Equal("https://inventory.example.test", HttpsEndpoint.NormalizeAddress("https://inventory.example.test:443/"));
    }

    [Fact]
    public async Task Https_stays_off_with_a_busy_port_the_http_port_or_a_configuration_of_its_own()
    {
        using var busy = new TcpListener(IPAddress.Any, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        await _settings.SetAsync(SettingKeys.ServerHttpsPort, port.ToString());
        var blocked = Endpoint(Service(), "http://*:5100");
        await blocked.PrepareAsync();
        Assert.False(blocked.Active);
        Assert.Contains(port.ToString(), blocked.Problem);
        Assert.Null(await _settings.GetAsync(SettingKeys.ServerHttpsActiveUrl));
        busy.Stop();

        var same = Endpoint(Service(), $"http://localhost:{port}");
        await same.PrepareAsync();
        Assert.False(same.Active);
        Assert.Contains("HTTP", same.Problem);

        var manual = Endpoint(Service(), "http://*:5100", new() { ["Kestrel:Endpoints:Https:Url"] = "https://*:5443" });
        await manual.PrepareAsync();
        Assert.True(manual.ManagedByConfiguration);
        Assert.False(manual.Active);
        Assert.Null(manual.Problem);

        Assert.Equal(new List<string> { "http://*:5100", "http://localhost:5102" }, HttpsEndpoint.HttpUrls(Configuration(" http://*:5100 ; http://localhost:5102")));
        Assert.Equal(new List<string> { "http://*:8080" }, HttpsEndpoint.HttpUrls(Configuration(null, new() { ["http_ports"] = "8080" })));
        Assert.Equal(new List<string> { "http://localhost:5000" }, HttpsEndpoint.HttpUrls(Configuration(null)));

        // HTTPS lauscht dort, wo HTTP lauscht; eine bestimmte Adresse bleibt genau diese.
        var any = new List<HttpsEndpoint.Binding> { HttpsEndpoint.Binding.AnyAddress };
        Assert.Equal(any, HttpsEndpoint.Scope(["http://*:5100"]));
        Assert.Equal(any, HttpsEndpoint.Scope(["http://0.0.0.0:5100"]));
        Assert.Equal(any, HttpsEndpoint.Scope(["http://srv-inventory:5100"]));
        Assert.Equal(any, HttpsEndpoint.Scope(["http://localhost:5102", "http://+:5100"]));
        Assert.Equal([HttpsEndpoint.Binding.Loopback], HttpsEndpoint.Scope(["http://localhost:5102", "http://[::1]:5103", "http://127.0.0.1:5104"]));
        Assert.Equal([new HttpsEndpoint.Binding(IPAddress.Loopback), new HttpsEndpoint.Binding(IPAddress.Parse("10.1.2.3"))],
            HttpsEndpoint.Scope(["http://127.0.0.1:5100", "http://10.1.2.3:5100"]));
        Assert.Equal([new HttpsEndpoint.Binding(IPAddress.IPv6Loopback)], HttpsEndpoint.Scope(["http://[::1]:5100"]));

        // Die Regel des Setups erkennt der Server am HTTP-Port, auch in übersetzter Ausgabe von netsh.
        Assert.True(HttpsEndpoint.MentionsPort("Lokaler Port:                         5100\r\n", 5100));
        Assert.False(HttpsEndpoint.MentionsPort("LocalPort:                            51000\r\n", 5100));
    }

    [Fact]
    public async Task Kestrel_serves_http_and_https_and_a_new_certificate_without_a_restart()
    {
        var httpPort = FreePort();
        var httpsPort = FreePort();
        await _settings.SetAsync(SettingKeys.ServerHttpsPort, httpsPort.ToString());
        await _settings.SetAsync(SettingKeys.ServerHttpsRedirect, bool.TrueString);
        await _settings.SetAsync(SettingKeys.ServerHttpsAddress, $"https://localhost:{httpsPort}");
        var certificates = Service();
        var https = Endpoint(certificates, $"http://127.0.0.1:{httpPort}");
        await https.PrepareAsync();
        Assert.True(https.Active);

        await using var app = await ServeAsync(https);

        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            Assert.Equal("HTTP/1.1 False", await http.GetStringAsync($"http://127.0.0.1:{httpPort}/"));
            using var page = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{httpPort}/?x=1");
            page.Headers.Accept.ParseAdd("text/html");
            using var redirected = await http.SendAsync(page);
            Assert.Equal(HttpStatusCode.TemporaryRedirect, redirected.StatusCode);
            Assert.Equal($"https://localhost:{httpsPort}/?x=1", redirected.Headers.Location!.ToString());
        }

        var (text, served) = await GetOverTlsAsync(httpsPort);
        Assert.Equal("HTTP/1.1 True", text);
        Assert.Equal(certificates.Certificate!.Thumbprint, served);

        using var next = Certificate(Name, _clock.Now.AddDays(-1), _clock.Now.AddDays(60));
        Assert.Null(await certificates.UploadAsync(next.Export(X509ContentType.Pkcs12, "pw"), "pw", "admin"));
        Assert.Equal(next.Thumbprint, (await GetOverTlsAsync(httpsPort)).Thumbprint);
        await app.StopAsync();
    }

    /// <summary>
    /// Nur im Windows-Job der CI: ein Zertifikat im Speicher des Computers mit nicht exportierbarem Schlüssel, wie es
    /// Autoenrollment ablegt, über Schannel ausgeliefert. Ohne die Umgebungsvariablen geschieht nichts.
    /// </summary>
    [Fact]
    public async Task A_certificate_from_the_machine_store_of_windows_is_served_over_tls()
    {
        var name = Environment.GetEnvironmentVariable("INVENTORY_STORE_CERTIFICATE");
        var thumbprint = Environment.GetEnvironmentVariable("INVENTORY_STORE_THUMBPRINT");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(thumbprint) || !OperatingSystem.IsWindows())
            return;
        var httpsPort = FreePort();
        await _settings.SetAsync(SettingKeys.ServerHttpsPort, httpsPort.ToString());
        var certificates = new ServerCertificateService(_settings, new ReversingProtector(), _protection, _audit, TimeProvider.System,
            NullLogger<ServerCertificateService>.Instance, _dataDirectory, _alerts);
        var https = Endpoint(certificates, $"http://127.0.0.1:{FreePort()}");
        await https.PrepareAsync();
        await using var app = await ServeAsync(https);

        Assert.Contains(certificates.StoreCandidates(), c => c.Thumbprint == thumbprint && c.KeyProblem is null);
        Assert.Null(await certificates.ApplyAsync(new CertificateChoice(ServerCertificateSource.MachineStore, StoreName: name), "admin"));
        Assert.Equal(thumbprint, certificates.Certificate!.Thumbprint);
        var (text, served) = await GetOverTlsAsync(httpsPort);
        Assert.Equal("HTTP/1.1 True", text);
        Assert.Equal(thumbprint, served);
        await app.StopAsync();
    }

    private static async Task<WebApplication> ServeAsync(HttpsEndpoint https)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.ConfigureKestrel(kestrel => https.Configure(kestrel));
        var app = builder.Build();
        app.UseMiddleware<HttpsRedirectMiddleware>(https);
        app.MapGet("/", (HttpContext context) => $"{context.Request.Protocol} {context.Request.IsHttps}");
        await app.StartAsync();
        return app;
    }

    private static async Task<(string Text, string Thumbprint)> GetOverTlsAsync(int port)
    {
        string? thumbprint = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                thumbprint = certificate!.Thumbprint;
                return true;
            },
        };
        using var client = new HttpClient(handler);
        var text = await client.GetStringAsync($"https://127.0.0.1:{port}/");
        return (text, thumbprint!);
    }

    private ServerCertificateService Service() =>
        new(_settings, new ReversingProtector(), _protection, _audit, _clock, NullLogger<ServerCertificateService>.Instance, _dataDirectory, _alerts,
            machineStore: () => [.. _store.Select(Copy)], trusted: c => !c.Subject.Contains("untrusted"));

    private HttpsEndpoint Endpoint(ServerCertificateService certificates, string urls, Dictionary<string, string?>? extra = null) =>
        new(certificates, _settings, Configuration(urls, extra), NullLogger<HttpsEndpoint>.Instance);

    private static IConfiguration Configuration(string? urls, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>(extra ?? []);
        if (urls is not null)
            values["urls"] = urls;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static HttpRequest Request(string method, string path, bool html = false, bool navigate = false, bool secure = false, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = secure ? "https" : "http";
        context.Request.Host = new HostString("srv-inventory", 5100);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        if (html)
            context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        if (navigate)
            context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        return context.Request;
    }

    private async Task<List<AuditEntry>> Audit()
    {
        await using var db = _db.CreateDbContext();
        return await db.AuditEntries.ToListAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static byte[] PublicKey(X509Certificate2 certificate)
    {
        using var key = certificate.GetRSAPublicKey()!;
        return key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Wie aus dem Zertifikatspeicher: jedes Mal eine neue Instanz, die der Dienst wieder freigibt.</summary>
    private static X509Certificate2 Copy(X509Certificate2 certificate) => certificate.HasPrivateKey
        ? X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable)
        : X509CertificateLoader.LoadCertificate(certificate.RawData);

    private static X509Certificate2 WithoutKey(X509Certificate2 certificate)
    {
        using (certificate)
            return X509CertificateLoader.LoadCertificate(certificate.RawData);
    }

    private static X509Certificate2 Certificate(string name, DateTimeOffset from, DateTimeOffset to, X509Certificate2? issuer = null, bool authority = false,
        string usage = ServerCertificates.ServerAuthentication, string? subject = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={subject ?? name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        if (authority)
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        else
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
            var alternative = new SubjectAlternativeNameBuilder();
            alternative.AddDnsName(name);
            request.CertificateExtensions.Add(alternative.Build());
        }
        if (issuer is null)
            return request.CreateSelfSigned(from, to);
        using var signed = request.Create(issuer, from, to, RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => throw new CryptographicException("The key was not found in the key ring.");
    }

    private sealed class Alerts : IAlertSink
    {
        public List<AlertEvent> Raised { get; } = [];
        public void Raise(AlertEvent alert) => Raised.Add(alert);
    }
}
