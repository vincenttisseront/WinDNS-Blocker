# WinDNS Blocker

Hub Windows (.NET 8) that downloads [Steve Jenkins pi-hole-lists](https://github.com/stevejenkins/pi-hole-lists) (list-of-lists), expands them into domains, and pushes **DNS Query Resolution Policies** to **N Windows DNS servers** from a **single service**.

## Architecture

- **DnsBlocker.Service** — Windows Service on one hub machine (timer + named-pipe IPC)
- **DnsBlocker.UI** — WPF thick client on the same hub (config, farm, logs, domain search, stats)
- **DnsBlocker.Core** — ingest, chunking, PowerShell/`-ComputerName` provider, SQLite state

```
Hub (service + UI)
  └─ WinRM/CIM ─► DNS-A, DNS-B, … DNS-N   (policies DnsBlocker_0001…)
```

`blocklists.txt` / `whitelists.txt` are **URLs to list indexes**, not raw domains. `regex.txt` is **not** applied (Windows QRP has no regex).

### Curated extra blocklists

In addition to Steve Jenkins LoL, the hub can ingest **curated ExtraBlocklistUrls** (toggles in **Configuration**). Defaults on first run:

| Toggle | Default | Raw URL |
|--------|---------|---------|
| YouTube ads (kboghdady) | on | `https://raw.githubusercontent.com/kboghdady/youTube_ads_4_pi-hole/master/youtubelist.txt` |
| OISD big | on | `https://big.oisd.nl/domainswild` |
| OISD NSFW (family) | on | `https://nsfw.oisd.nl/domainswild` |
| Chadmayfield porn (adult) | **off** | `https://raw.githubusercontent.com/chadmayfield/my-pihole-blocklists/master/lists/pi_blocklist_porn_all.list` |

OISD uses the **domainswild** endpoints (`*.apex`) so Windows DNS QRP `EQ,*.domain` covers subdomains — not the ABP root URLs (`https://big.oisd.nl/`) Vincent may have seen truncated.

**Auto-refresh:** the service re-downloads Steve Jenkins indexes + every enabled curated/custom URL on the **daily scheduled sync** (`ScheduledHour` / `ScheduledMinute`, default **02:00 local**) and whenever you click **Mettre à jour maintenant**.

**YouTube ads caveat:** DNS-only blocking is **partial**. This list helps on phones that use your DNS, but it will not kill all mobile YouTube ads (app-bundled / same-domain delivery). Pair with client-side blockers when needed.

## Prerequisites (Windows Server 2022)

- .NET 8 Runtime (or SDK to build)
- DNS Server role **or** RSAT DnsServer PowerShell module on the hub
- WinRM reachability + DNS admin rights on each target
- If DNS policies already replicate via AD, you may list a single target; otherwise list every DNS you want updated

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

Product version is centralized in `Directory.Build.props` (`Version`, default `1.0.0`).

## MSI release (Windows + WiX)

Release builds produce a Windows Installer package via [WiX Toolset 5](https://wixtoolset.org/) (`installer/DnsBlocker.Installer.wixproj`).

**Prerequisites (Windows):** .NET 8 SDK, WiX Toolset 5 (restored automatically via `WixToolset.Sdk/5.0.2` on first `dotnet build` of the installer).

```powershell
# Publish Service + UI (win-x64) and build artifacts/DnsBlocker-<Version>.msi
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -Version 1.2.0 -UpdateFeedBaseUrl https://example.com/releases

# Optional: self-contained publish
.\scripts\build-release.ps1 -SelfContained
```

Outputs:

| Artifact | Path |
|----------|------|
| MSI | `artifacts/DnsBlocker-<Version>.msi` |
| Updater feed | `artifacts/version.json` (`version`, `msiUrl`, `sha256`, `releaseNotes`, `publishedAt`) |

The MSI installs:

- Service → `C:\Program Files\DnsBlocker` (Windows Service **DnsBlocker**, started at install)
- UI → `C:\Program Files\DnsBlocker\UI` (+ Start Menu shortcut)
- Data dir → `C:\ProgramData\DnsBlocker\logs`

**Upgrades:** MajorUpgrade (`AllowSameVersionUpgrades`, schedule `afterInstallInitialize`) stops the service before replacing files so the hub survives in-place upgrades. Fixed `UpgradeCode` in `installer/Package.wxs`.

### In-app updates

Point `UpdateFeedUrl` in config (or the Configuration tab) at a published `version.json`. The service checks about daily; if `AutoInstallUpdates` is true it runs silent `msiexec /i … /qn` as LocalSystem (MSI ServiceControl stops/restarts the hub — do not stop the service yourself). With auto-install off, the UI notifies only (`Vérifier les mises à jour` / startup check). IPC: `GetAppVersion`, `CheckForUpdates`, `InstallUpdate`.

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
# Elevated — after building or downloading the MSI
msiexec /i artifacts\DnsBlocker-1.0.0.msi /qn
# or double-click the MSI
```

### Alternative: scripted copy (dev)

```powershell
# From an elevated PowerShell session
.\scripts\install.ps1
```

Then open `C:\Program Files\DnsBlocker\UI\DnsBlocker.UI.exe` (or the Start Menu shortcut), set DNS targets (one FQDN per line), save, and click **Mettre à jour maintenant**.

Config/state:

- `C:\ProgramData\DnsBlocker\config.json`
- `C:\ProgramData\DnsBlocker\state.db`
- `C:\ProgramData\DnsBlocker\logs\`

### Development mock provider

Set `DNSBLOCKER_USE_MOCK=1` to force the in-memory DNS provider (also automatic on non-Windows).

## Limits

1. **No regex** — Steve Jenkins `regex.txt` cannot be imported into QRP.
2. **Policy size** — domains are chunked (~1000 FQDN/policy by default). Very large lists (e.g. Chadmayfield ~1.9M) may still hit DNS API limits; keep the adult toggle off unless you accept many QRP policies.
3. **Stats** — Phase C uses DNS analytical ETW when enabled; without it the Stats tab shows an empty/actionable message (not a full Pi-hole live dashboard).
4. **YouTube / mobile ads** — DNS blocklists cannot remove every in-app ad; curated YouTube + OISD lists reduce domains, they do not guarantee an ad-free YouTube app.

## Uninstall

```powershell
# MSI
msiexec /x artifacts\DnsBlocker-1.0.0.msi /qn
# or Apps & Features

# Scripted install
.\scripts\uninstall.ps1
```
