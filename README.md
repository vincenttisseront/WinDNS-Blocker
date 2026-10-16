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
dotnet build src/DnsBlocker.Core src/DnsBlocker.Service tests/DnsBlocker.Core.Tests
dotnet test tests/DnsBlocker.Core.Tests
```

## Install (hub)

```powershell
# From an elevated PowerShell session
.\scripts\install.ps1
```

Then open `C:\Program Files\DnsBlocker\UI\DnsBlocker.UI.exe`, set DNS targets (one FQDN per line), save, and click **Mettre à jour maintenant**.

Config/state:

- `C:\ProgramData\DnsBlocker\config.json`
- `C:\ProgramData\DnsBlocker\state.db`
- `C:\ProgramData\DnsBlocker\logs\`

### Development mock provider

Set `DNSBLOCKER_USE_MOCK=1` to force the in-memory DNS provider (also automatic on non-Windows).

## Limits

1. **No regex** — Steve Jenkins `regex.txt` cannot be imported into QRP.
2. **Policy size** — domains are chunked (~1000 FQDN/policy by default). Very large lists may still hit DNS API limits; consider reducing sources or raising operational monitoring.
3. **Stats** — Phase C uses DNS analytical ETW when enabled; without it the Stats tab shows an empty/actionable message (not a full Pi-hole live dashboard).

## Uninstall

```powershell
.\scripts\uninstall.ps1
```
