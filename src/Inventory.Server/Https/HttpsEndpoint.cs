using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Inventory.Contracts;
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
    ILogger<HttpsEndpoint> logger, IStringLocalizer<SharedResource>? localizer = null, AgentService? agents = null)
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

    /// <summary>In appsettings.json abgeschaltet (Inventory:Https:Enabled = false), etwa hinter einem Reverse-Proxy.</summary>
    public bool Disabled { get; private set; }

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

    /// <summary>
    /// Pins (<see cref="AgentProtocol.KeyPin"/>) des aktiven Zertifikats für die Agenten: so nutzen sie HTTPS auch, wenn sie
    /// der Kette nicht vertrauen (selbstsigniert, Macs ohne die Zertifizierungsstelle der Domäne). Leer ohne HTTPS.
    /// </summary>
    public IReadOnlyList<string> Pins => Active && certificates.Certificate is { } certificate ? [AgentProtocol.KeyPin(certificate)] : [];

    /// <summary>Einmal beim Start, nach der Migration der Datenbank und bevor Kestrel bindet.</summary>
    public async Task PrepareAsync(CancellationToken ct = default)
    {
        _prepared = true;
        if (!configuration.GetValue($"{InventoryOptions.SectionName}:Https:Enabled", true))
        {
            Disabled = true;
            await settings.SetAsync(SettingKeys.ServerHttpsActiveUrl, null, ct);
            logger.LogInformation("The built-in HTTPS endpoint is switched off (Inventory:Https:Enabled)");
            return;
        }
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
        else if (Scope(httpUrls).Select(b => PortProblem(b, Port)).FirstOrDefault(p => p is not null) is { } busy)
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
        agents?.OfferHttps(Address, Pins);
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
        foreach (var binding in Scope(httpUrls))
        {
            if (binding.Localhost)
                kestrel.ListenLocalhost(Port, UseHttps);
            else if (binding.Address is null)
                kestrel.ListenAnyIP(Port, UseHttps);
            else
                kestrel.Listen(binding.Address, Port, UseHttps);
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

    /// <summary>Wo HTTPS lauscht: auf allen Adressen, auf localhost (beide Loopback-Adressen) oder auf einer bestimmten Adresse.</summary>
    internal readonly record struct Binding(IPAddress? Address, bool Localhost = false)
    {
        public static readonly Binding AnyAddress = new(null);
        public static readonly Binding Loopback = new(null, Localhost: true);
    }

    /// <summary>
    /// Wo HTTPS lauscht, abgeleitet aus den HTTP-Adressen: * , + und Namen = alle Adressen, localhost = beide Loopback-Adressen,
    /// eine IP genau diese. Lauscht HTTP irgendwo auf allen, dann HTTPS auch.
    /// </summary>
    internal static List<Binding> Scope(IEnumerable<string> httpUrls)
    {
        var bindings = new List<Binding>();
        foreach (var url in httpUrls)
        {
            BindingAddress parsed;
            try
            {
                parsed = BindingAddress.Parse(url);
            }
            catch (FormatException)
            {
                continue;
            }
            if (parsed.IsUnixPipe)
                continue;
            Binding binding;
            if (parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                binding = Binding.Loopback;
            else if (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
                binding = new Binding(ip);
            else
                return [Binding.AnyAddress];
            if (!bindings.Contains(binding))
                bindings.Add(binding);
        }
        if (bindings.Count == 0)
            return [Binding.AnyAddress];
        // localhost deckt die einzelnen Loopback-Adressen ab; doppelt gebunden schlüge der Start fehl.
        if (bindings.Contains(Binding.Loopback))
            bindings.RemoveAll(b => b.Address is not null && IPAddress.IsLoopback(b.Address));
        return bindings;
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
    /// <param name="binding">Nur dort prüfen, wo HTTPS lauschen wird; so fragt Windows für eine lokale Adresse nicht nach der Firewall.</param>
    internal static string? PortProblem(Binding binding, int port)
    {
        if (binding.Localhost)
            return BindProblem(IPAddress.Loopback, port, dualMode: false)
                   ?? (Socket.OSSupportsIPv6 ? BindProblem(IPAddress.IPv6Loopback, port, dualMode: false, ignoreMissingIpv6: true) : null);
        if (binding.Address is not null)
            return BindProblem(binding.Address, port, dualMode: false);
        // Erst IPv4 für sich: Windows bindet [::] (dual mode) auch dann, wenn ein anderes Programm 0.0.0.0 schon hält, und
        // die IPv4-Verbindungen gingen weiter dorthin.
        if (BindProblem(IPAddress.Any, port, dualMode: false) is { } ipv4)
            return ipv4;
        return Socket.OSSupportsIPv6 ? BindProblem(IPAddress.IPv6Any, port, dualMode: true) : null;
    }

    private static string? BindProblem(IPAddress address, int port, bool dualMode, bool ignoreMissingIpv6 = false)
    {
        try
        {
            var listener = new TcpListener(address, port);
            if (dualMode)
                listener.Server.DualMode = true;
            listener.Start();
            listener.Stop();
            return null;
        }
        catch (SocketException ex) when ((dualMode || ignoreMissingIpv6) && ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported
                                         or SocketError.AddressNotAvailable)
        {
            // IPv6 abgeschaltet: Kestrel bindet dann nur IPv4, und das ist frei.
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
    /// Regel für HTTP angelegt hat (also nicht mit /NOFIREWALL installiert wurde). Woanders steht das nicht: das Setup schreibt
    /// die Konfiguration nur bei der ersten Installation. Erkannt wird die Regel daher an Name und HTTP-Port, wie das Setup sie
    /// anlegt; eine gleichnamige Regel für etwas anderes zählt nicht. Eine passende HTTPS-Regel bleibt unangetastet.
    /// </summary>
    private async Task EnsureFirewallRuleAsync()
    {
        if (!ServiceRestart.Available)
            return;
        try
        {
            var installer = await NetshAsync($"advfirewall firewall show rule name=\"{InstallerFirewallRule}\"");
            if (installer.ExitCode != 0 || !HttpUrls(configuration).Select(PortOf).OfType<int>().Any(p => MentionsPort(installer.Output, p)))
                return;
            var existing = await NetshAsync($"advfirewall firewall show rule name=\"{FirewallRule}\"");
            if (existing.ExitCode == 0 && MentionsPort(existing.Output, Port))
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

    /// <summary>Die Ausgabe von netsh ist übersetzt ("LocalPort"/"Lokaler Port"); die Portnummer als eigenes Wort genügt.</summary>
    internal static bool MentionsPort(string netshOutput, int port) => Regex.IsMatch(netshOutput, $@"(?<!\d){port}(?!\d)");

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
