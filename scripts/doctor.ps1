#Requires -Version 5.1
<#
.SYNOPSIS
  Post-install health check for WinDNS Blocker on Windows Server.
.DESCRIPTION
  Verifies binaries, ProgramData, Windows service state, named-pipe reachability,
  and prints actionable fixes. Exit 0 = healthy, 1 = problems found.

  Written for Windows PowerShell 5.1 (avoid nested $() with commas inside
  double-quoted arguments, and avoid unescaped '>' redirection tokens).
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Program Files\DnsBlocker',
    [string]$ServiceName = 'DnsBlocker',
    [string]$PipeName = 'DnsBlocker',
    [switch]$StartServiceIfStopped
)

$ErrorActionPreference = 'Continue'
$failed = New-Object System.Collections.Generic.List[string]
$warned = New-Object System.Collections.Generic.List[string]

function Write-Ok {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ("[OK]  " + $Message) -ForegroundColor Green
}

function Write-Bad {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ("[FAIL] " + $Message) -ForegroundColor Red
    [void]$script:failed.Add($Message)
}

function Write-Warn {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ("[WARN] " + $Message) -ForegroundColor Yellow
    [void]$script:warned.Add($Message)
}

Write-Host 'WinDNS Blocker doctor' -ForegroundColor Cyan
Write-Host ('InstallRoot: ' + $InstallRoot)
Write-Host ''

# --- Binaries ---
$svcExe = Join-Path $InstallRoot 'DnsBlocker.Service.exe'
$uiExe = Join-Path $InstallRoot 'UI\DnsBlocker.UI.exe'

if (Test-Path -LiteralPath $svcExe) {
    Write-Ok ('Service binary: ' + $svcExe)
}
else {
    Write-Bad ('Missing service binary: ' + $svcExe + ' (reinstall MSI or run install.ps1)')
}

if (Test-Path -LiteralPath $uiExe) {
    Write-Ok ('UI binary: ' + $uiExe)
}
else {
    Write-Bad ('Missing UI binary: ' + $uiExe)
}

# Self-contained vs framework-dependent heuristic
$hostFxr = Join-Path $InstallRoot 'hostfxr.dll'
$coreClr = Join-Path $InstallRoot 'coreclr.dll'
if ((Test-Path -LiteralPath $hostFxr) -or (Test-Path -LiteralPath $coreClr)) {
    Write-Ok 'Looks self-contained (runtime shipped with install) - no separate .NET 8 install required for the service.'
}
else {
    Write-Warn 'No bundled runtime DLLs found. Framework-dependent install needs .NET 8 Runtime (and Desktop Runtime for the UI).'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnet) {
        Write-Ok ('dotnet on PATH: ' + $dotnet.Source)
    }
    else {
        Write-Bad 'dotnet not on PATH. Install .NET 8 Runtime from https://dotnet.microsoft.com/download/dotnet/8.0'
    }
}

# --- ProgramData ---
$data = 'C:\ProgramData\DnsBlocker'
$logs = Join-Path $data 'logs'
foreach ($dir in @($data, $logs)) {
    if (Test-Path -LiteralPath $dir) {
        Write-Ok ('Directory exists: ' + $dir)
    }
    else {
        try {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            Write-Warn ('Created missing directory: ' + $dir)
        }
        catch {
            Write-Bad ('Cannot create ' + $dir + ' : ' + $_.Exception.Message)
        }
    }
}

# ACL: SYSTEM + Admins full; Users read/execute (UI may read logs)
try {
    & icacls.exe $data /grant 'NT AUTHORITY\SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' 'BUILTIN\Users:(OI)(CI)RX' | Out-Null
    Write-Ok ('ACL refreshed on ' + $data + ' (SYSTEM/Administrators Full, Users RX)')
}
catch {
    Write-Warn ('Could not set ACL on ' + $data + ' : ' + $_.Exception.Message)
}

# --- Service ---
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    $hint = 'Windows service ''' + $ServiceName + ''' is not registered. Reinstall MSI or: New-Service -Name ' + $ServiceName + ' -BinaryPathName "' + $svcExe + '" -StartupType Automatic'
    Write-Bad $hint
}
else {
    $statusText = [string]$svc.Status
    $startTypeText = [string]$svc.StartType
    Write-Ok ('Service registered: ' + $ServiceName + ' (Status=' + $statusText + '; StartType=' + $startTypeText + ')')

    if ($svc.Status -ne 'Running') {
        if ($StartServiceIfStopped) {
            try {
                Start-Service -Name $ServiceName -ErrorAction Stop
                Start-Sleep -Seconds 2
                $svc.Refresh()
                if ($svc.Status -eq 'Running') {
                    Write-Ok 'Service started.'
                }
                else {
                    $failMsg = 'Service still not Running after Start-Service (Status=' + [string]$svc.Status + '). Check Event Viewer / ' + $logs + '\service-startup-failure.log'
                    Write-Bad $failMsg
                }
            }
            catch {
                $failMsg = 'Start-Service failed: ' + $_.Exception.Message + '. See ' + $logs + '\service-startup-failure.log and Event Viewer - Windows Logs - Application'
                Write-Bad $failMsg
            }
        }
        else {
            Write-Bad ('Service is ' + [string]$svc.Status + '. Run: Start-Service ' + $ServiceName + '   or re-run doctor with -StartServiceIfStopped')
        }
    }
}

# Service ImagePath
try {
    $regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $ServiceName
    $img = (Get-ItemProperty -Path $regPath -ErrorAction Stop).ImagePath
    Write-Ok ('Service ImagePath: ' + $img)
    $escapedRoot = [regex]::Escape($InstallRoot)
    if ($img -and ($img -notmatch $escapedRoot) -and ($img -notmatch 'DnsBlocker.Service')) {
        Write-Warn 'ImagePath does not look like the expected install location.'
    }
}
catch {
    Write-Warn 'Could not read service ImagePath from registry.'
}

# --- Named pipe ---
$pipePath = '\\.\pipe\' + $PipeName
if (Test-Path -LiteralPath $pipePath) {
    Write-Ok ('Named pipe present: ' + $pipePath + ' (service IPC listening)')
}
else {
    if (($null -ne $svc) -and ($svc.Status -eq 'Running')) {
        Write-Bad ('Service is Running but pipe ' + $pipePath + ' is missing - IPC not up yet or crashed. Check ' + $logs + ' and restart the service.')
    }
    else {
        Write-Warn ('Named pipe ' + $pipePath + ' not found (expected when service is stopped).')
    }
}

# Crash / startup logs
$crashUi = Join-Path $logs 'ui-crash.log'
$crashSvc = Join-Path $logs 'service-startup-failure.log'
if (Test-Path -LiteralPath $crashSvc) {
    Write-Warn ('Found service startup failure log: ' + $crashSvc)
    Get-Content -LiteralPath $crashSvc -Tail 20 | ForEach-Object { Write-Host ('    ' + $_) }
}
if (Test-Path -LiteralPath $crashUi) {
    Write-Warn ('Found UI crash log: ' + $crashUi)
    Get-Content -LiteralPath $crashUi -Tail 20 | ForEach-Object { Write-Host ('    ' + $_) }
}

# Desktop / Server Core hint (SKU 143 = Server Datacenter Core in some tables; caption check is primary)
$os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($null -ne $os) {
    $caption = [string]$os.Caption
    if (($caption -match 'Server') -and ($caption -match 'Core')) {
        Write-Warn 'Server Core detected: WPF UI will not run. Use Server with Desktop Experience, or manage via IPC scripts.'
    }
}

Write-Host ''
$failCount = $failed.Count
$warnCount = $warned.Count
if ($failCount -eq 0) {
    Write-Host ('Doctor result: HEALTHY (' + $warnCount + ' warning(s))') -ForegroundColor Green
    Write-Host ('Launch UI: "' + $uiExe + '"')
    exit 0
}
else {
    Write-Host ('Doctor result: FAILED (' + $failCount + ' error(s), ' + $warnCount + ' warning(s))') -ForegroundColor Red
    Write-Host 'Fix the [FAIL] items above, then re-run doctor.ps1'
    exit 1
}
