<#
.SYNOPSIS
    Installiert oder aktualisiert Inventory als Windows-Dienst.
.DESCRIPTION
    Kopiert den Ordner "app" neben diesem Skript nach -InstallDir, legt beim ersten Mal die Konfiguration an und
    registriert den Dienst "Inventory". Daten (Datenbank, Schlüssel, Paketdateien, Sicherungen) liegen getrennt
    unter -DataDir und bleiben bei Updates erhalten.
.EXAMPLE
    .\install-service.ps1 -AllowedGroups 'FIRMA\Domänen-Admins' -ServiceAccount 'FIRMA\svc-inventory'
#>
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\Inventory",
    [string]$DataDir = "$env:ProgramData\Inventory",
    # Adresse(n), auf denen die Weboberfläche lauscht. Für HTTPS ein Zertifikat in appsettings.Production.json hinterlegen.
    [string]$Urls = 'http://*:5100',
    # Gruppen (Name oder SID), deren Mitglieder Inventory bedienen dürfen, z. B. 'FIRMA\Domänen-Admins'.
    # Standard: lokale Administratoren des Servers.
    [string[]]$AllowedGroups = @('S-1-5-32-544'),
    # Leer = LocalSystem. Ein Domänenkonto ist nötig, wenn Zielrechner ohne hinterlegte Zugangsdaten erreicht
    # oder das Active Directory mit dem Dienstkonto abgefragt werden soll.
    [string]$ServiceAccount
)
$ErrorActionPreference = 'Stop'
$serviceName = 'Inventory'
$source = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $source 'Inventory.Server.exe'))) { throw "Ordner 'app' mit Inventory.Server.exe fehlt neben diesem Skript." }

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host 'Beende laufenden Dienst ...'
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus('Stopped', '00:01:00')
}

Write-Host "Kopiere Programmdateien nach $InstallDir ..."
New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
# /MIR entfernt Dateien alter Versionen; die Konfiguration des Betreibers bleibt durch /XF erhalten.
robocopy $source $InstallDir /MIR /XF appsettings.Production.json /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Kopieren fehlgeschlagen (robocopy $LASTEXITCODE)." }

$settingsPath = Join-Path $InstallDir 'appsettings.Production.json'
if (-not (Test-Path $settingsPath)) {
    @{
        Urls      = $Urls
        Inventory = @{ DataDirectory = $DataDir; Authentication = 'Windows'; AllowedGroups = $AllowedGroups }
    } | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding UTF8
    Write-Host "Konfiguration angelegt: $settingsPath"
} else {
    Write-Host "Vorhandene Konfiguration bleibt unverändert: $settingsPath"
}

# Im Datenverzeichnis liegen verschlüsselte Zugangsdaten und Schlüssel: nur SYSTEM, Administratoren und das Dienstkonto.
$grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
if ($ServiceAccount) { $grants += "${ServiceAccount}:(OI)(CI)M" }
& icacls.exe $DataDir /inheritance:r /grant:r $grants | Out-Null

$binary = '"' + (Join-Path $InstallDir 'Inventory.Server.exe') + '"'
if (-not $service) {
    $create = @{ Name = $serviceName; BinaryPathName = $binary; DisplayName = 'Inventory'; StartupType = 'Automatic'
                 Description = 'Inventarisierung und Softwareverteilung' }
    if ($ServiceAccount) { $create.Credential = Get-Credential -UserName $ServiceAccount -Message 'Kennwort des Dienstkontos' }
    New-Service @create | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    Write-Host 'Dienst angelegt.'
}

Start-Service -Name $serviceName
Write-Host "Inventory läuft. Adresse: $($Urls -replace '\*', $env:COMPUTERNAME)"
Write-Host 'Hinweis: Falls von anderen Rechnern zugegriffen wird, den Port in der Windows-Firewall freigeben.'
