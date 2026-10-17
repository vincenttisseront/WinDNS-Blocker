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

### Prove QRP actually blocks (manual)

On the DNS server (elevated), run `scripts/Prove-QrpBlock.ps1`. The Microsoft-supported shape is:

```powershell
Add-DnsServerQueryResolutionPolicy -Name DnsBlocker_Test -Action IGNORE -Fqdn 'EQ,beeg.com,*.beeg.com' -ProcessingOrder 1 -PassThru
```

Server-level QueryProcessing (no `-ZoneName`, no `-ApplyOnRecursion`). After DNS restart, `Resolve-DnsName beeg.com -Server <dc>` must not return public A/AAAA. Hub sync creates a sentinel `DnsBlocker_00001` with that FQDN and **fails** the target if smoke/canary proof does not pass.

### Idempotent QRP sync (WIN32 9971)

**Update now** replaces all `DnsBlocker_*` policies in **one** PowerShell session (remove-all, then recreate chunks). If a stale policy remains (**WIN32 9971** / “already exists”), the push removes that name and retries Add — partial prior syncs no longer fail the farm.

### Prepare a DNS target (WIN32 1011)

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

**Upgrades:** MajorUpgrade (`AllowSameVersionUpgrades`, `RemoveExistingProducts` after `InstallFinalize`) so a failed mid-upgrade cannot wipe `DnsBlocker.UI.exe` while leaving Start Menu shortcuts. Fixed `UpgradeCode` in `installer/Package.wxs`.

### In-app updates

Default `UpdateFeedUrl` (also shown in the first-run wizard):

`https://github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/version.json`

That stable “latest” asset URL always follows the newest GitHub release that publishes `version.json`. Clear the field to disable checks. **Check for updates** downloads the MSI in the UI, launches elevated `msiexec /qb`, then **exits the UI** so files unlock. Auto-install (service) kills the UI process first, then runs detached silent msiexec. IPC: `GetAppVersion`, `CheckForUpdates`, `InstallUpdate`.

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

**Broken/sparse install** (Missing Shortcut / UI.exe gone after a bad in-app update):

1. Download `DnsBlocker-1.0.20.msi` (or newer) into Downloads.
2. Elevated CMD: run `Recover-DnsBlocker.cmd` from the MSI payload, or copy it from the repo `scripts\` folder next to the MSI and double-click it.
3. Or manual:
```cmd
wmic product where "Name like 'WinDNS Blocker%%'" call uninstall
msiexec /x {868B963E-5A3E-508F-9667-3C1EA232098D} /qn
msiexec /x {60726742-C306-5F22-B988-CC3CB3A4AC43} /qn
del /f /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\WinDNS Blocker\*.*"
rd /s /q "%ProgramFiles%\DnsBlocker"
msiexec /i "%USERPROFILE%\Downloads\DnsBlocker-1.0.25.msi" /qb
```

ProductCodes: 1.0.20=`{868B963E-…098D}`, 1.0.21=`{60726742-…AC43}`, 1.0.22=`{6AEED747-…5C2C}` (1.0.23 stamped at release).

If `UI\DnsBlocker.UI.exe` still exists, `Repair-DnsBlocker.cmd` opens Setup; if missing (or version stuck) use `Recover-DnsBlocker.cmd`.

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
2. **Policy size** — domains are packed under an ~8k-char FQDN criterion budget (~200/policy by default). Oversized criteria can Add successfully while leaving empty/inert FQDN criteria (**Succeeded ≠ block**). Sync verifies Enabled counts, optionally restarts the DNS Server service, and smoke-tests Resolve-DnsName. Prefer IGNORE (drop) over DENY (REFUSED / client failover).
3. **Stats** — When collection is enabled, the hub turns on `Microsoft-Windows-DNSServer/Analytical` on each farm DNS target and polls QUERY_RECEIVED events into SQLite `query_events` (local ETW on the hub alone is not enough when DNS is on another host, e.g. windc05). Load explains why the window is empty (collector off, analytical not enabled, clients not using farm DNS / DoH). “Blocked” in tops means the query name matches the hub blocklist (including `*.apex` / apex expansion).
4. **YouTube / CDN ads** — many mobile ad requests share the same hostnames as video delivery; pure DNS blocklists often cannot remove YouTube ads. Use **Status → Verify QRP policies** and **Stats** tops to confirm the hub is applying `DnsBlocker_*` policies and seeing queries — not to expect zero ads on phone YouTube.
5. **Sync ≠ applied** — a successful push records chunk counts in SQLite; live presence/enabled state is verified separately via `Get-DnsServerQueryResolutionPolicy` (IPC `GetTargetPolicyStatus`).

### Verify blocking (hub ≠ DNS target)

QRP policies are pushed to **farm targets** (e.g. `windc05.ar-systems.fr`). The hub UI on `windc04` does **not** block `nslookup` against `::1` / localhost — that hits the hub’s own DNS, which has no `DnsBlocker_*` policies.

1. Configure the hub target (e.g. `windc05.ar-systems.fr`) and run **Update now**.
2. On **Status**, click **Verify QRP policies** — expect `DnsBlocker_*` installed = enabled and matching the last sync count.
3. On **Status**, use **Test blocking** with a listed domain (e.g. `beeg.com`). The hub runs `Resolve-DnsName -Server <target>`. Expect REFUSED / NXDOMAIN / timeout — not Cloudflare IPs.
   - Manual check: `nslookup beeg.com windc05.ar-systems.fr`
4. Sync expands `*.apex` → also include apex (`*.beeg.com` ⇒ `beeg.com`) because Windows QRP `EQ,*.beeg.com` does not match the apex name alone.
5. On the phone, set DNS to that same Windows DNS only (disable DoH / private DNS). Prefer QRP action **IGNORE** (default) so stubs are less likely to fail over to `8.8.8.8` after REFUSED.
6. **Stats → Enable collection on DNS targets** (turns on remote `Microsoft-Windows-DNSServer/Analytical` + hub ingest), browse, then **Load**. Top called/blocked populate only for queries that hit the farm DNS.

## Uninstall

```powershell
# MSI
msiexec /x artifacts\DnsBlocker-1.0.0.msi /qn
# or Apps & Features

# Scripted install
.\scripts\uninstall.ps1
```
