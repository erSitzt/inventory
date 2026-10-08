# Inventory

Inventarisierung und Softwareverteilung für Windows-Rechner in einer Anwendung – agentenlos über hinterlegte
Zugangsdaten.

## Funktionen

- **Rechner** anlegen, aus dem Active Directory importieren, Erreichbarkeit prüfen
- **Inventar-Scan**: System, Betriebssystem, Prozessor, Software, Hotfixes, Datenträger, Netzwerk, Dienste
- **Sammlungen**: statisch oder dynamisch über Regeln (z. B. „Chrome installiert mit Version unter 141“)
- **Remote-Befehl** (cmd/PowerShell) mit Live-Ausgabe
- **Pakete** aus mehreren Schritten: Installation, Befehl, PowerShell, Dateien kopieren, Dienst, Warten, Neustart,
  Scan, eingebettetes Paket – jeweils mit Bedingungen und Fehlerverhalten
- **Deployments** parallel mit Live-Status, Abbruch, Wiederholung und Nachholen von Offline-Rechnern
- **Zeitpläne**, Software-Übersicht, CSV-Export, Protokoll, tägliche Datensicherung

## So funktioniert der Zugriff

Auf dem Zielrechner führt immer derselbe kleine **Runner** (`InventoryRunner.exe`, .NET Framework 4.8) einen Job aus.
Wie er dorthin kommt, bestimmt der Transport – einstellbar global und je Rechner:

| Transport | Voraussetzung auf dem Ziel | Job läuft als |
|---|---|---|
| SMB + Dienst (Standard) | Freigabe `ADMIN$`, Port 445, Konto mit lokalen Adminrechten | Lokales System |
| WinRM | PowerShell-Remoting aktiv, Port 5985, PowerShell im vollen Sprachmodus | hinterlegtes Konto |

| Agent (optional) | Dienst „Inventory Agent“, ausgehende Verbindung zum Server | Lokales System |

Auf dem Zielrechner entsteht `%SystemRoot%\Inventory` (nur SYSTEM und Administratoren): der Runner bleibt dort
liegen, Job-Verzeichnisse und der temporäre Dienst werden nach jedem Lauf entfernt.

### Agent

Für Rechner, die der Server nicht direkt erreicht (Notebooks unterwegs, andere Netze), gibt es denselben Runner als
dauerhaften Dienst. Er meldet sich einmalig mit dem Anmeldegeheimnis an, erhält einen eigenen Schlüssel und fragt
danach fortlaufend über HTTP(S) nach Jobs. Einrichtung:

1. Unter *Einstellungen → Agenten* die Adresse eintragen, unter der die Rechner den Server erreichen (HTTPS empfohlen).
2. Unter *Rechner* die Ziele auswählen und *Aktionen → Agent installieren* wählen. Die Installation
   selbst läuft agentenlos (SMB oder WinRM) nach `%ProgramFiles%\Inventory Agent`.
3. Sobald sich der Dienst meldet, steht der Rechner auf Transport „Agent“; je Rechner lässt sich das wieder umstellen.

Agenten aktualisieren sich selbst: Liefert der Server nach einem Update neuere Runner-Dateien aus, lädt der Agent sie
bei seiner nächsten Anfrage, tauscht sie aus und startet sich über die Wiederherstellung des Dienstes neu.

Zur Fehlersuche: `InventoryRunner.exe --agent-console` im Agentenordner, Protokoll in `agent.log`.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `Inventory.Contracts` | Job-Manifest und Ergebnisformat (von Server und Runner genutzt) |
| `Inventory.Core` | Modell, Regeln, Schnittstellen |
| `Inventory.Data` | EF Core/SQLite, Dienste (Scan, Pakete, Deployments, Zeitpläne …) |
| `Inventory.Remoting` | Transporte (SMB, WinRM, Simulation), Erreichbarkeit, LDAP |
| `Inventory.Runner` | Programm für den Zielrechner (net48) |
| `Inventory.Server` | ASP.NET Core: Blazor-Oberfläche, REST-API unter `/api`, Zeitplaner |

## Entwicklung unter WSL2

Das .NET 10 SDK liegt benutzerlokal unter `~/.dotnet` (`export PATH="$HOME/.dotnet:$PATH"`).

```bash
dotnet build && dotnet test          # baut auch den net48-Runner
dotnet run --project src/Inventory.Server --launch-profile http   # Simulation, http://localhost:5101
./scripts/run-win.sh                 # echter Windows-Prozess mit echten Transporten
```

- Unter Linux läuft der Server automatisch im **Simulationsmodus**: Transporte, Erreichbarkeit und AD liefern
  Beispieldaten. Schritte, deren Name „fehler“ enthält, schlagen in der Simulation fehl.
- Echte Zugriffe (SMB, Dienststeuerung, Kerberos) gehen nur als Windows-Prozess. `run-win.sh` veröffentlicht
  nach `%LOCALAPPDATA%\InventoryDev` und startet dort; Datenbank und Paketdateien liegen damit auf NTFS.
- Umgebungsspezifisches (z. B. die erlaubte Gruppe) gehört in `scripts/local.env`, das nicht eingecheckt wird;
  `run-win.sh` liest die Datei ein. Beispiel: `Inventory__AllowedGroups__0='FIRMA\Domänen-Admins'`
- Der Server selbst taugt nicht als SMB-Testziel: lokale Zugriffe nutzen immer das Konto des Server-Prozesses.

## Installation auf dem Server

```bash
./scripts/publish-win.sh             # erzeugt dist/Inventory
```

Den Ordner auf den Windows-Server kopieren und dort als Administrator ausführen:

```powershell
.\install-service.ps1 -AllowedGroups 'FIRMA\Domänen-Admins'
```

Das Skript installiert nach `C:\Program Files\Inventory`, legt Daten unter `C:\ProgramData\Inventory` ab und
registriert den Dienst. Ein erneuter Aufruf aktualisiert die Programmdateien; Konfiguration und Daten bleiben.

### Konfiguration (`appsettings.Production.json`)

| Schlüssel | Bedeutung |
|---|---|
| `Urls` | Adressen der Weboberfläche, z. B. `http://*:5100` |
| `Inventory:DataDirectory` | Datenbank, Schlüssel, Paketdateien (`repository`), Sicherungen (`backups`) |
| `Inventory:Authentication` | `Windows` (Standard auf Windows) oder `None` |
| `Inventory:AllowedGroups` | Gruppen mit Zugriff auf Oberfläche und API, als Name (`FIRMA\IT-Admins`) oder SID. Standard: lokale Administratoren (`S-1-5-32-544`) |
| `Inventory:Simulate` | `true` = keine echten Zugriffe |

Für eine vollständige Sicherung gehören neben `backups` auch die Ordner `keys` (ohne sie sind die gespeicherten
Passwörter nicht mehr lesbar) und `repository` dazu.
