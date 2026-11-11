using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Https;

/// <summary>
/// HTTPS neben HTTP: ein eigener Port mit dem Zertifikat aus <see cref="ServerCertificateService"/>. Beim Start einmal
/// vorbereitet (<see cref="PrepareAsync"/>), danach bindet Kestrel über <see cref="Configure"/>. Port und Ein/Aus wirken erst
/// nach einem Neustart; Zertifikat, Adresse und Umleitung sofort.
/// </summary>
public sealed class HttpsEndpoint(ServerCertificateService certificates, SettingsService settings, IConfiguration configuration,
    ILogger<HttpsEndpoint> logger, IStringLocalizer<SharedResource>? localizer = null)
{
    public const int DefaultPort = 5101;
    /// <summary>Name der Firewall-Regel; die des Setups für HTTP heißt "Inventory".</summary>
    public const string FirewallRule = "Inventory HTTPS";
    private const string InstallerFirewallRule = "Inventory";

    /// <summary>Wege für Skripte, Agenten und Werkzeuge: sie bekämen von einer Umleitung nur einen Fehler.</summary>
    private static readonly string[] NotRedirected = ["/api", "/agent", "/mcp", "/download", "/_blazor", "/_framework", "/_content"];

    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;
    private bool _prepared;

    /// <summary>Kestrel lauscht auf <see cref="Port"/> mit HTTPS.</summary>
    public bool Active { get; private set; }

    /// <summary>HTTPS steht in appsettings.json (Abschnitt Kestrel oder eine https-Adresse); die Einstellungen ändern daran nichts.</summary>
    public bool ManagedByConfiguration { get; private set; }

    /// <summary>Der Port, auf dem HTTPS seit dem Start läuft (oder laufen sollte).</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>Warum HTTPS nicht läuft; null, wenn es läuft oder in appsettings.json eingerichtet ist.</summary>
    public string? Problem { get; private set; }

    /// <summary>Die Firewall-Regel ließ sich nicht anlegen.</summary>
    public string? FirewallProblem { get; private set; }

    /// <summary>Die HTTPS-Adresse für Browser und Links, z. B. https://inventory.firma.local:5101; null ohne HTTPS.</summary>
    public string? Address { get; private set; }

    /// <summary>Browser, die über HTTP kommen, landen auf <see cref="Address"/>.</summary>
    public bool RedirectBrowsers { get; private set; }

    /// <summary>Einmal beim Start, nach der Migration der Datenbank und bevor Kestrel bindet.</summary>
    public async Task PrepareAsync(CancellationToken ct = default)
    {
        _prepared = true;
        var httpUrls = HttpUrls(configuration);
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() || httpUrls.Any(u => u.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
        {
            ManagedByConfiguration = true;
            await settings.SetAsync(SettingKeys.ServerHttpsActiveUrl, null, ct);
            logger.LogInformation("HTTPS is configured in appsettings.json; the built-in HTTPS endpoint stays off");
            return;
        }

        Port = int.TryParse(await settings.GetAsync(SettingKeys.ServerHttpsPort, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port : DefaultPort;
        if (httpUrls.Any(u => PortOf(u) == Port))
            Problem = L["Port {0} is already used for HTTP.", Port];
        else if (Scope(httpUrls).Select(a => PortProblem(a, Port)).FirstOrDefault(p => p is not null) is { } busy)
            Problem = L["Port {0} cannot be used: {1}", Port, busy];
        else
        {
            try
            {
                await certificates.InitializeAsync(ct);
                Active = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "No HTTPS certificate could be loaded");
                Problem = L["No certificate could be loaded: {0}", ex.Message];
            }
        }
        if (Problem is not null)
            logger.LogWarning("HTTPS is off: {Problem}", Problem);

        await UpdateAsync(ct);
        certificates.Changed += () => _ = UpdateQuietlyAsync();
        if (Active)
        {
            logger.LogInformation("HTTPS on port {Port}: {Address}", Port, Address);
            _ = Task.Run(EnsureFirewallRuleAsync, CancellationToken.None);
        }
    }

    /// <summary>Adresse und Umleitung neu bestimmen, nach einer Änderung in den Einstellungen oder am Zertifikat.</summary>
    public async Task UpdateAsync(CancellationToken ct = default)
    {
        Address = Active ? NormalizeAddress(await settings.GetAsync(SettingKeys.ServerHttpsAddress, ct)) ?? await DeriveAddressAsync(ct) : null;
        var redirect = await settings.GetAsync(SettingKeys.ServerHttpsRedirect, ct);
        RedirectBrowsers = Active && (bool.TryParse(redirect, out var on) ? on : certificates.Source != ServerCertificateSource.SelfSigned);
        await settings.SetAsync(SettingKeys.ServerHttpsActiveUrl, Address, ct);
    }

    /// <summary>Für Kestrel (beim Start des Servers): die HTTP-Adressen aus der Konfiguration und dazu HTTPS.</summary>
    public void Configure(KestrelServerOptions kestrel)
    {
        if (!_prepared)
        {
            logger.LogWarning("Kestrel was configured before HTTPS was prepared; HTTPS stays off");
            return;
        }
        if (!Active)
            return;
        // Sobald Endpunkte im Code stehen, übergeht Kestrel die konfigurierten Adressen ("Urls"); also hier selbst binden.
        var httpUrls = HttpUrls(configuration);
        foreach (var url in httpUrls)
            Listen(kestrel, url);
        // HTTPS auf denselben Adressen wie HTTP: wer HTTP auf localhost oder eine Adresse beschränkt, meint HTTPS mit.
        foreach (var address in Scope(httpUrls))
        {
            if (address is null)
                kestrel.ListenAnyIP(Port, UseHttps);
            else if (IPAddress.IsLoopback(address))
                kestrel.ListenLocalhost(Port, UseHttps);
            else
                kestrel.Listen(address, Port, UseHttps);
        }
    }

    private void UseHttps(ListenOptions listen)
    {
        // Die Windows-Anmeldung (Negotiate) geht nicht über HTTP/2.
        listen.Protocols = HttpProtocols.Http1;
        listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = certificates.Context,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }),
        });
    }

    /// <summary>
    /// Wo HTTPS lauscht, abgeleitet aus den HTTP-Adressen: null = alle Adressen (*, +, Namen), <see cref="IPAddress.Loopback"/>
    /// für localhost, sonst die eingetragenen Adressen. Lauscht HTTP irgendwo auf allen, dann HTTPS auch.
    /// </summary>
    internal static List<IPAddress?> Scope(IEnumerable<string> httpUrls)
    {
        var addresses = new List<IPAddress?>();
        foreach (var url in httpUrls)
        {
            BindingAddress binding;
            try
            {
                binding = BindingAddress.Parse(url);
            }
            catch (FormatException)
            {
                continue;
            }
            if (binding.IsUnixPipe)
                continue;
            IPAddress? address = binding.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
                : IPAddress.TryParse(binding.Host.Trim('[', ']'), out var ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) ? ip
                : null;
            if (address is null)
                return [null];
            if (IPAddress.IsLoopback(address))
                address = IPAddress.Loopback;
            if (!addresses.Contains(address))
                addresses.Add(address);
        }
        return addresses.Count == 0 ? [null] : addresses;
    }

    /// <summary>Wohin eine Anfrage umgeleitet wird; null = keine Umleitung.</summary>
    public string? RedirectTarget(HttpRequest request)
    {
        if (!RedirectBrowsers || request.IsHttps || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            return null;
        if (NotRedirected.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            return null;
        // Nur Seitenaufrufe aus dem Browser; Skripte (curl, Invoke-WebRequest) schicken keins von beidem.
        var navigation = request.Headers["Sec-Fetch-Mode"] == "navigate" || request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        if (!navigation)
            return null;
        var target = Address ?? $"https://{request.Host.Host}{(Port == 443 ? "" : ":" + Port.ToString(CultureInfo.InvariantCulture))}";
        return target + request.PathBase + request.Path + request.QueryString;
    }

    /// <summary>Die HTTP-Adressen, auf die der Server ohne HTTPS hören würde.</summary>
    public static List<string> HttpUrls(IConfiguration configuration)
    {
        var urls = configuration[WebHostDefaults.ServerUrlsKey];
        if (!string.IsNullOrWhiteSpace(urls))
            return [.. urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        var ports = configuration["http_ports"];
        if (!string.IsNullOrWhiteSpace(ports))
            return [.. ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => $"http://*:{p}")];
        return ["http://localhost:5000"];
    }

    /// <summary>
    /// Eine Adresse aus "Urls" so binden, wie Kestrel es selbst täte: localhost auf beiden Loopback-Adressen, eine IP genau dort,
    /// alles andere (*, +, Namen) auf allen Adressen.
    /// </summary>
    internal static void Listen(KestrelServerOptions kestrel, string url)
    {
        var address = BindingAddress.Parse(url);
        if (address.IsUnixPipe)
            kestrel.ListenUnixSocket(address.UnixPipePath);
        else if (address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            kestrel.ListenLocalhost(address.Port);
        else if (IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip))
            kestrel.Listen(ip, address.Port);
        else
            kestrel.ListenAnyIP(address.Port);
    }

    internal static int? PortOf(string url)
    {
        try
        {
            var address = BindingAddress.Parse(url);
            return address.IsUnixPipe ? null : address.Port;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>null, wenn sich der Port binden lässt, sonst warum nicht (belegt, von Windows reserviert).</summary>
    /// <param name="address">null = alle Adressen; sonst nur dort, damit Windows für eine lokale Adresse nicht nach der Firewall fragt.</param>
    internal static string? PortProblem(IPAddress? address, int port)
    {
        if (address is not null)
        {
            try
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                listener.Stop();
                return null;
            }
            catch (SocketException ex)
            {
                return ex.Message;
            }
        }
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start();
            listener.Stop();
            return null;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            // Ohne IPv6 bindet Kestrel nur IPv4.
        }
        catch (SocketException ex)
        {
            return ex.Message;
        }
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return null;
        }
        catch (SocketException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>"https://host[:port]" aus einer Eingabe; ohne Schema gilt https. null, wenn leer oder keine Adresse.</summary>
    public static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Ohne eingetragene Adresse: der Host aus der Adresse für die Agenten, wenn das Zertifikat ihn abdeckt, sonst der erste
    /// Name im Zertifikat, sonst der Name des Rechners.
    /// </summary>
    private async Task<string?> DeriveAddressAsync(CancellationToken ct)
    {
        var certificate = certificates.Certificate;
        string? host = null;
        if (Uri.TryCreate(await settings.GetAsync(SettingKeys.AgentServerUrl, ct), UriKind.Absolute, out var agentUrl)
            && (certificate is null || ServerCertificates.Covers(certificate, agentUrl.IdnHost)))
            host = agentUrl.Host;
        host ??= certificate is null ? null : ServerCertificates.Names(certificate)
            .FirstOrDefault(n => !n.StartsWith("*.", StringComparison.Ordinal) && !n.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !IPAddress.TryParse(n, out _));
        host ??= ServerCertificates.MachineNames()[0];
        return $"https://{host}{(Port == 443 ? "" : ":" + Port.ToString(CultureInfo.InvariantCulture))}";
    }

    private async Task UpdateQuietlyAsync()
    {
        try
        {
            await UpdateAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Updating the HTTPS address failed");
        }
    }

    /// <summary>
    /// Als Dienst: die Regel für den HTTPS-Port anlegen oder auf den aktuellen Port setzen, aber nur, wenn das Setup die
    /// Regel für HTTP angelegt hat (also nicht mit /NOFIREWALL installiert wurde). Eine passende Regel bleibt unangetastet.
    /// </summary>
    private async Task EnsureFirewallRuleAsync()
    {
        if (!ServiceRestart.Available)
            return;
        try
        {
            if ((await NetshAsync($"advfirewall firewall show rule name=\"{InstallerFirewallRule}\"")).ExitCode != 0)
                return;
            var existing = await NetshAsync($"advfirewall firewall show rule name=\"{FirewallRule}\"");
            // Die Ausgabe ist übersetzt ("LocalPort"/"Lokaler Port"); die Portnummer als eigenes Wort genügt.
            if (existing.ExitCode == 0 && Regex.IsMatch(existing.Output, $@"(?<![\d]){Port}(?![\d])"))
                return;
            if (existing.ExitCode == 0)
                await NetshAsync($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
            var added = await NetshAsync($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={Port}");
            if (added.ExitCode != 0)
                throw new InvalidOperationException(added.Output.Trim());
            logger.LogInformation("Firewall rule \"{Rule}\" for port {Port} created", FirewallRule, Port);
        }
        catch (Exception ex)
        {
            FirewallProblem = L["The firewall rule for port {0} could not be created: {1}", Port, ex.Message];
            logger.LogWarning("Firewall rule for HTTPS could not be created: {Message}", ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output)> NetshAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("netsh could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
}

/// <summary>Leitet Seitenaufrufe aus dem Browser von HTTP auf HTTPS um, wenn das eingeschaltet ist.</summary>
public sealed class HttpsRedirectMiddleware(RequestDelegate next, HttpsEndpoint https)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (https.RedirectTarget(context.Request) is not { } target)
            return next(context);
        context.Response.Redirect(target, permanent: false, preserveMethod: true);
        return Task.CompletedTask;
    }
}
