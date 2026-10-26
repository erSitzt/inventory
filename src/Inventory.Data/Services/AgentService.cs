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
                # Ohne .NET Framework 4.8 (Release 528040) auf 64-Bit-Windows startet der eigenständige Runner, sofern dabei.
                $runner = Join-Path $folder '{{JobFiles.RunnerExe}}'
                $large = Join-Path $folder '{{SelfContainedFolder}}\{{JobFiles.RunnerExe}}'
                $ndp = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
                if ((Test-Path $large) -and ${env:ProgramFiles(x86)} -and -not ($ndp -and $ndp.Release -ge 528040)) { $runner = $large }
                & $runner {{RunnerModes.InstallAgent}}
                $code = $LASTEXITCODE
            }
            finally {
                Remove-Item -Path $folder, $zip -Recurse -Force -ErrorAction SilentlyContinue
            }
            if ($code -ne 0) { Write-Error "Agent setup failed with exit code $code" }

            """;
    }

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
    public async Task<string?> EnrollAsync(AgentEnrollRequest request, CancellationToken ct = default)
    {
        var expected = await GetEnrollmentTokenAsync(ct);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.EnrollmentToken ?? ""))
            || string.IsNullOrWhiteSpace(request.HostName))
            return null;

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var name = request.HostName.Trim();
        // Auch unter dem FQDN eingetragene Rechner wiederfinden.
        var computer = (await db.Computers.ToListAsync(ct)).FirstOrDefault(c => c.Name.Split('.')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
        if (computer is null)
        {
            computer = new Computer { Name = name };
            db.Computers.Add(computer);
        }
        // Der Agent kennt sein DNS-Suffix; darüber erreichen ihn später auch agentenlose Wege und der Fernzugriff.
        // Was das Verzeichnis für einen dort bekannten Rechner geliefert hat, geht vor.
        if (computer.DnsName is null || computer.DirectorySeenUtc is null)
            computer.DnsName = MachineNames.ValidDnsName(request.DnsName, name) ?? computer.DnsName;
        computer.AgentKeyHash = Hash(key);
        computer.AgentVersion = request.Version;
        computer.AgentLastSeenUtc = time.GetUtcNow().UtcDateTime;
        computer.Transport = TransportKind.Agent;
        await db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>Anzeigetext aus Programmversion und Stand der Dateien, z. B. "0.1.0.0 (3fa9c21b)".</summary>
    public static string DescribeVersion(string? version, string? runnerHash) =>
        string.IsNullOrEmpty(runnerHash) ? version ?? "unbekannt" : $"{version} ({runnerHash[..Math.Min(8, runnerHash.Length)]})";

    /// <summary>Erkennt den Rechner am Agentenschlüssel und vermerkt den Kontakt; null bei unbekanntem Schlüssel.</summary>
    public async Task<Computer?> AuthenticateAsync(string? key, string? version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var hash = Hash(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.AgentKeyHash == hash, ct);
        if (computer is null)
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        if (computer.AgentLastSeenUtc is null || now - computer.AgentLastSeenUtc > SeenInterval || (version is not null && computer.AgentVersion != version))
        {
            computer.AgentLastSeenUtc = now;
            computer.LastSeenUtc = now;
            computer.DirectoryInactive = false;
            computer.IsOnline = true;
            computer.AgentVersion = version ?? computer.AgentVersion;
            await db.SaveChangesAsync(ct);
        }
        return computer;
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

        var config = JobJson.Serialize(new AgentConfig { ServerUrl = serverUrl, EnrollmentToken = await GetEnrollmentTokenAsync(ct) });
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
        var config = JobJson.Serialize(new AgentConfig { ServerUrl = serverUrl, EnrollmentToken = await GetEnrollmentTokenAsync(ct) });

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
            // Framework 4.8 (Release 528040) auf 64-Bit-Windows startet der eigenständige Runner.
            var script = withSelfContained
                ? "@echo off\r\nsetlocal\r\nset release=0\r\n"
                  + "for /f \"tokens=3\" %%v in ('reg query \"HKLM\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\" /v Release 2^>nul') do set /a release=%%v\r\n"
                  // Zuweisung in Anführungszeichen: ein & im Pfad des Setup-Ordners trennte sonst Befehle.
                  + $"set \"runner=%~dp0{JobFiles.RunnerExe}\"\r\n"
                  + $"if %release% lss 528040 if defined ProgramFiles(x86) set \"runner=%~dp0{SelfContainedFolder}\\{JobFiles.RunnerExe}\"\r\n"
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
