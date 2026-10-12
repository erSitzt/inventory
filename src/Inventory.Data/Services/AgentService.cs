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


    private const string InstallFolder = "agent";
    /// <summary>So oft wird "zuletzt gesehen" höchstens gespeichert; der Agent fragt alle paar Sekunden an.</summary>
    private static readonly TimeSpan SeenInterval = TimeSpan.FromMinutes(1);

    public Task<string?> GetServerUrlAsync(CancellationToken ct = default) => settings.GetAsync(SettingKeys.AgentServerUrl, ct);

    public Task SetServerUrlAsync(string? url, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.AgentServerUrl, string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/'), ct);

    public async Task<string> GetEnrollmentTokenAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.AgentEnrollmentToken, ct) ?? await RenewEnrollmentTokenAsync(ct);

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
    /// </summary>
    /// <exception cref="InvalidOperationException">Die Server-Adresse für Agenten ist nicht eingestellt.</exception>
    public async Task<BuiltJob> BuildInstallJobAsync(IReadOnlyList<string> runnerFiles, string runnerHash, CancellationToken ct = default)
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
            Copy-Item -Path (Join-Path (Get-Location) '{{InstallFolder}}\*') -Destination $dir -Force
            Set-Content -Path (Join-Path $dir '{{AgentProtocol.VersionFile}}') -Value '{{runnerHash}}' -Encoding ASCII
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
        var files = runnerFiles.Select(f => new JobFile(f, $@"{InstallFolder}\{Path.GetFileName(f)}")).ToList();
        return new BuiltJob(manifest, files) { InstallsAgent = true };
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
