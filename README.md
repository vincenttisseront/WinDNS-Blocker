# WinDNS Blocker

Windows hub (.NET 8) that downloads [Steve Jenkins pi-hole-lists](https://github.com/stevejenkins/pi-hole-lists) (list-of-lists), expands them into domains, and pushes **DNS Query Resolution Policies** to **N Windows DNS servers** from a **single service**.

> **FR :** l’interface propose Français / English (Configuration → Language). La doc publique ci-dessous est en anglais.

## Architecture

- **DnsBlocker.Service** — Windows Service on one hub machine (timer + named-pipe IPC)
- **DnsBlocker.UI** — WPF thick client on the same hub (config, farm, logs, domain search, stats)
- **DnsBlocker.Core** — ingest, chunking, PowerShell/`-ComputerName` provider, SQLite state

```
Hub (service + UI)
  └─ WinRM/CIM ─► DNS-A, DNS-B, … DNS-N   (policies DnsBlocker_0001…)
```

`blocklists.txt` / `whitelists.txt` are **URLs to list indexes**, not raw domains. `regex.txt` is **not** applied (Windows QRP has no regex).

### Language (UI)

- **Configuration → Language**: Auto / English / Français
- **Auto** (default): follows the Windows UI culture — French if `fr*`, otherwise English
- Preference is stored in `%ProgramData%\DnsBlocker\config.json` as `uiLanguage` (`auto` | `en` | `fr`)
- Changing language takes effect after restarting the UI

### Curated extra blocklists

In addition to Steve Jenkins LoL, the hub can ingest **curated ExtraBlocklistUrls** (toggles in **Configuration**). Defaults on first run:

| Toggle | Default | Raw URL |
|--------|---------|---------|
| YouTube ads (kboghdady) | on | `https://raw.githubusercontent.com/kboghdady/youTube_ads_4_pi-hole/master/youtubelist.txt` |
| OISD big | on | `https://big.oisd.nl/domainswild` |
| OISD NSFW (family) | on | `https://nsfw.oisd.nl/domainswild` |
| Chadmayfield porn (adult) | **off** | `https://raw.githubusercontent.com/chadmayfield/my-pihole-blocklists/master/lists/pi_blocklist_porn_all.list` |

OISD uses the **domainswild** endpoints (`*.apex`) so Windows DNS QRP `EQ,*.domain` covers subdomains — not the ABP root URLs (`https://big.oisd.nl/`).

**Auto-refresh:** the service re-downloads Steve Jenkins indexes + every enabled curated/custom URL on the **daily scheduled sync** (`ScheduledHour` / `ScheduledMinute`, default **02:00 local**) and whenever you click **Update now**.

**YouTube ads caveat:** DNS-only blocking is **partial**. This list helps on phones that use your DNS, but it will not kill all mobile YouTube ads (app-bundled / same-domain delivery). Pair with client-side blockers when needed.

## Prerequisites (Windows Server 2022)

- .NET 8 Runtime (or SDK to build)
- DNS Server role **or** RSAT DnsServer PowerShell module on the hub
- WinRM reachability + DNS admin rights on each target
- If DNS policies already replicate via AD, you may list a single target; otherwise list every DNS you want updated

<<<<<<< HEAD
### Prepare a DNS target (WIN32 1011)
=======
### Sync QRP idempotent (WIN32 9971)

`Mettre à jour` replaces all `DnsBlocker_*` policies in **one** PowerShell session (remove-all, then recreate chunks). If a stale policy remains (**WIN32 9971** / “already exists”), the push removes that name and retries Add — partial prior syncs no longer fail the farm.

### Préparer une cible DNS (WIN32 1011)
>>>>>>> b0a3adc (Make QRP sync idempotent for WIN32 9971 (policy already exists))

QRP policies need **Full Control** on `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server` on the target. DnsAdmins alone often yields **WIN32 1011**.

**From the app:** Configuration → **Prepare a target (WinRM)…**

1. Hostname (prefill from farm / machine domain)
2. Admin `DOMAIN\user` + password (DPAPI LocalMachine in `ProgramData\DnsBlocker\secrets` if saved — never logged)
3. Test WinRM / CIM
4. Remote prep: registry ACL for hub service account (or DnsAdmins), optional DnsAdmins membership, probe Add/Remove QRP
5. Target marked prepared; normal sync uses the hub service identity afterward

## Build

```powershell
dotnet build DnsBlocker.sln -c Release
dotnet test tests/DnsBlocker.Core.Tests
```

On Linux (CI / this cloud agent), build Core + Service + tests only — WPF requires Windows:

```bash
./scripts/build.sh
# or:
dotnet build src/DnsBlocker.Core src/DnsBlocker.Service tests/DnsBlocker.Core.Tests
dotnet test tests/DnsBlocker.Core.Tests
```

Product version is centralized in `Directory.Build.props` (`Version`).

## MSI release (Windows + WiX)

Release builds produce a Windows Installer package via [WiX Toolset 5](https://wixtoolset.org/) (`installer/DnsBlocker.Installer.wixproj`).

**Prerequisites (Windows):** .NET 8 SDK, WiX Toolset 5 (restored automatically via `WixToolset.Sdk/5.0.2` on first `dotnet build` of the installer).

```powershell
# Publish Service + UI (win-x64) and build artifacts/DnsBlocker-<Version>.msi
# Default msiUrl base: GitHub Releases download/v<Version>/
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -Version 1.2.0
.\scripts\build-release.ps1 -Version 1.2.0 -UpdateFeedBaseUrl https://example.com/releases

# Optional: self-contained publish
.\scripts\build-release.ps1 -SelfContained
```

Outputs:

| Artifact | Path |
|----------|------|
| MSI | `artifacts/DnsBlocker-<Version>.msi` |
| Updater feed | `artifacts/version.json` (`version`, `msiUrl`, `sha256`, `releaseNotes`, `publishedAt`) — upload next to the MSI on the GitHub release |

The MSI installs:

- Service → `C:\Program Files\DnsBlocker` (Windows Service **DnsBlocker** as LocalSystem, started at install)
- UI → `C:\Program Files\DnsBlocker\UI` (+ Start Menu shortcut)
- Tools → `doctor.ps1`, `Verify-Install.cmd` under `C:\Program Files\DnsBlocker`
- Data dir → `C:\ProgramData\DnsBlocker\logs`

**Runtime:** release MSIs built with `build-msi-linux.sh` (and `build-release.ps1 -SelfContained`) are **self-contained win-x64** — no separate .NET 8 install is required. Framework-dependent builds need .NET 8 Runtime (+ Desktop Runtime for the UI).

**After install / UI won’t open:** run elevated:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\DnsBlocker\doctor.ps1" -StartServiceIfStopped
```

Check `C:\ProgramData\DnsBlocker\logs\service-startup-failure.log` and `ui-crash.log` if the UI closes immediately.

**Upgrades:** MajorUpgrade (`AllowSameVersionUpgrades`, schedule `afterInstallInitialize`) stops the service before replacing files so the hub survives in-place upgrades. Fixed `UpgradeCode` in `installer/Package.wxs`.

### In-app updates

Default `UpdateFeedUrl` (also shown in the first-run wizard):

`https://github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/version.json`

That stable “latest” asset URL always follows the newest GitHub release that publishes `version.json`. Clear the field to disable checks. The service checks about daily; if `AutoInstallUpdates` is true it runs silent `msiexec /i … /qn` as LocalSystem (MSI ServiceControl stops/restarts the hub — do not stop the service yourself). With auto-install off, the UI notifies only (**Check for updates** / startup check). IPC: `GetAppVersion`, `CheckForUpdates`, `InstallUpdate`.

Publish both `DnsBlocker-<Version>.msi` and `version.json` as release assets on every tag so the latest feed stays valid.

**Linux MSI (wixl):** on this Cloud Agent / Ubuntu you can also produce an installable MSI without WiX Toolset:

```bash
./scripts/build-msi-linux.sh 1.0.0
# → artifacts/DnsBlocker-1.0.0.msi + artifacts/version.json
```

Requires `wixl` (`apt install wixl`). This is self-contained win-x64 (service + UI). Prefer `build-release.ps1` on Windows when you need a WiX Toolset 5 MSI identical to the installer project.

**Linux limitation (WiX 5):** `build-release.ps1` skips official WiX Toolset MSI compilation on non-Windows hosts.

## Install (hub)

### Preferred: MSI

```powershell
# Elevated — after downloading the release MSI
msiexec /i DnsBlocker-1.0.11.msi /l*v $env:TEMP\dnsblocker-install.log
```

**After MSI install (no PowerShell needed):**

1. **Setup assistant** opens (`DnsBlocker.UI.exe /setup`) — health check / repair.
2. **Open configuration** launches the first-run wizard (`/firstrun`) which writes `%ProgramData%\DnsBlocker\setup-complete.flag` + `setupWizardCompleted` when done.
3. Start Menu also has **WinDNS Blocker Setup assistant**; the main UI has **Wizard…** and repair flows (launches `Repair-DnsBlocker.cmd` under InstallRoot).

**Broken/sparse install** (GUI preferred; CLI fallback):

- Double-click `C:\Program Files\DnsBlocker\Repair-DnsBlocker.cmd` (or **Repair** in the wizard), or:
```powershell
irm https://raw.githubusercontent.com/vincenttisseront/WinDNS-Blocker/main/scripts/Repair-DnsBlocker.ps1 -OutFile $env:TEMP\Repair-DnsBlocker.ps1
powershell -ExecutionPolicy Bypass -File $env:TEMP\Repair-DnsBlocker.ps1
```

Server requirement: **Windows Server with Desktop Experience** (WPF UI). Server Core cannot host the UI.

### Alternative: scripted copy (dev)

```powershell
# From an elevated PowerShell session (self-contained by default)
.\scripts\install.ps1
.\scripts\doctor.ps1 -StartServiceIfStopped
```

Then open `C:\Program Files\DnsBlocker\UI\DnsBlocker.UI.exe` (or the Start Menu shortcut), set DNS targets (one FQDN per line), save, and click **Update now**.

Config/state:

- `C:\ProgramData\DnsBlocker\config.json`
- `C:\ProgramData\DnsBlocker\state.db`
- `C:\ProgramData\DnsBlocker\logs\` (includes `service-startup-failure.log` / `ui-crash.log` on failure)

### Development mock provider

Set `DNSBLOCKER_USE_MOCK=1` to force the in-memory DNS provider (also automatic on non-Windows).

## Limits

1. **No regex** — Steve Jenkins `regex.txt` cannot be imported into QRP.
2. **Policy size** — domains are chunked (~1000 FQDN/policy by default). Very large lists may still hit DNS API limits; consider reducing sources or raising operational monitoring.
3. **Stats** — Phase C uses hub-local DNS analytical ETW (`Microsoft-Windows-DNSServer`) when enabled; without Analytical logging the Stats tab shows an empty/actionable message. Top called/blocked domains come from SQLite `query_events` (ETW or synthetic), not a remote per-DC analytics API. “Blocked” means the query name is on the hub blocklist, not a confirmed QRP DENY on the wire.
4. **YouTube / CDN ads** — many mobile ad requests share the same hostnames as video delivery; pure DNS blocklists often cannot remove YouTube ads. Use **Status → Verify QRP policies** and **Stats** tops to confirm the hub is applying `DnsBlocker_*` policies and seeing queries — not to expect zero ads on phone YouTube.
5. **Sync ≠ applied** — a successful push records chunk counts in SQLite; live presence/enabled state is verified separately via `Get-DnsServerQueryResolutionPolicy` (IPC `GetTargetPolicyStatus`).

### Verify on a phone (point DNS at the farm)

1. Configure the hub target (e.g. `windc05.ar-systems.fr`) and run **Update now**.
2. On **Status**, click **Verify QRP policies** — expect `DnsBlocker_*` installed = enabled and matching the last sync count.
3. On the phone, set DNS to that same Windows DNS (or a resolver that forwards to it), browse, then **Stats → Load** (enable ETW collection on the hub first). Top called/blocked domains should move if the phone really uses that DNS.

## Uninstall

```powershell
# MSI
msiexec /x artifacts\DnsBlocker-1.0.0.msi /qn
# or Apps & Features

# Scripted install
.\scripts\uninstall.ps1
```
