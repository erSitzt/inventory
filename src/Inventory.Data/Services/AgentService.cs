using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Anmeldung und Erkennung von Agenten sowie der Job, der einen Agenten agentenlos installiert.</summary>
public sealed class AgentService(IDbContextFactory<InventoryDbContext> dbFactory, SettingsService settings, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null)
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    /// <summary>So oft wird "zuletzt gesehen" höchstens gespeichert; der Agent fragt alle paar Sekunden an.</summary>
    private static readonly TimeSpan SeenInterval = TimeSpan.FromMinutes(1);

    public Task<string?> GetServerUrlAsync(CancellationToken ct = default) => settings.GetAsync(SettingKeys.AgentServerUrl, ct);

    /// <summary>HTTPS-Adresse und Pins des Serverzertifikats, die Agenten angeboten werden.</summary>
    public sealed record HttpsOffer(string Url, IReadOnlyList<string> Pins);

    private volatile HttpsOffer? _https;

    /// <summary>Was der Server den Agenten an HTTPS anbietet; null ohne HTTPS.</summary>
    public HttpsOffer? Https => _https;

    /// <summary>Setzt der HTTPS-Endpunkt des Servers, beim Start und bei jeder Änderung von Adresse oder Zertifikat.</summary>
    public void OfferHttps(string? url, IReadOnlyList<string> pins) => _https = string.IsNullOrWhiteSpace(url) ? null : new HttpsOffer(url.TrimEnd('/'), pins);

    /// <summary>
    /// agent.json für eine neue Installation: die Adresse aus den Einstellungen und, wenn der Server HTTPS anbietet, dazu dessen
    /// Adresse und Pins. Der Agent verbindet sich dann gleich über HTTPS und nimmt die eingetragene Adresse nur als Rückfall.
    /// </summary>
    private async Task<string> NewConfigAsync(string serverUrl, CancellationToken ct)
    {
        var https = _https;
        return JobJson.Serialize(new AgentConfig
        {
            ServerUrl = serverUrl,
            EnrollmentToken = await GetEnrollmentTokenAsync(ct),
            SecureUrl = https is not null && !string.Equals(https.Url, serverUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? https.Url : null,
            CertificatePins = https is { Pins.Count: > 0 } ? [.. https.Pins] : null,
        });
    }

    public Task SetServerUrlAsync(string? url, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.AgentServerUrl, string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/'), ct);

    public async Task<string> GetEnrollmentTokenAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.AgentEnrollmentToken, ct) ?? await RenewEnrollmentTokenAsync(ct);

    /// <summary>Steht für "ohne Ablauf".</summary>
    public static readonly DateTime PublicDownloadForever = DateTime.MaxValue.Date;

    /// <summary>
    /// Bis wann die Download-Seite des Agenten ohne Anmeldung offen ist; null = geschlossen (auch wenn die Frist abgelaufen ist),
    /// <see cref="PublicDownloadForever"/> = ohne Ablauf.
    /// </summary>
    public async Task<DateTime?> GetPublicDownloadUntilAsync(CancellationToken ct = default)
    {
        if (!DateTime.TryParse(await settings.GetAsync(SettingKeys.AgentPublicDownloadUntilUtc, ct), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until))
            return null;
        return until > time.GetUtcNow().UtcDateTime ? until : null;
    }

    /// <summary>
    /// Räumt eine abgelaufene Frist ab und liefert true, wenn das gerade geschehen ist – einmal je Ablauf, damit der Takt
    /// das Schließen ins Protokoll schreiben kann.
    /// </summary>
    public async Task<bool> CloseExpiredPublicDownloadAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingKeys.AgentPublicDownloadUntilUtc, ct) is null || await GetPublicDownloadUntilAsync(ct) is not null)
            return false;
        await settings.SetAsync(SettingKeys.AgentPublicDownloadUntilUtc, null, ct);
        return true;
    }

    /// <param name="duration">null = schließen; <see cref="Timeout.InfiniteTimeSpan"/> = ohne Ablauf.</param>
    public Task SetPublicDownloadAsync(TimeSpan? duration, CancellationToken ct = default)
    {
        var until = duration is null ? (DateTime?)null : duration == Timeout.InfiniteTimeSpan ? PublicDownloadForever : time.GetUtcNow().UtcDateTime + duration.Value;
        return settings.SetAsync(SettingKeys.AgentPublicDownloadUntilUtc, until?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    /// <summary>
    /// Skript für eine PowerShell mit Administratorrechten: lädt das Agent-Setup von der öffentlichen Seite, entpackt es und
    /// richtet den Agenten ein. Nur Cmdlets, damit es auch im eingeschränkten Sprachmodus läuft.
    /// </summary>
    /// <exception cref="InvalidOperationException">Die Server-Adresse für Agenten ist nicht eingestellt.</exception>
    public async Task<string> BuildInstallScriptAsync(CancellationToken ct = default)
    {
        var serverUrl = await GetServerUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException(L["Please enter the server address for agents under Settings first."]);
        // Aufgeräumt wird auch nach einem Fehler: im entpackten Ordner liegt das Anmeldegeheimnis.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $folder = Join-Path $env:TEMP 'InventoryAgentSetup'
            $zip = "$folder.zip"
            $code = 1
            try {
                Remove-Item -Path $folder, $zip -Recurse -Force -ErrorAction SilentlyContinue
                Invoke-WebRequest -Uri '{{serverUrl.Replace("'", "''")}}/{{PublicSetupPath}}' -OutFile $zip -UseBasicParsing
                Expand-Archive -Path $zip -DestinationPath $folder -Force
                # Ohne .NET Framework 4.8 (Release 528040) startet der eigenständige Runner, wenn das Setup ihn enthält und
                # .NET 10 auf dem Rechner läuft: 64 Bit und Build 9200 (Server 2012) bis 10239 oder ab 14393 (Windows 10 1607).
                $runner = Join-Path $folder '{{JobFiles.RunnerExe}}'
                $large = Join-Path $folder '{{SelfContainedFolder}}\{{JobFiles.RunnerExe}}'
                $ndp = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
                $build = [int](Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber -ErrorAction SilentlyContinue).CurrentBuildNumber
                $supported = ($build -ge 9200 -and $build -lt 10240) -or $build -ge 14393
                if ((Test-Path $large) -and ${env:ProgramFiles(x86)} -and $supported -and -not ($ndp -and $ndp.Release -ge 528040)) { $runner = $large }
                & $runner {{RunnerModes.InstallAgent}}
                $code = $LASTEXITCODE
            }
            finally {
                Remove-Item -Path $folder, $zip -Recurse -Force -ErrorAction SilentlyContinue
            }
            if ($code -ne 0) { Write-Error "Agent setup failed with exit code $code" }

            """;
    }

    /// <summary>
    /// Installationsskript für den Mac (bash, als root): lädt den Agenten passend zur Architektur vom Server, richtet ihn als
    /// LaunchDaemon ein und startet ihn. Mit <c>--uninstall</c> entfernt es ihn wieder. Enthält das Anmeldegeheimnis.
    /// </summary>
    /// <exception cref="InvalidOperationException">Die Server-Adresse für Agenten ist nicht eingestellt.</exception>
    public async Task<string> BuildMacInstallScriptAsync(CancellationToken ct = default)
    {
        var (serverUrl, config) = await MacConfigAsync(ct);
        var token = await GetEnrollmentTokenAsync(ct);
        return Lf($$"""
            #!/bin/bash
            # Inventory-Agent für macOS: installiert oder aktualisiert den Agenten als LaunchDaemon; --uninstall entfernt ihn.
            # Als root ausführen, z. B.:  curl -fsSL {{serverUrl}}/{{PublicMacScriptPath}} | sudo bash
            set -euo pipefail
            SERVER={{Quote(serverUrl)}}
            TOKEN={{Quote(token)}}
            {{MacPrelude}}

            if [ "${1:-}" = '--uninstall' ]; then
              launchctl bootout system "$PLIST" 2>/dev/null || true
              rm -f "$PLIST"
              rm -rf "$DIR"
              pkgutil --forget {{MacAgentLabel}} >/dev/null 2>&1 || true
              echo 'Inventory agent removed.'
              exit 0
            fi

            {{MacPrepare}}
            HEADERS="$(mktemp "$DIR/headers.XXXXXX")"
            trap 'rm -f "$DOWNLOAD" "$HEADERS"' EXIT
            curl -fsSL -H "{{AgentProtocol.EnrollmentTokenHeader}}: $TOKEN" -D "$HEADERS" -o "$DOWNLOAD" "$SERVER/{{AgentProtocol.DownloadPath}}/$RUNTIME"
            HASH="$(grep -i '^{{AgentHashHeader}}:' "$HEADERS" | tail -n 1 | cut -d: -f2 | tr -d ' \r\n')"
            {{MacInstallSteps(config)}}
            echo 'Inventory agent installed. The Mac appears in Inventory under "Computers" in a few seconds.'

            """);
    }

    /// <summary>Ein Mac-Agent, den das Installationspaket mitbringt: Laufzeit, Datei und Stand (wie beim Download).</summary>
    public sealed record MacAgentBinary(string Runtime, string File, string Hash);

    public const string MacPackageFileName = "Inventory-Agent.pkg";

    /// <summary>
    /// Installationspaket für den Mac (.pkg, für Intune oder Jamf): bringt die Agenten für Apple Silicon und Intel mit, braucht
    /// bei der Installation also keine Verbindung zum Server. Der Agent meldet sich an, sobald er den Server erreicht (Büro, VPN).
    /// Unsigniert; enthält das Anmeldegeheimnis.
    /// </summary>
    /// <param name="version">Paketversion, unter der macOS die Installation verbucht (die des Servers).</param>
    /// <exception cref="InvalidOperationException">Die Server-Adresse ist nicht eingestellt, oder der Server bringt keinen Mac-Agenten mit.</exception>
    public async Task BuildMacPackageAsync(Stream output, IReadOnlyList<MacAgentBinary> agents, string version, CancellationToken ct = default)
    {
        var (_, config) = await MacConfigAsync(ct);
        // Das Paket ist für jeden Mac gedacht: ohne einen der beiden Agenten bräche die Installation auf einem Teil der Macs ab.
        if (!new[] { RunnerRuntimes.MacArm64, RunnerRuntimes.MacX64 }.All(runtime => agents.Any(a => a.Runtime == runtime)))
            throw new InvalidOperationException(L["This server does not include the macOS agent."]);
        var hashes = string.Join("\n", agents.Select(a => $"  {a.Runtime}) HASH={Quote(a.Hash)} ;;"));
        var postinstall = Lf($$"""
            #!/bin/bash
            # Inventory-Agent für macOS aus dem Installationspaket: richtet den mitgebrachten Agenten als LaunchDaemon ein. Den
            # Server braucht es dafür nicht; der Agent meldet sich an, sobald er ihn erreicht.
            set -euo pipefail
            SOURCE="$(cd "$(dirname "$0")" && pwd)"
            {{MacPrelude}}

            if [ "${3:-/}" != '/' ]; then
              echo 'The agent can only be installed on the startup volume.' >&2
              exit 1
            fi

            {{MacPrepare}}
            trap 'rm -f "$DOWNLOAD"' EXIT
            if [ ! -f "$SOURCE/$RUNTIME/{{AgentProtocol.MacAgentFile}}" ]; then
              echo "This package does not contain the agent for $RUNTIME." >&2
              exit 1
            fi
            case "$RUNTIME" in
            {{hashes}}
            esac
            cp "$SOURCE/$RUNTIME/{{AgentProtocol.MacAgentFile}}" "$DOWNLOAD"
            {{MacInstallSteps(config)}}
            echo 'Inventory agent installed.'

            """);
        var files = new List<MacPackageFile> { new(MacPackage.PostinstallScript, true, Content: Encoding.UTF8.GetBytes(postinstall)) };
        files.AddRange(agents.Select(a => new MacPackageFile($"{a.Runtime}/{AgentProtocol.MacAgentFile}", true, a.File)));
        MacPackage.Write(output, MacAgentLabel, version, files);
    }

    private async Task<(string ServerUrl, string Config)> MacConfigAsync(CancellationToken ct)
    {
        var serverUrl = await GetServerUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException(L["Please enter the server address for agents under Settings first."]);
        return (serverUrl, await NewConfigAsync(serverUrl, ct));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>bash verträgt keine Windows-Zeilenenden; der Quelltext hat zwar LF (.gitattributes), aber sicher ist sicher.</summary>
    private static string Lf(string script) => script.Replace("\r\n", "\n");

    /// <summary>Gemeinsamer Anfang der Mac-Skripte: Ordner, plist, nur als root.</summary>
    private static readonly string MacPrelude = $$"""
        DIR='{{MacAgentDirectory}}'
        PLIST='/Library/LaunchDaemons/{{MacAgentLabel}}.plist'

        if [ "$(id -u)" -ne 0 ]; then
          echo 'Please run as root (sudo).' >&2
          exit 1
        fi
        """;

    /// <summary>Architektur bestimmen, Ordner anlegen, Platz für die neue Agent-Datei ($DOWNLOAD).</summary>
    private static readonly string MacPrepare = $$"""
        # hw.optional.arm64 erkennt Apple Silicon auch dann, wenn das Skript unter Rosetta läuft (uname -m meldete dort x86_64).
        if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" = 1 ]; then
          RUNTIME='{{RunnerRuntimes.MacArm64}}'
        else
          RUNTIME='{{RunnerRuntimes.MacX64}}'
        fi

        umask 077
        mkdir -p "$DIR"
        chown root:wheel "$DIR"
        chmod 700 "$DIR"
        DOWNLOAD="$(mktemp "$DIR/download.XXXXXX")"
        """;

    /// <summary>Die neue Agent-Datei ($DOWNLOAD, Stand $HASH) signieren, an ihren Platz bringen und den LaunchDaemon (neu) starten.</summary>
    private static string MacInstallSteps(string config) => $$"""
        chmod 700 "$DOWNLOAD"
        # Auf Apple Silicon starten nur signierte Programme; eine Ad-hoc-Signatur genügt, Gatekeeper prüft nur Downloads aus dem Browser.
        xattr -c "$DOWNLOAD" 2>/dev/null || true
        codesign --force --sign - "$DOWNLOAD"

        launchctl bootout system "$PLIST" 2>/dev/null || true
        mv -f "$DOWNLOAD" "$DIR/{{AgentProtocol.MacAgentFile}}"
        cat > "$DIR/{{AgentProtocol.ConfigFile}}" <<'INVENTORY_CONFIG'
        {{config}}
        INVENTORY_CONFIG
        printf '%s' "$HASH" > "$DIR/{{AgentProtocol.VersionFile}}"

        cat > "$PLIST" <<'INVENTORY_PLIST'
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{{MacAgentLabel}}</string>
          <key>ProgramArguments</key>
          <array><string>{{MacAgentDirectory}}/{{AgentProtocol.MacAgentFile}}</string><string>{{RunnerModes.Agent}}</string></array>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><true/>
          <key>ThrottleInterval</key><integer>30</integer>
          <key>StandardErrorPath</key><string>{{MacAgentDirectory}}/stderr.log</string>
        </dict>
        </plist>
        INVENTORY_PLIST
        chown root:wheel "$PLIST"
        chmod 644 "$PLIST"
        launchctl bootstrap system "$PLIST"
        """;

    public const string MacAgentDirectory = AgentProtocol.MacAgentDirectory;
    public const string MacAgentLabel = AgentProtocol.MacAgentLabel;
    /// <summary>Antwortkopf des Downloads: der Stand der Agent-Dateien, den das Skript als agent.version ablegt.</summary>
    public const string AgentHashHeader = "X-Agent-Hash";
    public const string PublicMacScriptPath = "agent/macos/install.sh";
    public const string MacScriptFileName = "install-inventory-agent.sh";

    /// <summary>Prüft das Anmeldegeheimnis, ohne anzumelden (Download des Mac-Agenten).</summary>
    public async Task<bool> IsEnrollmentTokenAsync(string? token, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(token)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(await GetEnrollmentTokenAsync(ct)), Encoding.UTF8.GetBytes(token));

    public const string PublicPagePath = "agent";
    public const string PublicSetupPath = "agent/setup.zip";
    public const string PublicScriptPath = "agent/install.ps1";

    /// <summary>Neues Anmeldegeheimnis; bereits angemeldete Agenten bleiben gültig.</summary>
    public async Task<string> RenewEnrollmentTokenAsync(CancellationToken ct = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await settings.SetAsync(SettingKeys.AgentEnrollmentToken, token, ct);
        return token;
    }

    /// <summary>Meldet einen Agenten an und liefert seinen Schlüssel; null, wenn das Anmeldegeheimnis nicht stimmt.</summary>
    public async Task<string?> EnrollAsync(AgentEnrollRequest request, CancellationToken ct = default) => (await EnrollWithReasonAsync(request, ct)).Key;

    /// <summary>Ergebnis einer Anmeldung: der Schlüssel, oder warum der Server sie trotz gültigem Geheimnis abgelehnt hat.</summary>
    public sealed record Enrollment(string? Key, string? Refused = null);

    /// <summary>Gleichnamige Rechner einer anderen Plattform übernimmt ein Agent nie; die Anmeldung scheitert dann mit Begründung.</summary>
    public async Task<Enrollment> EnrollWithReasonAsync(AgentEnrollRequest request, CancellationToken ct = default)
    {
        var expected = await GetEnrollmentTokenAsync(ct);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.EnrollmentToken ?? ""))
            || string.IsNullOrWhiteSpace(request.HostName))
            return new Enrollment(null);

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var name = request.HostName.Trim();
        var platform = request.Platform == AgentPlatforms.MacOS ? OsPlatform.MacOS : OsPlatform.Windows;
        var computers = await db.Computers.ToListAsync(ct);
        // Ein Mac ist an seiner Seriennummer sicher wiederzuerkennen, auch wenn er inzwischen anders heißt.
        var computer = platform == OsPlatform.MacOS && !string.IsNullOrWhiteSpace(request.SerialNumber)
            ? computers.FirstOrDefault(c => c.Platform == platform && string.Equals(c.SerialNumber, request.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;
        // Auch unter dem FQDN eingetragene Rechner wiederfinden.
        var byName = computers.FirstOrDefault(c => c.Name.Split('.')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
        if (computer is null && byName is not null)
        {
            if (byName.Platform != platform)
                return new Enrollment(null, L["A computer named {0} exists already, but it is not a {1} computer; the agent was not enrolled.", name, platform == OsPlatform.MacOS ? "macOS" : "Windows"]);
            computer = byName;
        }
        // Umbenannter Mac: der Eintrag folgt dem neuen Namen, sonst passten Jobs und Namensprüfung nicht mehr zusammen.
        else if (computer is not null && byName is null && !computer.Name.Split('.')[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            computer.Name = name;
        if (computer is null)
        {
            computer = new Computer { Name = name, Platform = platform };
            db.Computers.Add(computer);
        }
        computer.SerialNumber ??= string.IsNullOrWhiteSpace(request.SerialNumber) ? null : request.SerialNumber.Trim();
        // Der Agent kennt sein DNS-Suffix; darüber erreichen ihn später auch agentenlose Wege und der Fernzugriff.
        // Was das Verzeichnis für einen dort bekannten Rechner geliefert hat, geht vor.
        if (computer.DnsName is null || computer.DirectorySeenUtc is null)
            computer.DnsName = MachineNames.ValidDnsName(request.DnsName, name) ?? computer.DnsName;
        computer.AgentKeyHash = Hash(key);
        computer.AgentVersion = request.Version;
        computer.AgentLastSeenUtc = time.GetUtcNow().UtcDateTime;
        computer.Transport = TransportKind.Agent;
        await db.SaveChangesAsync(ct);
        return new Enrollment(key);
    }

    /// <summary>Anzeigetext aus Programmversion und Stand der Dateien, z. B. "0.1.0.0 (3fa9c21b)".</summary>
    public static string DescribeVersion(string? version, string? runnerHash) =>
        string.IsNullOrEmpty(runnerHash) ? version ?? "unbekannt" : $"{version} ({runnerHash[..Math.Min(8, runnerHash.Length)]})";

    /// <summary>Erkennt den Rechner am Agentenschlüssel und vermerkt den Kontakt; null bei unbekanntem Schlüssel.</summary>
    /// <summary>Wie viele Rechner einen Agenten haben und wie viele davon zuletzt über HTTPS kamen.</summary>
    public async Task<(int Agents, int Https)> CountConnectionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agents = db.Computers.Where(c => c.AgentKeyHash != null);
        return (await agents.CountAsync(ct), await agents.CountAsync(c => c.AgentHttps == true, ct));
    }

    /// <param name="https">Ob die Anfrage über HTTPS kam; null = nicht vermerken.</param>
    public async Task<Computer?> AuthenticateAsync(string? key, string? version, CancellationToken ct = default, bool? https = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var hash = Hash(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.AgentKeyHash == hash, ct);
        if (computer is null)
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        if (computer.AgentLastSeenUtc is null || now - computer.AgentLastSeenUtc > SeenInterval || (version is not null && computer.AgentVersion != version)
            || (https is not null && computer.AgentHttps != https))
        {
            computer.AgentHttps = https ?? computer.AgentHttps;
            computer.AgentLastSeenUtc = now;
            computer.LastSeenUtc = now;
            computer.DirectoryInactive = false;
            computer.IsOnline = true;
            computer.AgentVersion = version ?? computer.AgentVersion;
            await db.SaveChangesAsync(ct);
        }
        return computer;
    }

    /// <summary>
    /// Entfernt den Agenten vom Rechner (Windows und Mac) und danach seine Zuordnung auf dem Server. Der Agent meldet zuerst
    /// das Ergebnis und entfernt sich erst dann selbst; scheitert der Auftrag, bleibt die Zuordnung.
    /// </summary>
    public async Task<JobResult> RemoveAsync(int computerId, RemoteJobService jobs, CancellationToken ct = default)
    {
        var job = new JobManifest { Steps = [new JobStep { Name = "Agent entfernen", Type = StepType.UninstallAgent, TimeoutSeconds = 120 }] };
        var result = await jobs.RunAsync(computerId, job, null, ct);
        if (result.Success)
            await ForgetAsync(computerId, ct);
        return result;
    }

    /// <summary>Entfernt die Agenten-Zuordnung; der Rechner wird wieder agentenlos angesprochen.</summary>
    public async Task ForgetAsync(int computerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Computers.Where(c => c.Id == computerId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.AgentKeyHash, (string?)null).SetProperty(c => c.Transport, (TransportKind?)null), ct);
    }

    /// <summary>
    /// Job, der den Agenten über einen agentenlosen Transport installiert oder aktualisiert:
    /// Runner-Dateien nach %ProgramFiles%\Inventory Agent, Konfiguration schreiben, Dienst anlegen und starten.
    /// Der Agent wird der Runner, der den Job ausführt (<see cref="RunnerRuntimes.DirectoryVariable"/>): auf einem Rechner
    /// ohne .NET Framework 4.8 hat der Transport bereits die eigenständige .NET-10-Variante gewählt.
    /// </summary>
    /// <exception cref="InvalidOperationException">Die Server-Adresse für Agenten ist nicht eingestellt.</exception>
    public async Task<BuiltJob> BuildInstallJobAsync(CancellationToken ct = default)
    {
        var serverUrl = await GetServerUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException(L["Please enter the server address for agents under Settings first."]);

        var config = await NewConfigAsync(serverUrl, ct);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $dir = Join-Path $env:ProgramFiles 'Inventory Agent'
            $name = '{{AgentProtocol.ServiceName}}'
            $service = Get-Service -Name $name -ErrorAction SilentlyContinue
            if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $name -Force; Start-Sleep -Seconds 2 }
            if (-not (Test-Path $dir)) {
                New-Item -ItemType Directory -Path $dir | Out-Null
                # Im Ordner liegen Anmeldegeheimnis und Schlüssel, und der Dienst läuft als SYSTEM: nur SYSTEM und Administratoren.
                & icacls.exe $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
            }
            # Der Runner liegt unter …\Runner\<Hash>; der Ordnername ist der Hash, den der Agent dem Server meldet.
            $source = $env:{{RunnerRuntimes.DirectoryVariable}}
            if (-not $source -or -not (Test-Path (Join-Path $source '{{JobFiles.RunnerExe}}'))) { throw 'Runner folder not found' }
            Get-ChildItem -Path $source -File | Where-Object { $_.Name -ne '.complete' } | Copy-Item -Destination $dir -Force
            Set-Content -Path (Join-Path $dir '{{AgentProtocol.VersionFile}}') -Value (Split-Path -Leaf $source) -Encoding ASCII
            Set-Content -Path (Join-Path $dir '{{AgentProtocol.ConfigFile}}') -Value '{{config.Replace("'", "''")}}' -Encoding UTF8
            if (-not $service) {
                New-Service -Name $name -BinaryPathName ('"' + (Join-Path $dir '{{JobFiles.RunnerExe}}') + '" {{RunnerModes.Agent}}') -DisplayName 'Inventory Agent' -StartupType Automatic | Out-Null
            }
            # Der erste Neustart kommt schnell: so startet sich der Agent nach einer Selbstaktualisierung neu.
            & sc.exe failure $name reset= 3600 actions= restart/5000/restart/60000/restart/300000 | Out-Null
            & sc.exe failureflag $name 1 | Out-Null
            Start-Service -Name $name
            Write-Output "Agent installiert: $dir"
            """;

        var manifest = new JobManifest
        {
            Steps = [new JobStep { Name = "Agent installieren", Type = StepType.PowerShell, Script = script, TimeoutSeconds = 300 }],
        };
        return new BuiltJob(manifest, []) { InstallsAgent = true };
    }

    public const string SetupFileName = "InventoryAgentSetup.zip";
    public const string SetupScript = "Install-Agent.cmd";
    /// <summary>Unterordner des Setup-Pakets mit dem eigenständigen .NET-10-Runner.</summary>
    public const string SelfContainedFolder = "net10";

    /// <summary>
    /// Agent-Setup zum Verteilen ohne agentenlosen Zugriff (Startskript einer Gruppenrichtlinie, vorhandene Softwareverteilung):
    /// Runner-Dateien, Konfiguration mit Server-Adresse und Anmeldegeheimnis, ein Startskript. Der Runner richtet sich daraus selbst ein.
    /// </summary>
    /// <param name="selfContainedFiles">
    /// Der eigenständige .NET-10-Runner mit seinem Hash; landet im Unterordner net10 und wird nur auf Rechnern ohne
    /// .NET Framework 4.8 gestartet. null, wenn der Server ihn nicht mitbringt.
    /// </param>
    /// <exception cref="InvalidOperationException">Die Server-Adresse für Agenten ist nicht eingestellt.</exception>
    public async Task<byte[]> BuildSetupAsync(IReadOnlyList<string> runnerFiles, string runnerHash, CancellationToken ct = default,
        IReadOnlyList<string>? selfContainedFiles = null, string? selfContainedHash = null)
    {
        var serverUrl = await GetServerUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException(L["Please enter the server address for agents under Settings first."]);
        var config = await NewConfigAsync(serverUrl, ct);

        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content, Encoding encoding)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), encoding);
                writer.Write(content);
            }
            var utf8 = new UTF8Encoding(false);
            // Der Runner richtet sich aus seinem eigenen Ordner ein: jede Variante bekommt Konfiguration und Version daneben.
            void AddRunner(string folder, IReadOnlyList<string> files, string hash)
            {
                foreach (var file in files)
                    zip.CreateEntryFromFile(file, folder + Path.GetFileName(file));
                Add(folder + AgentProtocol.ConfigFile, config, utf8);
                Add(folder + AgentProtocol.VersionFile, hash, Encoding.ASCII);
            }
            AddRunner("", runnerFiles, runnerHash);
            var withSelfContained = selfContainedFiles is { Count: > 0 } && selfContainedHash is not null;
            if (withSelfContained)
                AddRunner(SelfContainedFolder + "/", selfContainedFiles!, selfContainedHash!);
            // Läuft als Startskript unter Local System; bei vorhandenem Agenten kehrt der Runner sofort zurück. Ohne .NET
            // Framework 4.8 (Release 528040) startet der eigenständige Runner, wenn .NET 10 auf dem Rechner läuft:
            // 64 Bit und Build 9200 (Server 2012) bis 10239 oder ab 14393 (Windows 10 1607), wie beim Transport.
            var script = withSelfContained
                ? "@echo off\r\nsetlocal\r\nset release=0\r\nset build=0\r\nset large=0\r\n"
                  + "for /f \"tokens=3\" %%v in ('reg query \"HKLM\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\" /v Release 2^>nul') do set /a release=%%v\r\n"
                  + "for /f \"tokens=3\" %%v in ('reg query \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\" /v CurrentBuildNumber 2^>nul') do set /a build=%%v\r\n"
                  + "if %build% geq 9200 if %build% lss 10240 set large=1\r\n"
                  + "if %build% geq 14393 set large=1\r\n"
                  // Zuweisung in Anführungszeichen: ein & im Pfad des Setup-Ordners trennte sonst Befehle.
                  + $"set \"runner=%~dp0{JobFiles.RunnerExe}\"\r\n"
                  + $"if %release% lss 528040 if %large%==1 if defined ProgramFiles(x86) set \"runner=%~dp0{SelfContainedFolder}\\{JobFiles.RunnerExe}\"\r\n"
                  + $"\"%runner%\" {RunnerModes.InstallAgent} %*\r\nexit /b %errorlevel%\r\n"
                : $"@echo off\r\n\"%~dp0{JobFiles.RunnerExe}\" {RunnerModes.InstallAgent} %*\r\nexit /b %errorlevel%\r\n";
            Add(SetupScript, script, Encoding.ASCII);
            Add("README.txt", string.Join("\r\n",
                "Inventory agent setup",
                "",
                $"Install:   {SetupScript}            (as administrator or Local System)",
                $"Reinstall: {SetupScript} {RunnerModes.Force}",
                $"Remove:    {JobFiles.RunnerExe} {RunnerModes.UninstallAgent}",
                "",
                "The setup copies the agent to %ProgramFiles%\\Inventory Agent and registers the service InventoryAgent.",
                "On a computer that already has the agent it does nothing: the agent updates itself from the server.",
                $"Without .NET Framework 4.8 the setup uses the self-contained runner in the folder {SelfContainedFolder} (64-bit Windows only).",
                "",
                $"{AgentProtocol.ConfigFile} contains the enrollment secret of the server. Whoever can read it can enroll a computer.",
                "Keep this folder readable only for the computer accounts and administrators that need it.",
                ""), utf8);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Job, der den Starter für den Fernzugriff auf einem Admin-PC für alle Benutzer einrichtet: Programm nach
    /// %ProgramFiles%\Inventory Launcher kopieren und als Ziel für inventory://-Links eintragen.
    /// </summary>
    public static BuiltJob BuildLauncherInstallJob(string launcherDirectory)
    {
        const string folder = "launcher";
        const string target = @"%ProgramFiles%\Inventory Launcher";
        var files = Directory.GetFiles(launcherDirectory).Select(f => new JobFile(f, $@"{folder}\{Path.GetFileName(f)}")).ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("The launcher is not bundled with the server.");
        var manifest = new JobManifest
        {
            Steps =
            [
                new JobStep { Name = "Starter kopieren", Type = StepType.FileCopy, File = folder, Destination = target },
                new JobStep { Name = "Starter eintragen", Type = StepType.Command, Script = $"\"{target}\\InventoryLauncher.exe\" --register-machine", TimeoutSeconds = 60 },
            ],
        };
        return new BuiltJob(manifest, files);
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
