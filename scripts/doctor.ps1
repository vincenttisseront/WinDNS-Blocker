#Requires -Version 5.1
<#
.SYNOPSIS
  Post-install health check for WinDNS Blocker on Windows Server.
.DESCRIPTION
  Verifies binaries, ProgramData, Windows service state, named-pipe reachability,
  and prints actionable fixes. Exit 0 = healthy, 1 = problems found.

  Written for Windows PowerShell 5.1 (string concatenation only; no bare '>').
  Uses SIDs for icacls so French/localized BUILTIN names work.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = '',
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

function Get-InstallRootFromRegistry {
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($root in $roots) {
        $apps = Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'WinDNS Blocker*' }
        foreach ($app in $apps) {
            if ($app.InstallLocation -and (Test-Path -LiteralPath $app.InstallLocation)) {
                return $app.InstallLocation.TrimEnd('\')
            }
            if ($app.DisplayIcon) {
                $dir = Split-Path -Parent ([string]$app.DisplayIcon)
                if ($dir -and (Test-Path -LiteralPath $dir)) {
                    return $dir.TrimEnd('\')
                }
            }
        }
    }
    return $null
}

# Resolve install root: explicit param > registry > well-known paths
$candidates = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
    [void]$candidates.Add($InstallRoot.TrimEnd('\'))
}
$regRoot = Get-InstallRootFromRegistry
if ($regRoot) { [void]$candidates.Add($regRoot) }
if ($env:ProgramFiles) {
    [void]$candidates.Add((Join-Path $env:ProgramFiles 'DnsBlocker'))
}
$pf86 = ${env:ProgramFiles(x86)}
if ($pf86) {
    [void]$candidates.Add((Join-Path $pf86 'DnsBlocker'))
}
# Also try literal classic path
[void]$candidates.Add('C:\Program Files\DnsBlocker')
[void]$candidates.Add('C:\Program Files (x86)\DnsBlocker')

$resolved = $null
foreach ($c in $candidates) {
    if ([string]::IsNullOrWhiteSpace($c)) { continue }
    $svcProbe = Join-Path $c 'DnsBlocker.Service.exe'
    if (Test-Path -LiteralPath $svcProbe) {
        $resolved = $c
        break
    }
}
if (-not $resolved) {
    foreach ($c in $candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if (Test-Path -LiteralPath $c) {
            $resolved = $c
            break
        }
    }
}
if (-not $resolved) {
    $resolved = 'C:\Program Files\DnsBlocker'
}

$InstallRoot = $resolved

Write-Host 'WinDNS Blocker doctor' -ForegroundColor Cyan
Write-Host ('InstallRoot: ' + $InstallRoot)
Write-Host ('PowerShell: ' + $PSVersionTable.PSVersion.ToString() + ' | 64-bit process: ' + [Environment]::Is64BitProcess)
Write-Host ''

# Show what is actually present
if (Test-Path -LiteralPath $InstallRoot) {
    $entries = @(Get-ChildItem -LiteralPath $InstallRoot -Force -ErrorAction SilentlyContinue | Select-Object -First 25)
    Write-Host ('Contents of InstallRoot (' + $entries.Count + ' entries shown):')
    foreach ($e in $entries) {
        Write-Host ('  ' + $e.Name)
    }
    if ($entries.Count -eq 0) {
        Write-Warn 'InstallRoot exists but is empty.'
    }
    Write-Host ''
}
else {
    Write-Bad ('InstallRoot does not exist: ' + $InstallRoot)
}

# --- Binaries ---
$svcExe = Join-Path $InstallRoot 'DnsBlocker.Service.exe'
$uiExe = Join-Path $InstallRoot 'UI\DnsBlocker.UI.exe'

if (Test-Path -LiteralPath $svcExe) {
    Write-Ok ('Service binary: ' + $svcExe)
}
else {
    Write-Bad ('Missing service binary: ' + $svcExe)
    Write-Bad 'The MSI did not lay down DnsBlocker.Service.exe. Reinstall with logging:'
    Write-Bad '  msiexec /i DnsBlocker-1.0.5.msi /l*v %TEMP%\dnsblocker-install.log'
    Write-Bad 'Then open the log and search for InstallFiles / return value 3.'
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
    if (-not (Test-Path -LiteralPath $svcExe)) {
        Write-Warn 'No runtime DLLs and no service EXE - treat as incomplete MSI install (not framework-dependent).'
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
}

# --- ProgramData ---
$data = Join-Path $env:ProgramData 'DnsBlocker'
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

# ACL via SIDs (works on FR/EN): SYSTEM, Administrators, Users
try {
    & icacls.exe $data /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    Write-Ok ('ACL refreshed on ' + $data + ' (SYSTEM/Administrators Full, Users RX via SID)')
}
catch {
    Write-Warn ('Could not set ACL on ' + $data + ' : ' + $_.Exception.Message)
}

# --- Service ---
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    $hint = 'Windows service ''' + $ServiceName + ''' is not registered.'
    Write-Bad $hint
    if (Test-Path -LiteralPath $svcExe) {
        Write-Bad ('Register with (admin): New-Service -Name ' + $ServiceName + ' -BinaryPathName "' + $svcExe + '" -StartupType Automatic; Start-Service ' + $ServiceName)
    }
    else {
        Write-Bad 'Reinstall the MSI (binaries missing, cannot register the service yet).'
    }
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
                    Write-Bad ('Service still not Running after Start-Service (Status=' + [string]$svc.Status + '). Check Event Viewer / ' + $logs + '\service-startup-failure.log')
                }
            }
            catch {
                Write-Bad ('Start-Service failed: ' + $_.Exception.Message + '. See ' + $logs + '\service-startup-failure.log and Event Viewer - Windows Logs - Application')
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

# ARP product registration
$arp = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'WinDNS Blocker*' } |
    Select-Object -First 1
if ($null -ne $arp) {
    Write-Ok ('ARP product: ' + $arp.DisplayName + ' version=' + $arp.DisplayVersion)
}
else {
    Write-Warn 'No WinDNS Blocker entry under Uninstall registry (MSI may not have completed PublishProduct).'
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
    Write-Host 'If binaries are missing, uninstall any partial product then reinstall:'
    Write-Host '  msiexec /x {product-code}   OR Settings - Apps - WinDNS Blocker - Uninstall'
    Write-Host '  msiexec /i DnsBlocker-1.0.5.msi /l*v %TEMP%\dnsblocker-install.log'
    exit 1
}
