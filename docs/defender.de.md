# Microsoft Defender for Endpoint

Agentenlose Jobs sehen für Endpunktschutz wie ein Angriff aus: eine Datei wird nach `ADMIN$` kopiert, ein Dienst aus
der Ferne angelegt und als Local System gestartet, PowerShell von diesem Dienst aufgerufen. Diese Seite beschreibt, was
Defender meldet, wie sich Inventory eng gefasst freigeben lässt und was im Portal bleibt, weil Microsoft dafür keine
Schnittstelle anbietet.

Die Schritte gelten für den eigenen Mandanten. Sie brauchen im Microsoft-Defender-Portal die Rolle
*Sicherheitsadministrator* (oder eine eigene Rolle, die Sicherheitseinstellungen verwalten darf).

## Was Defender meldet

| Meldung | Ursache |
|---|---|
| *Suspicious service creation initiated remotely*, *File dropped and launched from remote location*, *Possible lateral movement involving suspicious file*, *Suspicious service launched* | Verhalten: der agentenlose Weg selbst (Kopie nach `ADMIN$`, Dienststeuerung aus der Ferne). |
| *Anomaly detected in ASEP registry*, *Suspicious file registered as a service* | Der Runner-Dienst wird beim ersten Job angelegt und nach einem Server-Update auf den neuen Ordner umgestellt. |
| *An active '…' malware was detected* mit einem Erkennungsnamen, der auf `!ml` endet | Keine Signatur einer bekannten Schadsoftware-Familie. Das Suffix `!ml` kennzeichnet ein Urteil des maschinellen Modells in der Cloud: ein unsigniertes Programm, das kaum ein Rechner je gesehen hat und das auf dem Weg ankommt, den Schadsoftware nimmt. Jede Server-Version bringt einen Runner mit neuem Hash mit, die Datei beginnt also jedes Mal ohne Reputation. |
| *Compromised device (attack disruption)* | Automatische Reaktion auf die Kombination des Obigen. Sie kann das Gerät eindämmen und das für den Zugriff verwendete Konto deaktivieren. |

## 1. Den Agenten nutzen, den agentenlosen Weg einschränken

Rechner mit Agent verbinden sich ausgehend; nichts von dem obigen Verhalten tritt auf. Unter
*Einstellungen → Agentenloser Zugriff* wählen:

- **nur zur Installation des Agenten** – Scans, Verteilungen und Zeitpläne erreichen nur Rechner mit Agent; der
  einzige verbleibende agentenlose Job ist *Rechner → Aktionen → Agent installieren*.
- **nie** – sobald der Agent auf anderem Weg kommt: *Einstellungen → Agenten → Agent-Setup herunterladen* liefert ein
  Paket für ein Startskript per Gruppenrichtlinie oder eine vorhandene Softwareverteilung (siehe README, Abschnitt
  *Agent*).

Entwicklungsstände nicht gegen Rechner testen, die in Defender eingebunden sind: jeder Build ist eine Datei, die noch
niemand gesehen hat.

## 2. Die Runner-Datei freigeben

Ein Indikator *Zulassen* für den Datei-Hash beendet das Schadsoftware-Urteil für diesen Runner. Die
Verhaltensmeldungen bringt er nicht zum Schweigen; das ist Schritt 3.

Der Hash steht unter *Einstellungen → SHA-256 von InventoryRunner.exe*, oder auf dem Server:

```powershell
(Get-FileHash "$env:ProgramFiles\Inventory\runner\InventoryRunner.exe" -Algorithm SHA256).Hash
```

Er ändert sich mit jedem Server-Update; der Indikator muss also nach jedem Update erneuert werden, bis die Releases
signiert sind. Indikatoren setzen auf den Clients Microsoft Defender Antivirus mit Cloudschutz voraus sowie die
Funktion *Datei zulassen oder blockieren* (*Einstellungen → Endpunkte → Erweiterte Features*).

**Im Portal:** *Einstellungen → Endpunkte → Indikatoren → Dateihashes → Element hinzufügen*, Aktion *Zulassen*.

**Aus den Inventory-Einstellungen:** *Einstellungen → Microsoft Defender* legt den Indikator für den aktuellen Runner
an und entfernt die der früheren Runner. Sie melden sich mit Ihrem eigenen Konto bei Microsoft an, indem Sie auf der
Anmeldeseite von Microsoft einen Code eingeben; der Server sieht nie ein Kennwort und bewahrt das Token nicht auf.
Einmalig vorzubereiten:

1. Microsoft Entra Admin Center → *App-Registrierungen → Neue Registrierung*, einzelner Mandant, keine Umleitungs-URI.
2. *Authentifizierung → Öffentliche Clientflows zulassen*: **Ja**.
3. *API-Berechtigungen → Berechtigung hinzufügen → Von meiner Organisation verwendete APIs → WindowsDefenderATP →
   Delegierte Berechtigungen → `Ti.ReadWrite`*, dann *Administratorzustimmung erteilen*.
4. Mandanten-ID und Anwendungs-ID (Client) in den Inventory-Einstellungen eintragen.

Die Anwendung hat kein Geheimnis und keine eigenen Rechte: was sie darf, ist auf das beschränkt, was die angemeldete
Person in Defender darf; dieses Konto braucht also eine Rolle, die Indikatoren verwaltet. Eine Richtlinie für
bedingten Zugriff, die den Gerätecode-Ablauf sperrt, sperrt auch diese Anmeldung; dann das Skript unten oder das
Portal verwenden. Die Einstellungsseite zeigt an, wenn sich der Runner seit der letzten Freigabe geändert hat.

**Per Skript:** die Indikator-Schnittstelle nimmt ein Anwendungstoken. Dafür einmalig eine Anwendung in Microsoft
Entra ID registrieren, die API-Berechtigung *WindowsDefenderATP → Anwendung → `Ti.ReadWrite.All`* hinzufügen, die
Administratorzustimmung erteilen und einen geheimen Clientschlüssel anlegen
([Beschreibung von Microsoft](https://learn.microsoft.com/defender-endpoint/api/exposed-apis-create-app-webapp)).

```powershell
$tenantId = '<Mandanten-ID>'
$clientId = '<Anwendungs-ID (Client)>'
$secret   = Read-Host 'Geheimer Clientschlüssel' -AsSecureString
$sha256   = '<SHA-256 von InventoryRunner.exe>'

$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenantId/oauth2/token" -Body @{
    resource      = 'https://api.securitycenter.microsoft.com'
    client_id     = $clientId
    client_secret = [Net.NetworkCredential]::new('', $secret).Password
    grant_type    = 'client_credentials'
}).access_token

$indicator = @{
    indicatorValue = $sha256
    indicatorType  = 'FileSha256'
    action         = 'Allowed'
    title          = 'Inventory runner'
    description    = 'InventoryRunner.exe of the Inventory server (scans and software deployment)'
    generateAlert  = $false
    # Optional: ablaufen lassen, damit ein veralteter Runner nicht für immer freigegeben bleibt.
    expirationTime = (Get-Date).ToUniversalTime().AddMonths(6).ToString('o')
    # Optional: nur für bestimmte Gerätegruppen.
    # rbacGroupNames = @('Workstations')
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'https://api.security.microsoft.com/api/indicators' `
    -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $indicator
```

Den Indikator des vorigen Runners nach einem Update entfernen:

```powershell
$headers = @{ Authorization = "Bearer $token" }
$old = (Invoke-RestMethod -Uri "https://api.security.microsoft.com/api/indicators?`$filter=indicatorValue eq '<alter SHA-256>'" -Headers $headers).value
$old | ForEach-Object { Invoke-RestMethod -Method Delete -Uri "https://api.security.microsoft.com/api/indicators/$($_.id)" -Headers $headers }
```

Sobald die Releases signiert sind, ersetzt ein Indikator vom Typ `CertificateThumbprint` den Hash und übersteht
Updates.

## 3. Die Verhaltensmeldungen abstimmen (nur im Portal)

Regeln zur Warnungsoptimierung (früher Warnungsunterdrückung) lassen sich weder per PowerShell noch über eine
öffentliche Schnittstelle anlegen; Microsoft bietet sie nur im Portal an.

1. Eine der von Inventory ausgelösten Warnungen öffnen und **Warnung optimieren** (*Tune alert*, eventuell hinter
   **…**) wählen. Das füllt die Beweise dieser Warnung vor. Alternativ: *Einstellungen → Microsoft Defender XDR →
   Warnungsoptimierung → Neue Regel hinzufügen*, Dienstquelle *Microsoft Defender for Endpoint*.
2. Die Regel *nur auf diesen Warnungstyp* anwenden und je Warnungstitel eine Regel anlegen, statt einer Regel für
   beliebige Warnungen.
3. Die Bedingungen eng halten und mit **UND** verknüpfen: der Dateiname `InventoryRunner.exe`, der Ordner
   `C:\Windows\Inventory\Runner\` (Datei) und, wo die Warnung es anbietet, der Inventory-Server als Remote- bzw.
   Quellgerät. Welche Eigenschaften zur Verfügung stehen, hängt von der Warnung ab.
4. Aktion **Warnung auflösen**. Die Warnungen bleiben sichtbar und durchsuchbar, kommen aber aufgelöst an. *Warnung
   ausblenden* erst, wenn sich gezeigt hat, dass die Regel nichts anderes trifft.
5. Auf der folgenden Seite bietet das Portal an, die Datei als Zulassen-Indikator aufzunehmen – dasselbe wie
   Schritt 2.

Nicht das Anlegen von Diensten aus der Ferne allgemein abstimmen und `%SystemRoot%\Inventory\` nicht vom Virenscan
ausnehmen: dort landen Paketdateien.

Die automatische Angriffsunterbrechung richtet sich nicht nach der Warnungsoptimierung. Ohne das Schadsoftware-Urteil
(Schritt 2) und ohne agentenlose Jobs (Schritt 1) fehlt ihr der Auslöser; Ausnahmen von der automatischen Reaktion
gibt es im Portal (*Einstellungen → Microsoft Defender XDR*), sie schwächen aber den Schutz der ausgenommenen Konten
und Geräte und sollten das letzte Mittel bleiben.

## 4. Die Fehlerkennung melden

Den Vorfall im Portal als *Falsch positiv* oder *Zur Information, erwartete Aktivität* einstufen und den Runner einer
veröffentlichten Version unter <https://www.microsoft.com/wdsi/filesubmission> einreichen (*Software developer*,
*Incorrectly detected as malware*). Entwicklungsstände einzureichen ist sinnlos: der nächste Build hat einen neuen
Hash.

## Prüfen, was Inventory getan hat

Die erweiterte Suche zeigt den Fußabdruck auf den Zielrechnern, etwa um eine Regel zu überprüfen:

```kusto
DeviceFileEvents
| where Timestamp > ago(7d)
| where FolderPath startswith @"C:\Windows\Inventory\"
| summarize Files = count(), Computers = dcount(DeviceId) by InitiatingProcessAccountName, RequestSourceIP
```
