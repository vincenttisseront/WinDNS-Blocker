#Requires -Version 5.1
<#
.SYNOPSIS
  Post-install health check for WinDNS Blocker on Windows Server.
.DESCRIPTION
  Verifies binaries, ProgramData, Windows service state, named-pipe reachability,
  and prints actionable fixes. Exit 0 = healthy, 1 = problems found.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\Program Files\DnsBlocker",
    [string]$ServiceName = "DnsBlocker",
    [string]$PipeName = "DnsBlocker",
    [switch]$StartServiceIfStopped
)

$ErrorActionPreference = "Continue"
$failed = @()
$warned = @()

function Write-Ok([string]$Message) { Write-Host "[OK]  $Message" -ForegroundColor Green }
function Write-Bad([string]$Message) { Write-Host "[FAIL] $Message" -ForegroundColor Red; $script:failed += $Message }
function Write-Warn([string]$Message) { Write-Host "[WARN] $Message" -ForegroundColor Yellow; $script:warned += $Message }

Write-Host "WinDNS Blocker doctor" -ForegroundColor Cyan
Write-Host "InstallRoot: $InstallRoot"
Write-Host ""

# --- Binaries ---
$svcExe = Join-Path $InstallRoot "DnsBlocker.Service.exe"
$uiExe = Join-Path $InstallRoot "UI\DnsBlocker.UI.exe"
$doctorSelf = Join-Path $InstallRoot "doctor.ps1"

if (Test-Path $svcExe) { Write-Ok "Service binary: $svcExe" }
else { Write-Bad "Missing service binary: $svcExe (reinstall MSI or run install.ps1)" }

if (Test-Path $uiExe) { Write-Ok "UI binary: $uiExe" }
else { Write-Bad "Missing UI binary: $uiExe" }

# Self-contained vs framework-dependent heuristic
$hostFxr = Join-Path $InstallRoot "hostfxr.dll"
$coreClr = Join-Path $InstallRoot "coreclr.dll"
if ((Test-Path $hostFxr) -or (Test-Path $coreClr)) {
    Write-Ok "Looks self-contained (runtime shipped with install) — no separate .NET 8 install required for the service."
}
else {
    Write-Warn "No bundled runtime DLLs found. Framework-dependent install needs .NET 8 Runtime (and Desktop Runtime for the UI)."
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        Write-Ok "dotnet on PATH: $($dotnet.Source)"
    }
    else {
        Write-Bad "dotnet not on PATH. Install .NET 8 Runtime from https://dotnet.microsoft.com/download/dotnet/8.0"
    }
}

# --- ProgramData ---
$data = "C:\ProgramData\DnsBlocker"
$logs = Join-Path $data "logs"
foreach ($dir in @($data, $logs)) {
    if (Test-Path $dir) { Write-Ok "Directory exists: $dir" }
    else {
        try {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            Write-Warn "Created missing directory: $dir"
        }
        catch {
            Write-Bad "Cannot create $dir : $($_.Exception.Message)"
        }
    }
}

# ACL: SYSTEM + Admins full; Users read/execute (UI may read logs)
try {
    icacls $data /grant "NT AUTHORITY\SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "BUILTIN\Users:(OI)(CI)RX" | Out-Null
    Write-Ok "ACL refreshed on $data (SYSTEM/Administrators Full, Users RX)"
}
catch {
    Write-Warn "Could not set ACL on $data : $($_.Exception.Message)"
}

# --- Service ---
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Bad "Windows service '$ServiceName' is not registered. Reinstall MSI or: New-Service -Name $ServiceName -BinaryPathName `"$svcExe`" -StartupType Automatic"
}
else {
    Write-Ok "Service registered: $ServiceName (Status=$($svc.Status), StartType=$($svc.StartType))"
    if ($svc.Status -ne "Running") {
        if ($StartServiceIfStopped) {
            try {
                Start-Service -Name $ServiceName -ErrorAction Stop
                Start-Sleep -Seconds 2
                $svc.Refresh()
                if ($svc.Status -eq "Running") { Write-Ok "Service started." }
                else { Write-Bad "Service still not Running after Start-Service (Status=$($svc.Status)). Check Event Viewer / $logs\service-startup-failure.log" }
            }
            catch {
                Write-Bad "Start-Service failed: $($_.Exception.Message). See $logs\service-startup-failure.log and Event Viewer > Windows Logs > Application"
            }
        }
        else {
            Write-Bad "Service is $($svc.Status). Run: Start-Service $ServiceName   or re-run doctor with -StartServiceIfStopped"
        }
    }
}

# Service ImagePath
try {
    $img = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -ErrorAction Stop).ImagePath
    Write-Ok "Service ImagePath: $img"
    if ($img -and ($img -notmatch [regex]::Escape($InstallRoot)) -and ($img -notmatch "DnsBlocker.Service")) {
        Write-Warn "ImagePath does not look like the expected install location."
    }
}
catch {
    Write-Warn "Could not read service ImagePath from registry."
}

# --- Named pipe ---
$pipePath = "\\.\pipe\$PipeName"
if (Test-Path $pipePath) {
    Write-Ok "Named pipe present: $pipePath (service IPC listening)"
}
else {
    if ($svc -and $svc.Status -eq "Running") {
        Write-Bad "Service is Running but pipe $pipePath is missing — IPC not up yet or crashed. Check $logs and restart the service."
    }
    else {
        Write-Warn "Named pipe $pipePath not found (expected when service is stopped)."
    }
}

# Crash / startup logs
$crashUi = Join-Path $logs "ui-crash.log"
$crashSvc = Join-Path $logs "service-startup-failure.log"
if (Test-Path $crashSvc) {
    Write-Warn "Found service startup failure log: $crashSvc"
    Get-Content $crashSvc -Tail 20 | ForEach-Object { Write-Host "    $_" }
}
if (Test-Path $crashUi) {
    Write-Warn "Found UI crash log: $crashUi"
    Get-Content $crashUi -Tail 20 | ForEach-Object { Write-Host "    $_" }
}

# Desktop / Server Core hint
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os -and $os.Caption -match "Server" -and $os.OperatingSystemSKU -eq 143) {
    Write-Warn "Server Core detected: WPF UI will not run. Use Server with Desktop Experience, or manage via IPC scripts."
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "Doctor result: HEALTHY ($($warned.Count) warning(s))" -ForegroundColor Green
    Write-Host "Launch UI: `"$uiExe`""
    exit 0
}
else {
    Write-Host "Doctor result: FAILED ($($failed.Count) error(s), $($warned.Count) warning(s))" -ForegroundColor Red
    Write-Host "Fix the [FAIL] items above, then re-run doctor.ps1"
    exit 1
}
