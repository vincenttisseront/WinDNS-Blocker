#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstall any broken/partial WinDNS Blocker install, download the release MSI, install, run doctor.

.DESCRIPTION
  Use this when doctor reports missing DnsBlocker.Service.exe / UI / unregistered service
  (sparse Program Files layout from older MSIs). Safe on French Windows Server (SID ACLs).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Repair-DnsBlocker.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Repair-DnsBlocker.ps1 -Version 1.0.6
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.6',
    [string]$MsiUrl = '',
    [string]$MsiPath = '',
    [string]$ExpectedSha256 = '',
    [string]$InstallRoot = 'C:\Program Files\DnsBlocker',
    [switch]$SkipDownload,
    [switch]$KeepLog
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host ('==> ' + $Message) -ForegroundColor Cyan
}

function Get-UninstallEntries {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $found = @()
    foreach ($p in $paths) {
        $found += @(Get-ItemProperty -Path $p -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'WinDNS Blocker*' })
    }
    return $found
}

function Uninstall-ExistingProducts {
    Write-Step 'Stopping service (if present)...'
    $svc = Get-Service -Name 'DnsBlocker' -ErrorAction SilentlyContinue
    if ($null -ne $svc) {
        Stop-Service -Name 'DnsBlocker' -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    $apps = Get-UninstallEntries
    if ($apps.Count -eq 0) {
        Write-Host 'No ARP Uninstall entry found (partial install is OK).'
        return
    }

    foreach ($app in $apps) {
        $name = [string]$app.DisplayName
        $ver = [string]$app.DisplayVersion
        Write-Step ('Uninstalling ' + $name + ' ' + $ver + '...')
        $code = $null
        if ($app.PSChildName -and ($app.PSChildName -match '^\{[0-9A-Fa-f-]+\}$')) {
            $code = $app.PSChildName
        }
        elseif ($app.UninstallString -match '\{[0-9A-Fa-f-]+\}') {
            $code = $Matches[0]
        }

        if ($code) {
            $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', $code, '/qb', '/norestart') -Wait -PassThru
            Write-Host ('msiexec /x exit: ' + $p.ExitCode)
        }
        elseif ($app.UninstallString) {
            Write-Host ('WARN: Could not parse product code; UninstallString=' + $app.UninstallString) -ForegroundColor Yellow
        }
    }

    Start-Sleep -Seconds 2
}

function Clear-PartialInstallRoot {
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) { return }

    $svc = Join-Path $Root 'DnsBlocker.Service.exe'
    $ui = Join-Path $Root 'UI\DnsBlocker.UI.exe'
    if ((Test-Path -LiteralPath $svc) -and (Test-Path -LiteralPath $ui)) {
        Write-Host 'InstallRoot already has Service+UI; leaving files for MSI MajorUpgrade.'
        return
    }

    Write-Step ('Removing partial InstallRoot: ' + $Root)
    try {
        Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Host ('WARN: could not fully remove InstallRoot: ' + $_.Exception.Message) -ForegroundColor Yellow
        Write-Host 'Close any Explorer windows on that folder and re-run if install still fails.'
    }
}

function Get-FileSha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

Write-Host 'WinDNS Blocker repair' -ForegroundColor Cyan
Write-Host ('Target version: ' + $Version)
Write-Host ''

Uninstall-ExistingProducts
Clear-PartialInstallRoot -Root $InstallRoot

# Ensure ProgramData exists with SID ACLs (FR/EN)
$data = Join-Path $env:ProgramData 'DnsBlocker'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'logs') | Out-Null
& icacls.exe $data /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' 2>$null | Out-Null

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $env:TEMP ('DnsBlocker-' + $Version + '.msi')
}

if (-not $SkipDownload) {
    if ([string]::IsNullOrWhiteSpace($MsiUrl)) {
        $MsiUrl = 'https://github.com/vincenttisseront/WinDNS-Blocker/releases/download/v' + $Version + '/DnsBlocker-' + $Version + '.msi'
    }
    Write-Step ('Downloading ' + $MsiUrl)
    Write-Host (' -> ' + $MsiPath)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $MsiUrl -OutFile $MsiPath -UseBasicParsing
}

if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw ('MSI not found: ' + $MsiPath)
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    $actual = Get-FileSha256 -Path $MsiPath
    $expect = $ExpectedSha256.ToLowerInvariant()
    if ($actual -ne $expect) {
        throw ('SHA-256 mismatch. expected=' + $expect + ' actual=' + $actual)
    }
    Write-Host ('SHA-256 OK: ' + $actual) -ForegroundColor Green
}
else {
    $actual = Get-FileSha256 -Path $MsiPath
    Write-Host ('SHA-256: ' + $actual + ' (verify against the GitHub release notes)')
}

$log = Join-Path $env:TEMP ('dnsblocker-repair-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
Write-Step ('Installing MSI (log: ' + $log + ')')
$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', $MsiPath, '/l*v', $log, '/qb', '/norestart') -Wait -PassThru
if ($p.ExitCode -ne 0) {
    Write-Host ('msiexec failed, exit=' + $p.ExitCode) -ForegroundColor Red
    Write-Host ('Search log for "return value 3" / InstallFiles: ' + $log)
    throw ('Install failed with exit code ' + $p.ExitCode)
}

$svcExe = Join-Path $InstallRoot 'DnsBlocker.Service.exe'
$uiExe = Join-Path $InstallRoot 'UI\DnsBlocker.UI.exe'
$doctor = Join-Path $InstallRoot 'doctor.ps1'

if (-not (Test-Path -LiteralPath $svcExe)) {
    throw ('After MSI install, still missing: ' + $svcExe + ' — see ' + $log)
}
if (-not (Test-Path -LiteralPath $uiExe)) {
    throw ('After MSI install, still missing: ' + $uiExe + ' — see ' + $log)
}
if (-not (Test-Path -LiteralPath $doctor)) {
    throw ('After MSI install, still missing: ' + $doctor + ' — see ' + $log)
}

Write-Step 'Running doctor...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $doctor -StartServiceIfStopped
$code = $LASTEXITCODE

if (-not $KeepLog) {
    # Keep log on failure for support
    if ($code -eq 0) {
        Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
    }
}

if ($code -ne 0) {
    Write-Host ('Repair finished but doctor FAILED. Log: ' + $log) -ForegroundColor Yellow
    exit $code
}

Write-Host ''
Write-Host 'Repair complete: doctor HEALTHY.' -ForegroundColor Green
Write-Host ('UI: ' + $uiExe)
exit 0
