# Inventory

**English** · [Deutsch](README.de.md)

Inventory and software deployment for Windows computers in a single application – agentless, using stored
credentials. The user interface is available in English and German; translations into other languages are welcome
(see [Languages](#languages)).

![Overview](docs/screenshots/en/overview.png)

## Features

- **Computers**: add manually, import from Active Directory, check reachability – optionally on a regular heartbeat
  (computers with name anomalies are skipped)
- **Inventory scan**: system, operating system, processor, software, hotfixes, disks, network, services, local users
  and administrators, firewall, antivirus and Windows Update status
- **Collections**: static or dynamic via rules (e.g. "Chrome installed with version below 141")
- **Remote command** (cmd/PowerShell) with live output
- **Packages** made of several steps: installation, command, PowerShell, copy files, service, wait, reboot, scan,
  embedded package – each with conditions and error handling
- **Package library**: ready-made packages of common programs (generated daily from the winget manifests), import
  with one click, automatic adoption of new versions and a dynamic "… outdated" collection that follows along
- **Run context** per step: as Local System or in the session of the logged-on user
- **Remote access** per computer: offer Remote Assistance, shadow a session, Remote Desktop, TeamViewer/AnyDesk
- **Deployments** in parallel with live status, cancel, retry, and catching up on offline computers
- **Schedules**, software overview, CSV export, audit log, daily backup, REST API with interactive docs

## Screenshots

| | |
|---|---|
| **Computer list** with status, security indicators and filters ![Computers](docs/screenshots/en/computers.png) | **Computer details** – inventory, software, hotfixes, services, security, history ![Computer details](docs/screenshots/en/computer-detail.png) |
| **Package library** – ready-made packages from winget, installer type at a glance ![Package library](docs/screenshots/en/library.png) | **Deployment** – progress per computer, offline computers are retried ![Deployment](docs/screenshots/en/deployment.png) |
| **Dynamic collections** – e.g. all computers with an outdated version ![Collections](docs/screenshots/en/collections.png) | **Schedules** for scans and deployments ![Schedules](docs/screenshots/en/schedules.png) |

The pictures come from simulation mode (`Inventory:Simulate`), which shows a sample environment without real computers.

## How access works

On the target computer the same small **runner** (`InventoryRunner.exe`, .NET Framework 4.8) always executes a job.
How it gets there is decided by the transport – configurable globally and per computer:

| Transport | Prerequisite on the target | Job runs as |
|---|---|---|
| SMB + service (default) | `ADMIN$` share, port 445, account with local admin rights | Local System |
| WinRM | PowerShell remoting enabled, port 5985, PowerShell in full language mode | stored account |
| Agent (optional) | service "Inventory Agent", outbound connection to the server | Local System |

On the target computer `%SystemRoot%\Inventory` is created (SYSTEM and administrators only): the runner stays there,
job directories and the temporary service are removed after every run.

### Agent

For computers the server cannot reach directly (notebooks on the road, other networks) the same runner is available
as a permanent service. It enrolls once with the enrollment secret, receives its own key and then continuously asks
for jobs over HTTP(S). Setup:

1. Under *Settings → Agents* enter the address under which the computers reach the server (HTTPS recommended).
2. Under *Computers* select the targets and choose *Actions → Install agent*. The installation itself runs agentless
   (SMB or WinRM) into `%ProgramFiles%\Inventory Agent`.
3. As soon as the service reports in, the computer is on transport "Agent"; this can be changed per computer.

Agents update themselves: when the server ships newer runner files after an update, the agent downloads them on its
next request, swaps them and restarts itself through the service recovery.

Troubleshooting: `InventoryRunner.exe --agent-console` in the agent folder, log in `agent.log`.

### Remote access

The computer list and the detail page have a *Remote access* menu:

| Method | What is started | Prerequisite on the computers |
|---|---|---|
| Offer Remote Assistance | `msra.exe /offerRA <computer>` | policy "Configure Offer Remote Assistance", firewall group "Remote Assistance" |
| Shadow session | `mstsc.exe /v:<computer> /shadow:<session> /control`; the session number comes from the last scan | policy "Set rules for remote control of Remote Desktop Services user sessions", firewall rules "Remote Desktop – Shadow" |
| Remote Desktop | `mstsc.exe /v:<computer>` | Remote Desktop enabled |
| TeamViewer / AnyDesk | the program via its own link with the device ID captured during the scan | program installed on both sides |

For the first three to start with one click, the admin PC needs the **launcher** once (`InventoryLauncher.exe`, a few
kilobytes): download it from the menu under "Set up the launcher", put it in a fixed place and double-click it once. It
registers itself for the user account as the handler for `inventory://` links (no admin rights). For several admin PCs
there is "Install remote access launcher" under *Computers → Actions* (into `%ProgramFiles%\Inventory Launcher`, for
all users). The launcher accepts only these three actions, validates computer name and session number and starts
nothing but `msra.exe` and `mstsc.exe` from System32 – other websites cannot trigger anything else through the link.

Without the launcher the same dialog shows the commands to copy and offers Remote Desktop as an `.rdp` file.

Every call is logged. If a different computer responds under the listed name, the menu warns.

### Active Directory

The synchronization (*Settings → Active Directory*) creates computers from computer accounts and reconciles existing ones:

- **Exclude disabled computer accounts** (default: on): disabled accounts are not created; if an existing computer is
  disabled in the directory, it counts as no longer found.
- **Computers no longer found** are marked "not in AD" (filter in the computer list, rule field for dynamic
  collections). Optionally the synchronization deletes them after they have been missing continuously for a
  configurable grace period – including inventory data and history.
- Manually added computers the directory never knew remain untouched. If the directory returns no accounts at all or
  conspicuously many computers would be deleted at once, the run changes or deletes nothing and reports it.

### Package library

The action `.github/workflows/catalog.yml` reads the manifests of the programs listed in `catalog/apps.txt` from the
[winget repository](https://github.com/microsoft/winget-pkgs) every day and writes `catalog.json` to the `catalog`
branch: per program the current version, the vendor's download address, SHA-256 and silent parameters. The server
loads this file (`Inventory:Catalog:Url`), shows it under *Package library* and downloads the setup directly from the
vendor on import; if the checksum does not match, nothing is adopted.

`catalog/apps.txt` is the base selection of common programs that install for all users. In addition the action
generates `winget.json`, a search index over the whole winget repository with all programs that have an unattended
MSI or EXE installer (`Inventory:Catalog:IndexUrl`). The library's search shows matches from it under "More results
from winget"; they can be imported and kept up to date the same way. These entries are taken unchanged from the
manifests – test on a single computer before deploying. Setups that only install into the user profile get the
context "logged-on user" on import.

Older versions: the import dialog offers all versions the winget repository still lists (at most the 60 newest per
program, `Inventory:Catalog:VersionsUrl`). An older version is created as a separate package "Name Version" that is
never updated automatically and can exist next to the current package. If the vendor removes an old setup from the
server, the download fails.

- More programs in the base selection: add the winget ID to `catalog/apps.txt`.
- Own catalog: point `Inventory:Catalog:Url` to your own file or address (same format).
- Keep programs up to date automatically: have the "… outdated" collection created on import and set up a schedule
  that regularly deploys the package to this collection.

### Run context

Commands, PowerShell scripts and installations run as **Local System** by default. For everything that concerns the
user profile (HKCU, AppData, network drives) **Logged-on user** can be chosen per step: the runner then starts the
process in the active session with that user's rights. If nobody is logged on, the step is skipped. This requires the
runner to run as Local System (SMB + service or agent, not WinRM). For the duration of the run, script and package
files are placed in a folder readable by users under `%ProgramData%\InventoryRunner` – such steps must therefore not
contain secrets.

## Languages

The interface follows the browser language (English or German) and can be switched in the top bar; the choice is
kept in a cookie. `Inventory:Language` sets the server's default language, which is also used for entries written by
background jobs (audit log). The runner and agent only speak English – their messages are technical and stored as data.

**Adding a translation:** all texts are English in the source code and are translated through
`IStringLocalizer<SharedResource>` (Microsoft.Extensions.Localization). To add a language, copy
`src/Inventory.Core/Resources/SharedResource.de.resx` to `SharedResource.<culture>.resx` (e.g. `SharedResource.fr.resx`),
translate the `<value>` elements and keep the `name` attributes (the English texts) and the `{0}` placeholders as they
are. The new language appears in the switcher automatically after a build. `tests/Inventory.Tests/LocalizationTests.cs`
checks that every text used in the code has a German translation and that placeholders survive; extend it for your
language if you want the same safety net.

## Structure

| Project | Content |
|---|---|
| `Inventory.Contracts` | job manifest and result format (used by server and runner) |
| `Inventory.Core` | model, rules, interfaces, translations (`Resources`) |
| `Inventory.Data` | EF Core/SQLite, services (scan, packages, deployments, schedules …) |
| `Inventory.Remoting` | transports (SMB, WinRM, simulation), reachability, LDAP |
| `Inventory.Runner` | program for the target computer (net48) |
| `Inventory.Launcher` | launcher for the admin PC: opens remote access from `inventory://` links (net48) |
| `Inventory.Server` | ASP.NET Core: Blazor UI, REST API under `/api`, scheduler |

## Development under WSL2

The .NET 10 SDK is installed per user under `~/.dotnet` (`export PATH="$HOME/.dotnet:$PATH"`).

```bash
dotnet build && dotnet test          # also builds the net48 runner
dotnet run --project src/Inventory.Server --launch-profile http   # simulation, http://localhost:5101
./scripts/run-win.sh                 # real Windows process with real transports
```

- On Linux the server automatically runs in **simulation mode**: transports, reachability and AD return sample
  data. Steps whose name contains "fail" fail in the simulation.
- Real access (SMB, service control, Kerberos) only works as a Windows process. `run-win.sh` publishes to
  `%LOCALAPPDATA%\InventoryDev` and starts there; database and package files are then on NTFS.
- Environment-specific values (e.g. the allowed group) belong in `scripts/local.env`, which is not checked in;
  `run-win.sh` reads the file. Example: `Inventory__AllowedGroups__0='COMPANY\Domain Admins'`
- The server itself is no good as an SMB test target: local access always uses the account of the server process.

## Installation

1. Download `Inventory-Setup-x.y.z.exe` from the [releases page](https://github.com/erSitzt/inventory/releases/latest).
2. Run it as administrator on the Windows server. The wizard asks for port, allowed Windows groups and service
   account, creates the service "Inventory" and optionally opens the port in the firewall.
3. Open the interface at `http://<server>:5100`.

Program files live under `C:\Program Files\Inventory`, data and settings under `C:\ProgramData\Inventory`. Nothing
else needs to be installed on the server (.NET is included).

Unattended:

```
Inventory-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PORT=5100 /GROUPS="COMPANY\Domain Admins"
```

More switches: `/SERVICEACCOUNT="COMPANY\svc-inventory" /SERVICEPASSWORD=…`, `/NOFIREWALL`, `/LOG=setup.log`.
The SHA-256 checksum of every release file is listed in `SHA256SUMS.txt`.

## Updating

- **In the interface:** the server checks GitHub every few hours. If there is a newer version, a notice appears at the
  top; *Settings → Updates* shows the changes and the "Update now" button. Automatic updates in a nightly maintenance
  window can be enabled there as well (default: off).
- **Manually:** run the new setup over the existing installation. Settings and data are kept.

Before every update through the interface the database is backed up, running deployments are awaited, and the result
is written to the audit log. Agents update themselves afterwards. Going back to an older version is only possible via
the backup (`backups`), because newer versions extend the database schema.

Self-update requires the service to run as Local System or with local administrator rights. The update check can be
disabled with `Inventory:Update:Enabled = false`.

> **About the chain of trust:** updates bring new program code onto the server and, through the agents, onto all
> managed computers. Releases are currently **not signed**; the checksum only secures the transfer, not the origin.
> If you want to lock this down, leave automatic updates off and review releases before installing. Once releases are
> signed, `Inventory:Update:RequiredPublisher` enforces the publisher check.

### Creating a release (for maintainers)

A tag `vX.Y.Z` starts the pipeline (`.github/workflows/release.yml`): tests, publish, setup, ZIP, checksums, GitHub
release with automatically generated notes. Tags with a suffix (`v1.2.0-rc1`) are marked as pre-release and ignored
by the update check. If the secrets `SIGNING_CERT_PFX` (Base64 of the PFX file) and `SIGNING_CERT_PASSWORD` exist, the
pipeline signs server, runner and setup.

### Configuration (`C:\ProgramData\Inventory\appsettings.json`)

| Key | Meaning |
|---|---|
| `Urls` | addresses of the web interface, e.g. `http://*:5100` |
| `Inventory:DataDirectory` | database, keys, package files (`repository`), backups (`backups`) |
| `Inventory:Language` | default language of the interface and of background jobs (`en` or `de`); users can switch in the top bar |
| `Inventory:Authentication` | `Windows` (default on Windows), `Oidc`, both (`Windows,Oidc`) or `None` |
| `Inventory:AllowedGroups` | Windows groups with access, as name (`COMPANY\IT-Admins`) or SID. Default: local administrators (`S-1-5-32-544`) |
| `Inventory:Oidc:TenantId` | Entra tenant (GUID or domain); for other providers use `Inventory:Oidc:Authority` instead |
| `Inventory:Oidc:ClientId`, `ClientSecret` | from the app registration |
| `Inventory:Oidc:AllowedGroups` | object IDs of the allowed groups or names of app roles; without an entry the server does not start |
| `Inventory:Update:Enabled`, `Repository`, `RequiredPublisher` | update check, source of the releases, expected publisher of the signature |
| `Inventory:Catalog:Url` | address or path of the package library's `catalog.json` |
| `Inventory:Catalog:VersionsUrl` | folder with the version lists (`<winget-ID>.json`); empty = offer only the current version |
| `Inventory:Catalog:IndexUrl` | address or path of the search index `winget.json`; empty = disable the search over all of winget |
| `Inventory:Simulate` | `true` = no real access |

### Sign-in

Windows sign-in and OIDC can be enabled individually or together. If both are active, a chooser page appears; both
paths lead to the same session. The agent interface is independent of this (own keys).

**Setting up Microsoft Entra ID**

1. In the Entra admin center under *App registrations* create a new registration, platform **Web**, redirect URI
   `https://<server>:<port>/signin-oidc`. Entra only accepts HTTPS (exception: `http://localhost`).
2. Under *Certificates & secrets* create a client secret.
3. Get the authorization into the token – one of the two options:
   - *Token configuration → Add groups claim → Security groups* (ID token, group ID). `Inventory:Oidc:AllowedGroups`
     then takes the **object IDs** of the groups. For users with more than 200 groups Entra omits the claim; use the
     second option then.
   - Create *app roles* and assign them to users or groups (enterprise application → users and groups).
     `AllowedGroups` then takes the **value** of the role.
4. Add the configuration (the secret does not belong in the repository but in `C:\ProgramData\Inventory\appsettings.json`
   on the server, or in `scripts/local.env` for development):

```json
{
  "Inventory": {
    "Authentication": "Windows,Oidc",
    "Oidc": {
      "TenantId": "<tenant ID>",
      "ClientId": "<application ID>",
      "ClientSecret": "<secret>",
      "AllowedGroups": [ "<object ID of the group or name of the app role>" ]
    }
  }
}
```

HTTPS for Kestrel: `"Kestrel": { "Endpoints": { "Https": { "Url": "https://*:5100", "Certificate": { "Subject": "<server>", "Store": "My", "Location": "LocalMachine" } } } }`.

**API tokens** for scripts are created in the interface under *API tokens* and work in every sign-in mode:

```bash
curl -H "Authorization: Bearer inv_…" https://<server>:<port>/api/computers
```

With Windows sign-in enabled, `curl --negotiate -u : …` works as well.

### API

The REST interface is documented at `/api/docs` (interactive, with example calls for curl and other clients); the
OpenAPI description is at `/api/openapi.json` and can be imported into Postman, PowerShell or code generators. Both
addresses require the same sign-in as the rest of the API.

| Area | Endpoints |
|---|---|
| Computers | create, query, delete, check reachability, inventory scan, installed software, RDP file, run command or PowerShell script |
| Packages | list, upload files (`curl -T setup.msi …/api/packages/<id>/files/setup.msi`) |
| Deployments | start, query progress per computer |
| Reports | computers and software as CSV or JSON |

The protocol between agent and server (`/api/agent/…`) is not part of the documented API.

For a complete backup, the folders `keys` (without them the stored passwords are unreadable) and `repository` belong
next to `backups`.

## License

[MIT](LICENSE). Inventory uses, among others, MudBlazor (MIT), Entity Framework Core and ASP.NET Core (MIT) and
SQLite (public domain).
