# Inventory

> **English summary:** Inventory is a self-hosted inventory and software deployment tool for Windows computers – an
> all-in-one tool that needs no agents on the clients. It works agentless (SMB + temporary service, or
> WinRM) with stored admin credentials and optionally with a self-updating agent. Download the setup from the
> [releases page](https://github.com/erSitzt/inventory/releases/latest) and run it on a Windows server. The user
> interface and this documentation are in German.

Inventarisierung und Softwareverteilung für Windows-Rechner in einer Anwendung – agentenlos über hinterlegte
Zugangsdaten.

![Übersicht](docs/screenshots/uebersicht.png)

## Funktionen

- **Rechner** anlegen, aus dem Active Directory importieren, Erreichbarkeit prüfen – auf Wunsch regelmäßig per Heartbeat (Rechner mit Namensauffälligkeiten ausgenommen)
- **Inventar-Scan**: System, Betriebssystem, Prozessor, Software, Hotfixes, Datenträger, Netzwerk, Dienste
- **Sammlungen**: statisch oder dynamisch über Regeln (z. B. „Chrome installiert mit Version unter 141“)
- **Remote-Befehl** (cmd/PowerShell) mit Live-Ausgabe
- **Pakete** aus mehreren Schritten: Installation, Befehl, PowerShell, Dateien kopieren, Dienst, Warten, Neustart,
  Scan, eingebettetes Paket – jeweils mit Bedingungen und Fehlerverhalten
- **Paketbibliothek**: fertige Pakete gängiger Programme (täglich aus den winget-Manifesten erzeugt), Import per
  Klick, automatische Übernahme neuer Versionen und eine mitwandernde Sammlung „… veraltet“
- **Ausführungskontext** je Schritt: als Lokales System oder in der Sitzung des angemeldeten Benutzers
- **Fernzugriff** je Rechner: Remoteunterstützung anbieten, Sitzung spiegeln, Remotedesktop, TeamViewer/AnyDesk
- **Deployments** parallel mit Live-Status, Abbruch, Wiederholung und Nachholen von Offline-Rechnern
- **Zeitpläne**, Software-Übersicht, CSV-Export, Protokoll, tägliche Datensicherung

## Screenshots

| | |
|---|---|
| **Rechnerliste** mit Status, Sicherheitsampel und Filtern ![Rechner](docs/screenshots/rechner.png) | **Rechner im Detail** – Inventar, Software, Hotfixes, Dienste, Sicherheit, Verlauf ![Rechner-Detail](docs/screenshots/rechner-detail.png) |
| **Paketbibliothek** – fertige Pakete aus winget, Setup-Art auf einen Blick ![Paketbibliothek](docs/screenshots/bibliothek.png) | **Deployment** – Fortschritt je Rechner, Offline-Rechner werden nachgeholt ![Deployment](docs/screenshots/deployment.png) |
| **Dynamische Sammlungen** – z. B. alle Rechner mit veralteter Version ![Sammlungen](docs/screenshots/sammlungen.png) | **Zeitpläne** für Scans und Verteilungen ![Zeitpläne](docs/screenshots/zeitplaene.png) |

Die Bilder stammen aus dem Simulationsmodus (`Inventory:Simulate`), der ohne echte Rechner eine Beispielumgebung zeigt.

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

### Fernzugriff

In der Rechnerliste und auf der Detailseite gibt es das Menü *Fernzugriff*:

| Weg | Was gestartet wird | Voraussetzung auf den Rechnern |
|---|---|---|
| Remoteunterstützung anbieten | `msra.exe /offerRA <Rechner>` | Richtlinie „Remoteunterstützung anbieten konfigurieren“, Firewall-Gruppe „Remoteunterstützung“ |
| Sitzung spiegeln | `mstsc.exe /v:<Rechner> /shadow:<Sitzung> /control`; die Sitzungsnummer stammt aus dem letzten Scan | Richtlinie „Regeln für Remotesteuerung von Remotedesktopdienste-Benutzersitzungen festlegen“, Firewall-Regeln „Remotedesktop – Schatten“ |
| Remotedesktop | `mstsc.exe /v:<Rechner>` | Remotedesktop aktiviert |
| TeamViewer / AnyDesk | das Programm über dessen eigenen Link mit der beim Scan erfassten Geräte-ID | Programm auf beiden Seiten installiert |

Damit die ersten drei per Klick starten, braucht der Admin-PC einmalig den **Starter** (`InventoryLauncher.exe`, wenige
Kilobyte): im Menü unter „Starter einrichten“ herunterladen, an einem festen Ort ablegen und einmal doppelklicken. Er
trägt sich für das Benutzerkonto als Ziel für `inventory://`-Links ein (ohne Adminrechte). Für mehrere Admin-PCs gibt
es unter *Rechner → Aktionen* „Starter für Fernzugriff installieren“ (nach `%ProgramFiles%\Inventory Launcher`, für
alle Benutzer). Der Starter nimmt nur diese drei Aktionen an, prüft Rechnername und Sitzungsnummer und startet
ausschließlich `msra.exe` und `mstsc.exe` aus System32 – andere Webseiten können über den Link nichts anderes auslösen.

Ohne Starter zeigt derselbe Dialog die Befehle zum Kopieren und bietet Remotedesktop als `.rdp`-Datei an.

Jeder Aufruf steht im Protokoll. Antwortet unter dem eingetragenen Namen ein anderer Rechner, warnt das Menü.

### Active Directory

Die Synchronisation (*Einstellungen → Active Directory*) legt Computerkonten als Rechner an und gleicht vorhandene ab:

- **Deaktivierte Computerkonten ausschließen** (Standard: an): deaktivierte Konten werden nicht angelegt; ist ein
  vorhandener Rechner im Verzeichnis deaktiviert, gilt er als nicht mehr gefunden.
- **Nicht mehr gefundene Rechner** werden als „nicht im AD“ gekennzeichnet (Filter in der Rechnerliste, Regelfeld für
  dynamische Sammlungen). Wahlweise löscht die Synchronisation sie, nachdem sie eine einstellbare Frist lang
  ununterbrochen gefehlt haben – samt Inventardaten und Verlauf.
- Von Hand angelegte Rechner, die das Verzeichnis nie gekannt hat, bleiben unberührt. Liefert das Verzeichnis gar keine
  Konten oder wären auffällig viele Rechner auf einmal zu löschen, ändert bzw. löscht der Lauf nichts und meldet das.

### Paketbibliothek

Die Action `.github/workflows/catalog.yml` liest täglich die Manifeste der in `catalog/apps.txt` aufgeführten Programme
aus dem [winget-Repository](https://github.com/microsoft/winget-pkgs) und schreibt `catalog.json` in den Branch
`catalog`: je Programm aktuelle Version, Download-Adresse des Herstellers, SHA-256 und Silent-Parameter. Der Server
lädt diese Datei (`Inventory:Catalog:Url`), zeigt sie unter *Paketbibliothek* an und lädt beim Import das Setup direkt
vom Hersteller; stimmt die Prüfsumme nicht, wird nichts übernommen.

`catalog/apps.txt` ist die Grundauswahl gängiger Programme, die für alle Benutzer installieren. Daneben erzeugt die Action
`winget.json`, einen Suchindex über das gesamte winget-Repository mit allen Programmen, die ein unbeaufsichtigt
installierbares MSI- oder EXE-Setup haben (`Inventory:Catalog:IndexUrl`). Die Suche der Paketbibliothek zeigt Treffer daraus
unter „Weitere Treffer aus winget“; sie lassen sich genauso importieren und aktuell halten. Diese Einträge sind unverändert
aus den Manifesten übernommen – vor dem Verteilen an einem einzelnen Rechner testen. Setups, die nur ins Benutzerprofil
installieren, erhalten beim Import den Kontext „angemeldeter Benutzer“.

Ältere Versionen: Der Import-Dialog bietet alle Versionen an, die das winget-Repository noch führt (je Programm höchstens
die 60 neuesten, `Inventory:Catalog:VersionsUrl`). Eine ältere Version wird als eigenständiges Paket „Name Version“
angelegt, das nie automatisch aktualisiert wird und neben dem aktuellen Paket bestehen kann. Nimmt der Hersteller ein
altes Setup vom Server, schlägt der Download fehl.

- Weitere Programme in die Grundauswahl: winget-ID in `catalog/apps.txt` ergänzen.
- Eigener Katalog: `Inventory:Catalog:Url` auf eine eigene Datei oder Adresse zeigen lassen (gleiches Format).
- Programme automatisch aktuell halten: beim Import die Sammlung „… veraltet“ anlegen lassen und einen Zeitplan
  einrichten, der das Paket regelmäßig auf diese Sammlung bereitstellt.

### Ausführungskontext

Befehle, PowerShell-Skripte und Installationen laufen standardmäßig als **Lokales System**. Für alles, was das
Benutzerprofil betrifft (HKCU, AppData, Netzlaufwerke), lässt sich je Schritt **Angemeldeter Benutzer** wählen: Der
Runner startet den Prozess dann in der aktiven Sitzung mit den Rechten dieses Benutzers. Ist niemand angemeldet, wird
der Schritt übersprungen. Das setzt voraus, dass der Runner als Lokales System läuft (SMB + Dienst oder Agent, nicht
WinRM). Skript und Paketdateien liegen dafür während des Laufs in einem für Benutzer lesbaren Ordner unter
`%ProgramData%\InventoryRunner` – in solche Schritte gehören deshalb keine Geheimnisse.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `Inventory.Contracts` | Job-Manifest und Ergebnisformat (von Server und Runner genutzt) |
| `Inventory.Core` | Modell, Regeln, Schnittstellen |
| `Inventory.Data` | EF Core/SQLite, Dienste (Scan, Pakete, Deployments, Zeitpläne …) |
| `Inventory.Remoting` | Transporte (SMB, WinRM, Simulation), Erreichbarkeit, LDAP |
| `Inventory.Runner` | Programm für den Zielrechner (net48) |
| `Inventory.Launcher` | Starter für den Admin-PC: öffnet Fernzugriffe aus `inventory://`-Links (net48) |
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

## Installation

1. Von der [Release-Seite](https://github.com/erSitzt/inventory/releases/latest) `Inventory-Setup-x.y.z.exe` laden.
2. Auf dem Windows-Server als Administrator ausführen. Der Assistent fragt nach Port, erlaubten Windows-Gruppen und
   Dienstkonto, legt den Dienst „Inventory“ an und gibt auf Wunsch den Port in der Firewall frei.
3. Die Oberfläche unter `http://<server>:5100` öffnen.

Programmdateien liegen unter `C:\Program Files\Inventory`, Daten und Einstellungen unter `C:\ProgramData\Inventory`.
Auf dem Server muss nichts weiter installiert sein (.NET ist enthalten).

Unbeaufsichtigt:

```
Inventory-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PORT=5100 /GROUPS="FIRMA\Domänen-Admins"
```

Weitere Schalter: `/SERVICEACCOUNT="FIRMA\svc-inventory" /SERVICEPASSWORD=…`, `/NOFIREWALL`, `/LOG=setup.log`.
Zu jeder Datei eines Releases steht die SHA-256-Prüfsumme in `SHA256SUMS.txt`.

## Aktualisieren

- **In der Oberfläche:** Der Server fragt alle paar Stunden bei GitHub nach. Gibt es eine neuere Version, erscheint oben
  ein Hinweis; unter *Einstellungen → Updates* stehen die Änderungen und der Knopf „Jetzt aktualisieren“. Dort lässt sich
  auch die automatische Aktualisierung in einem nächtlichen Wartungsfenster einschalten (Standard: aus).
- **Von Hand:** das neue Setup über die vorhandene Installation ausführen. Einstellungen und Daten bleiben erhalten.

Vor jeder Aktualisierung über die Oberfläche wird die Datenbank gesichert, laufende Deployments werden abgewartet, und
das Ergebnis steht im Protokoll. Agenten aktualisieren sich danach selbst. Ein Zurück auf eine ältere Version geht nur
über die Sicherung (`backups`), weil neuere Versionen das Datenbankschema erweitern.

Die Selbstaktualisierung setzt voraus, dass der Dienst als Lokales System oder mit lokalen Administratorrechten läuft.
Abschalten lässt sich die Update-Prüfung mit `Inventory:Update:Enabled = false`.

> **Zur Vertrauenskette:** Über Updates gelangt neuer Programmcode auf den Server und über die Agenten auf alle
> verwalteten Rechner. Releases sind derzeit **nicht signiert**; die Prüfsumme sichert nur die Übertragung, nicht die
> Herkunft. Wer das absichern will, lässt die Automatik aus und prüft Releases vor dem Einspielen. Sobald Releases
> signiert sind, erzwingt `Inventory:Update:RequiredPublisher` die Prüfung des Herausgebers.

### Release erstellen (für Maintainer)

Ein Tag `vX.Y.Z` startet die Pipeline (`.github/workflows/release.yml`): Tests, Veröffentlichung, Setup, ZIP,
Prüfsummen, GitHub-Release mit automatisch erzeugten Notizen. Tags mit Zusatz (`v1.2.0-rc1`) werden als Vorabversion
markiert und von der Update-Prüfung ignoriert. Liegen die Secrets `SIGNING_CERT_PFX` (Base64 der PFX-Datei) und
`SIGNING_CERT_PASSWORD` vor, signiert die Pipeline Server, Runner und Setup.

### Konfiguration (`C:\ProgramData\Inventory\appsettings.json`)

| Schlüssel | Bedeutung |
|---|---|
| `Urls` | Adressen der Weboberfläche, z. B. `http://*:5100` |
| `Inventory:DataDirectory` | Datenbank, Schlüssel, Paketdateien (`repository`), Sicherungen (`backups`) |
| `Inventory:Authentication` | `Windows` (Standard auf Windows), `Oidc`, beides (`Windows,Oidc`) oder `None` |
| `Inventory:AllowedGroups` | Windows-Gruppen mit Zugriff, als Name (`FIRMA\IT-Admins`) oder SID. Standard: lokale Administratoren (`S-1-5-32-544`) |
| `Inventory:Oidc:TenantId` | Entra-Mandant (GUID oder Domäne); für andere Anbieter stattdessen `Inventory:Oidc:Authority` |
| `Inventory:Oidc:ClientId`, `ClientSecret` | Aus der App-Registrierung |
| `Inventory:Oidc:AllowedGroups` | Objekt-IDs der erlaubten Gruppen oder Namen von App-Rollen; ohne Eintrag startet der Server nicht |
| `Inventory:Update:Enabled`, `Repository`, `RequiredPublisher` | Update-Prüfung, Quelle der Releases, erwarteter Herausgeber der Signatur |
| `Inventory:Catalog:Url` | Adresse oder Pfad der `catalog.json` der Paketbibliothek |
| `Inventory:Catalog:VersionsUrl` | Ordner mit den Versionslisten (`<winget-ID>.json`); leer = nur die aktuelle Version anbieten |
| `Inventory:Catalog:IndexUrl` | Adresse oder Pfad des Suchindex `winget.json`; leer = Suche in ganz winget abschalten |
| `Inventory:Simulate` | `true` = keine echten Zugriffe |

### Anmeldung

Windows-Anmeldung und OIDC lassen sich einzeln oder gemeinsam aktivieren. Sind beide aktiv, erscheint eine Auswahlseite;
beide Wege führen zur selben Sitzung. Die Agenten-Schnittstelle ist davon unabhängig (eigene Schlüssel).

**Microsoft Entra ID einrichten**

1. Im Entra Admin Center unter *App-Registrierungen* eine neue Registrierung anlegen, Plattform **Web**, Umleitungs-URI
   `https://<server>:<port>/signin-oidc`. Entra akzeptiert nur HTTPS (Ausnahme: `http://localhost`).
2. Unter *Zertifikate & Geheimnisse* einen geheimen Clientschlüssel erzeugen.
3. Berechtigung in das Token bringen – eine der beiden Varianten:
   - *Tokenkonfiguration → Gruppenanspruch hinzufügen → Sicherheitsgruppen* (ID-Token, Gruppen-ID). In
     `Inventory:Oidc:AllowedGroups` kommen dann die **Objekt-IDs** der Gruppen. Bei Benutzern mit mehr als 200 Gruppen
     lässt Entra den Anspruch weg; dann die zweite Variante nehmen.
   - *App-Rollen* anlegen und Benutzern oder Gruppen zuweisen (Unternehmensanwendung → Benutzer und Gruppen). In
     `AllowedGroups` kommt der **Wert** der Rolle.
4. Konfiguration ergänzen (das Geheimnis gehört nicht ins Repository, sondern in `C:\ProgramData\Inventory\appsettings.json`
   auf dem Server bzw. für die Entwicklung in `scripts/local.env`):

```json
{
  "Inventory": {
    "Authentication": "Windows,Oidc",
    "Oidc": {
      "TenantId": "<Mandanten-ID>",
      "ClientId": "<Anwendungs-ID>",
      "ClientSecret": "<Geheimnis>",
      "AllowedGroups": [ "<Objekt-ID der Gruppe oder Name der App-Rolle>" ]
    }
  }
}
```

HTTPS für Kestrel: `"Kestrel": { "Endpoints": { "Https": { "Url": "https://*:5100", "Certificate": { "Subject": "<server>", "Store": "My", "Location": "LocalMachine" } } } }`.

**API-Tokens** für Skripte werden in der Oberfläche unter *API-Tokens* angelegt und funktionieren in jedem Anmeldemodus:

```bash
curl -H "Authorization: Bearer inv_…" https://<server>:<port>/api/computers
```

Mit aktiver Windows-Anmeldung geht weiterhin auch `curl --negotiate -u : …`.

### API

Die REST-Schnittstelle ist unter `/api/docs` dokumentiert (interaktiv, mit Beispielaufrufen für curl und andere
Clients); die OpenAPI-Beschreibung liegt unter `/api/openapi.json` und lässt sich in Postman, PowerShell oder
Code-Generatoren einlesen. Beide Adressen verlangen dieselbe Anmeldung wie die übrige API.

| Bereich | Endpunkte |
|---|---|
| Rechner | anlegen, abfragen, löschen, Erreichbarkeit prüfen, Inventar-Scan, installierte Software, RDP-Datei, Befehl oder PowerShell-Skript ausführen |
| Pakete | auflisten, Dateien hochladen (`curl -T setup.msi …/api/packages/<id>/files/setup.msi`) |
| Deployments | starten, Fortschritt je Rechner abfragen |
| Berichte | Rechner und Software als CSV oder JSON |

Das Protokoll zwischen Agent und Server (`/api/agent/…`) ist nicht Teil der dokumentierten API.

Für eine vollständige Sicherung gehören neben `backups` auch die Ordner `keys` (ohne sie sind die gespeicherten
Passwörter nicht mehr lesbar) und `repository` dazu.

## Lizenz

[MIT](LICENSE). Inventory verwendet unter anderem MudBlazor (MIT), Entity Framework Core und ASP.NET Core (MIT) sowie
SQLite (Public Domain).
