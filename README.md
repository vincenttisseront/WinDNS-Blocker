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

`blocklists.txt` / `whitelists.txt` are **URLs to lists**, not raw domains. `regex.txt` is **not** applied (Windows QRP has no regex).

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
2. **Policy size** — domains are chunked (~1000 FQDN/policy by default). Very large lists may still hit DNS API limits; consider reducing sources or raising operational monitoring.
3. **Stats** — Phase C uses hub-local DNS analytical ETW (`Microsoft-Windows-DNSServer`) when enabled; without Analytical logging the Stats tab shows an empty/actionable message. Top called/blocked domains come from SQLite `query_events` (ETW or synthetic), not a remote per-DC analytics API. “Blocked” means the query name is on the hub blocklist, not a confirmed QRP DENY on the wire.
4. **YouTube / CDN ads** — many mobile ad requests share the same hostnames as video delivery; pure DNS blocklists often cannot remove YouTube ads. Use **Statut → Vérifier les politiques QRP** and **Stats** tops to confirm the hub is applying `DnsBlocker_*` policies and seeing queries — not to expect zero ads on phone YouTube.
5. **Sync ≠ applied** — a successful push records chunk counts in SQLite; live presence/enabled state is verified separately via `Get-DnsServerQueryResolutionPolicy` (IPC `GetTargetPolicyStatus`).

### Verify on a phone (point DNS at the farm)

1. Configure the hub target (e.g. `windc05.ar-systems.fr`) and run **Mettre à jour maintenant**.
2. On **Statut**, click **Vérifier les politiques QRP** — expect `DnsBlocker_*` installées = activées and matching the last sync count.
3. On the phone, set DNS to that same Windows DNS (or a resolver that forwards to it), browse, then **Stats → Charger** (enable ETW collection on the hub first). Top domaines appelés/bloqués should move if the phone really uses that DNS.

## Uninstall

```powershell
# MSI
msiexec /x artifacts\DnsBlocker-1.0.0.msi /qn
# or Apps & Features

# Scripted install
.\scripts\uninstall.ps1
```
