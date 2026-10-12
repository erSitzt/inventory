# Microsoft Defender for Endpoint

Agentless jobs look like an attack to endpoint detection: a file copied to `ADMIN$`, a service created remotely and
started as Local System, PowerShell launched by that service. This page describes what Defender reports, how to allow
Inventory narrowly, and what stays in the portal because Microsoft offers no API for it.

The steps apply to your own tenant. They need the *Security Administrator* role (or a custom role that may manage
security settings) in the Microsoft Defender portal.

## What Defender reports

| Report | Cause |
|---|---|
| *Suspicious service creation initiated remotely*, *File dropped and launched from remote location*, *Possible lateral movement involving suspicious file*, *Suspicious service launched* | Behaviour: the agentless way itself (copy to `ADMIN$`, remote service control). |
| *Anomaly detected in ASEP registry*, *Suspicious file registered as a service* | The runner service is created on the first job and pointed at the new folder after a server update. |
| *An active '…' malware was detected* with a detection name ending in `!ml` | No signature of a known malware family. The suffix `!ml` marks a verdict of the cloud's machine-learning model: an unsigned program that hardly any computer has seen, arriving the way malware does. Every server version ships a runner with a new hash, so the file starts without reputation each time. |
| *Compromised device (attack disruption)* | Automatic response to the combination of the above. It can contain the device and disable the account used for the access. |

## 1. Use the agent, restrict the agentless way

Computers with the agent connect outbound; none of the behaviour above occurs. Under *Settings → Agentless access*
choose:

- **only to install the agent** – scans, deployments and schedules reach only computers with an agent; the one
  agentless job left is *Computers → Actions → Install agent*.
- **never** – once the agent arrives another way (software distribution, group policy).

Do not test development builds against computers that are onboarded to Defender: every build is a file nobody has
seen before.

## 2. Allow the runner file

An *allow* indicator for the file hash ends the malware verdict for this runner. It does not silence the behaviour
alerts; that is step 3.

The hash is shown under *Settings → SHA-256 of InventoryRunner.exe*, or on the server:

```powershell
(Get-FileHash "$env:ProgramFiles\Inventory\runner\InventoryRunner.exe" -Algorithm SHA256).Hash
```

It changes with every server update, so the indicator has to be renewed after each update until the releases are
code-signed. Indicators need Microsoft Defender Antivirus with cloud-delivered protection on the clients and the
feature *Allow or block file* (*Settings → Endpoints → Advanced features*).

**In the portal:** *Settings → Endpoints → Indicators → File hashes → Add item*, action *Allow*.

**From the Inventory settings:** *Settings → Microsoft Defender* creates the indicator for the current runner and
removes those of earlier runners. You sign in at Microsoft with your own account by entering a code on Microsoft's
sign-in page; the server never sees a password and does not keep the token. Prepare once:

1. Microsoft Entra admin center → *App registrations → New registration*, single tenant, no redirect URI.
2. *Authentication → Allow public client flows*: **Yes**.
3. *API permissions → Add a permission → APIs my organization uses → WindowsDefenderATP → Delegated permissions →
   `Ti.ReadWrite`*, then *Grant admin consent*.
4. Enter the tenant ID and the application (client) ID in the Inventory settings.

The application has no secret and no rights of its own: what it may do is limited to what the signed-in person may do
in Defender, so that account needs a role that manages indicators. A Conditional Access policy that blocks the device
code flow also blocks this sign-in; use the script below or the portal then. The settings page shows when the runner
has changed since the last allowance.

**By script:** the indicator API takes an application token. Register an application in Microsoft Entra ID once, add
the API permission *WindowsDefenderATP → Application → `Ti.ReadWrite.All`*, grant admin consent and create a client
secret ([Microsoft's description](https://learn.microsoft.com/defender-endpoint/api/exposed-apis-create-app-webapp)).

```powershell
$tenantId = '<tenant id>'
$clientId = '<application (client) id>'
$secret   = Read-Host 'Client secret' -AsSecureString
$sha256   = '<SHA-256 of InventoryRunner.exe>'

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
    # Optional: let it expire, so that an outdated runner does not stay allowed for ever.
    expirationTime = (Get-Date).ToUniversalTime().AddMonths(6).ToString('o')
    # Optional: only for certain device groups.
    # rbacGroupNames = @('Workstations')
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'https://api.security.microsoft.com/api/indicators' `
    -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $indicator
```

Remove the indicator of the previous runner after an update:

```powershell
$headers = @{ Authorization = "Bearer $token" }
$old = (Invoke-RestMethod -Uri "https://api.security.microsoft.com/api/indicators?`$filter=indicatorValue eq '<old SHA-256>'" -Headers $headers).value
$old | ForEach-Object { Invoke-RestMethod -Method Delete -Uri "https://api.security.microsoft.com/api/indicators/$($_.id)" -Headers $headers }
```

Once the releases are code-signed, one indicator of the type `CertificateThumbprint` replaces the hash and survives
updates.

## 3. Tune the behaviour alerts (portal only)

Alert tuning rules (formerly alert suppression) cannot be created by PowerShell or a public API; Microsoft offers
them in the portal only.

1. Open one of the alerts Inventory caused and choose **Tune alert** (possibly behind **…**). That pre-fills the
   evidence of this alert. Alternatively: *Settings → Microsoft Defender XDR → Alert tuning → Add new rule*, service
   source *Microsoft Defender for Endpoint*.
2. Apply the rule to *this alert type only* and create one rule per alert title, rather than one rule for any alert.
3. Keep the conditions narrow and combine them with **AND**: the file name `InventoryRunner.exe`, the folder
   `C:\Windows\Inventory\Runner\` (file) and, where the alert offers it, the Inventory server as the remote or
   source device. Which properties are available depends on the alert.
4. Action **Resolve alert**. The alerts stay visible and searchable, but arrive resolved. *Hide alert* only once the
   rule has proven to match nothing else.
5. On the page that follows, the portal offers to add the file as an allow indicator – the same as step 2.

Do not tune remote service creation in general and do not exclude `%SystemRoot%\Inventory\` from antivirus scanning:
package files land there.

Automatic attack disruption is not governed by alert tuning. Without the malware verdict (step 2) and the agentless
jobs (step 1) its trigger is gone; exclusions from the automatic response exist in the portal
(*Settings → Microsoft Defender XDR*), but they weaken the protection of the excluded accounts and devices and should
stay the last resort.

## 4. Report the false positive

Classify the incident as *False positive* or *Informational, expected activity* in the portal, and submit the runner
of a released version at <https://www.microsoft.com/wdsi/filesubmission> (*Software developer*, *Incorrectly detected
as malware*). Submitting development builds is pointless: the next build has a new hash.

## Checking what Inventory did

Advanced hunting shows the footprint on the targets, for example to verify a tuning rule:

```kusto
DeviceFileEvents
| where Timestamp > ago(7d)
| where FolderPath startswith @"C:\Windows\Inventory\"
| summarize Files = count(), Computers = dcount(DeviceId) by InitiatingProcessAccountName, RequestSourceIP
```
