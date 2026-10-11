# Inventory

[English](README.md) · **Deutsch**

Inventarisierung und Softwareverteilung für Windows-Rechner in einer Anwendung – agentenlos über hinterlegte
Zugangsdaten. Die Oberfläche gibt es auf Deutsch und Englisch; Übersetzungen in weitere Sprachen sind willkommen
(siehe [Sprachen](#sprachen)).

![Übersicht](docs/screenshots/uebersicht.png)

## Funktionen

- **Rechner** anlegen, aus dem Active Directory importieren, Erreichbarkeit prüfen – auf Wunsch regelmäßig per Heartbeat (Rechner mit Namensauffälligkeiten ausgenommen)
- **Inventar-Scan**: System, Betriebssystem, Prozessor, Software, Hotfixes, Datenträger, Netzwerk, Dienste
- **Sammlungen**: statisch oder dynamisch über Regeln (z. B. „Chrome installiert mit Version unter 141“)
- **Remote-Befehl** (cmd/PowerShell) mit Live-Ausgabe
- **Pakete** aus mehreren Schritten: Installation, Befehl, PowerShell, Dateien kopieren, Dienst, Warten, Neustart,
  Scan, eingebettetes Paket – jeweils mit Bedingungen und Fehlerverhalten; Schritte nach einem Neustart laufen
  automatisch weiter, sobald der Rechner wieder da ist
- **Paketbibliothek**: fertige Pakete gängiger Programme (täglich aus den winget-Manifesten erzeugt), Import per
  Klick, automatische Übernahme neuer Versionen und eine mitwandernde Sammlung „… veraltet“
- **Ausführungskontext** je Schritt: als Lokales System oder in der Sitzung des angemeldeten Benutzers
- **Fernzugriff** je Rechner: Remoteunterstützung anbieten, Sitzung spiegeln, Remotedesktop, TeamViewer/AnyDesk
- **Deployments** parallel mit Live-Status, Abbruch, Wiederholung und Nachholen von Offline-Rechnern
- **Zeitpläne**, Software-Übersicht, CSV-Export, Protokoll, tägliche Datensicherung, REST-API mit interaktiver Dokumentation

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

Für Rechner, die der Server nicht direkt erreicht (Notebooks unterwegs, andere Netze, kein SMB-/WinRM-Weg), gibt es
denselben Runner als dauerhaften Windows-Dienst. Er spricht immer nur *zum* Server – der Server verbindet sich nie zum
Agenten.

**So funktioniert er**

| | |
|---|---|
| Dienst | `InventoryAgent` („Inventory Agent“), läuft als **Lokales System**, Starttyp Automatisch, Wiederherstellung: Neustart nach 5 s / 60 s / 5 min |
| Dateien | `%ProgramFiles%\Inventory Agent` – der Runner (`InventoryRunner.exe`, .NET Framework 4.8), `agent.json` (Server-Adresse + Anmeldegeheimnis), `agent.key` (eigener Schlüssel des Agenten), `agent.version` (Hash der installierten Runner-Dateien), `agent.log`; der Ordner ist nur für SYSTEM und Administratoren lesbar |
| Anmeldung | Beim ersten Start schickt der Agent Anmeldegeheimnis und Rechnernamen an `POST /api/agent/enroll`. Der Server prüft das Geheimnis, legt den Rechner an, falls unbekannt (Abgleich über den Kurznamen, ein unter FQDN eingetragener Rechner wird wiederverwendet), erzeugt einen zufälligen 256-Bit-Schlüssel, speichert nur dessen SHA-256-Hash und gibt den Schlüssel einmalig zurück. Der Agent legt ihn in `agent.key` ab und schickt das Geheimnis nie wieder. |
| Authentifizierung | Jede weitere Anfrage trägt den Kopf `X-Agent-Key: <Schlüssel>`. Der Server sucht den Hash; ein unbekannter Schlüssel (Rechner gelöscht, Agent entfernt) ergibt 401, der Agent löscht `agent.key` und meldet sich mit dem Geheimnis neu an. |
| Abfrage | `POST /api/agent/poll` ist ein Long-Poll: Der Server hält die Anfrage bis zu 50 s offen und antwortet `204`, wenn kein Job kam; der Agent fragt sofort erneut. Ein Job wird so innerhalb einer Sekunde abgeholt, mit einer offenen HTTP-Anfrage je Rechner statt Dauerfeuer. Jede Abfrage aktualisiert „letzter Kontakt“; die Oberfläche zeigt den Agenten als verbunden, solange der letzte Kontakt keine 5 Minuten zurückliegt. |
| Job-Ausführung | Das Job-Manifest kommt mit der Antwort der Abfrage; Paketdateien werden einzeln geholt (`GET /api/agent/jobs/{id}/files/{n}`) in einen Job-Ordner unterhalb des Agentenordners. Derselbe `JobExecutor` wie im agentenlosen Betrieb führt die Schritte als Lokales System aus (bzw. in der Sitzung des angemeldeten Benutzers, wenn so eingestellt). Fortschrittszeilen gehen jede Sekunde an den Server (`POST …/progress`), das Ergebnis am Ende (`POST …/result`, bis 64 MB für große Scans), danach wird der Job-Ordner gelöscht. |
| Zeitlimits | Holt kein Agent den Job innerhalb von 2 Minuten ab, gilt der Rechner als nicht erreichbar (Deployments mit „Offline-Rechner nachholen“ versuchen es später erneut). Während ein Job läuft, wartet der Server die Summe der Schritt-Zeitlimits plus Reserve ab. |
| Selbstaktualisierung | Die Abfrage enthält den Hash der installierten Runner-Dateien. Liefert der Server einen anderen Runner aus (nach einem Server-Update), kommt statt eines Jobs ein Update-Angebot: Der Agent lädt erst alle Dateien in einen Zwischenordner, tauscht sie dann aus (laufende Dateien werden zu `*.old` umbenannt und beim nächsten Start gelöscht), schreibt den neuen Hash und beendet sich mit Code 3 – die Wiederherstellung des Dienstes startet ihn binnen Sekunden mit den neuen Dateien neu. Wartende Jobs bleiben so lange in der Warteschlange. |
| Fehler | Bei jedem Fehler (Server nicht erreichbar, ungültige Antwort) protokolliert der Agent in `agent.log` und versucht es nach 30 s erneut, beliebig oft. |

**Was die Kommunikation braucht**

- **Richtung und Ports:** nur ausgehend, vom Client zur Server-Adresse aus *Einstellungen → Agenten*
  (z. B. `https://inventory.firma.local:5100`). Kein eingehender Port, keine Firewall-Regel und kein SMB/WinRM auf dem
  Client. Der Port des Servers (Standard 5100) muss aus den Client-Netzen erreichbar sein (Firewall-Regel auf dem
  Server; das Setup kann sie anlegen).
- **HTTPS:** dringend empfohlen – über diesen Kanal erhält der Agent Befehle, die er als Lokales System ausführt. Der
  Agent nutzt den Windows-Zertifikatspeicher; das Serverzertifikat muss also auf dem Client vertrauenswürdig sein
  (interne CA oder öffentliches Zertifikat), einen Schalter „jedes Zertifikat akzeptieren“ gibt es nicht. TLS 1.2 wird
  erzwungen. Kestrel-HTTPS: siehe [Konfiguration](#konfiguration-cprogramdatainventoryappsettingsjson).
- **Proxy:** Der Agent läuft als Lokales System und nutzt die WinHTTP-Proxyeinstellungen des Rechners (`netsh winhttp
  set proxy …`), nicht den Browser-Proxy eines Benutzers. Ein direkter Weg zum Server ist am einfachsten.
- **Namensauflösung:** Der Client muss den Hostnamen der Server-Adresse auflösen (DNS oder `hosts`). Der Server muss den
  Client gar nicht auflösen.
- **Anmeldegeheimnis:** steht unter *Einstellungen → Agenten* und wird vom Installationsjob in `agent.json`
  geschrieben. *Geheimnis erneuern* sperrt nur neue Anmeldungen – bereits angemeldete Agenten arbeiten mit ihren
  Schlüsseln weiter. `agent.json` und `agent.key` sind wie ein Passwort zu behandeln; deshalb ist der Agentenordner
  abgeschottet.
- **Die Agenten-Schnittstelle ist von der Benutzeranmeldung unabhängig** (Windows/OIDC/API-Tokens): `/api/agent/*`
  prüft allein den Schlüssel und ist der einzige Teil der API, der das tut.

**Einrichtung**

1. Unter *Einstellungen → Agenten* die Adresse eintragen, unter der die Rechner den Server erreichen (HTTPS
   empfohlen), und speichern.
2. Unter *Rechner* die Ziele auswählen und *Aktionen → Agent installieren* wählen. Die Installation selbst läuft
   agentenlos (SMB oder WinRM): Runner kopieren, `agent.json` und `agent.version` schreiben, Dienst anlegen und
   starten. Ohne agentenlosen Weg lässt sich der Ordner `agent` vom Server (`<Programmordner>\runner`) kopieren, eine
   `agent.json` mit `{"serverUrl": "https://…", "enrollmentToken": "…"}` dazulegen und der Dienst von Hand anlegen:
   `sc create InventoryAgent binPath= "\"C:\Program Files\Inventory Agent\InventoryRunner.exe\" --agent" start= auto`.
3. Sobald sich der Dienst angemeldet hat, steht der Rechner auf Transport „Agent“ (grüner Chip „Agent“). Je Rechner
   lässt sich der Transport wieder auf SMB oder WinRM stellen; der Agent fragt trotzdem weiter ab.

**Agent entfernen:** Dienst auf dem Rechner beenden und löschen (`sc stop InventoryAgent`, `sc delete
InventoryAgent`, Ordner löschen), danach in *Rechner bearbeiten* „Agent vergessen“ wählen – das verwirft den Schlüssel
auf dem Server und setzt den Transport zurück. Umgekehrt würde sich der noch laufende Agent mit dem Geheimnis einfach
neu anmelden.

Nach einem Server-Update aktualisieren sich die Agenten selbst (siehe oben); nichts muss neu verteilt werden.

**Fehlersuche:** `agent.log` im Agentenordner zeigt Anmeldung, Fehler und Updates. `InventoryRunner.exe --agent-console`
dort (als Administrator, bei gestopptem Dienst) führt dieselbe Schleife in der Konsole aus. Das Protokoll des Servers
hält jede Anmeldung mit Rechnername, Version und Absenderadresse fest.

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

## Sprachen

Die Oberfläche folgt der Browsersprache (Deutsch oder Englisch) und lässt sich in der Kopfleiste umschalten; die Wahl
bleibt in einem Cookie. `Inventory:Language` legt die Standardsprache des Servers fest, die auch für Einträge aus
Hintergrundläufen gilt (Protokoll). Runner und Agent sprechen nur Englisch – ihre Meldungen sind technisch und werden
als Daten gespeichert.

**Eine Übersetzung ergänzen:** Alle Texte stehen englisch im Quelltext und werden über
`IStringLocalizer<SharedResource>` (Microsoft.Extensions.Localization) übersetzt. Für eine neue Sprache
`src/Inventory.Core/Resources/SharedResource.de.resx` nach `SharedResource.<kultur>.resx` kopieren (z. B.
`SharedResource.fr.resx`), die `<value>`-Elemente übersetzen und die `name`-Attribute (die englischen Texte) sowie die
Platzhalter `{0}` unverändert lassen. Nach dem Bauen erscheint die Sprache von selbst im Umschalter.
`tests/Inventory.Tests/LocalizationTests.cs` prüft, dass jeder im Code verwendete Text eine deutsche Übersetzung hat und
Platzhalter erhalten bleiben; für eine weitere Sprache lässt sich der Test entsprechend erweitern.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `Inventory.Contracts` | Job-Manifest und Ergebnisformat (von Server und Runner genutzt) |
| `Inventory.Core` | Modell, Regeln, Schnittstellen, Übersetzungen (`Resources`) |
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
  Beispieldaten. Schritte, deren Name „fail“ enthält, schlagen in der Simulation fehl.
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
| `Inventory:Language` | Standardsprache der Oberfläche und der Hintergrundläufe (`en` oder `de`); Benutzer schalten in der Kopfleiste um |
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
| Pakete | auflisten, anlegen, ändern, löschen; Schritte hinzufügen, ändern, verschieben und löschen; Dateien hochladen und löschen (`curl -T setup.msi …/api/packages/<id>/files/setup.msi`) |
| Deployments | starten, auflisten, Fortschritt je Rechner abfragen, abbrechen |
| Sammlungen | auflisten, anlegen, ändern, löschen; Mitglieder statischer Sammlungen hinzufügen und entfernen |
| Zeitpläne | auflisten, anlegen, ändern, löschen, sofort ausführen |
| Berichte | Rechner und Software als CSV oder JSON |

Das Protokoll zwischen Agent und Server (`/api/agent/…`) ist nicht Teil der dokumentierten API.

Für eine vollständige Sicherung gehören neben `backups` auch die Ordner `keys` (ohne sie sind die gespeicherten
Passwörter nicht mehr lesbar) und `repository` dazu.

## Roadmap

[ROADMAP.md](ROADMAP.md) (englisch) beschreibt, was für die nächsten Versionen geplant ist – robustere Verteilung (Neustart mit Fortsetzung, Erkennungsregeln, Deinstallation, Wartungsfenster), Rollen, tieferes Inventar und Compliance, Alarme und Berichte – jeweils mit Begründung und dem, was bewusst wegbleibt. Jeder Punkt ist ein GitHub-Issue in den Meilensteinen [v0.2](https://github.com/erSitzt/inventory/milestone/1), [v0.3](https://github.com/erSitzt/inventory/milestone/2) und [v0.4](https://github.com/erSitzt/inventory/milestone/3).

## Lizenz

[MIT](LICENSE). Inventory verwendet unter anderem MudBlazor (MIT), Entity Framework Core und ASP.NET Core (MIT) sowie
SQLite (Public Domain).
