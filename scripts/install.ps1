#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the DnsBlocker Windows Service and WPF UI (dev / sideload path).
.NOTES
  Default publish is win-x64 **self-contained** so Windows Server does not need a separate .NET 8 install.
  For MSI installs prefer the release MSI; this script is the fallback/manual path.
  Prerequisites: DNS Server tools / RSAT DnsServer on the hub when applying real QRP (mock mode otherwise).
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\publish"),
    [string]$InstallRoot = "C:\Program Files\DnsBlocker",
    [string]$ServiceName = "DnsBlocker",
    [string]$ServiceDisplayName = "WinDNS Blocker Hub",
    [switch]$FrameworkDependent,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "WinDNS Blocker install" -ForegroundColor Cyan
Write-Host "  SelfContained: $(-not $FrameworkDependent)"

if (-not $SkipPublish) {
    Write-Host "Publishing projects..." -ForegroundColor Cyan
    $publishArgs = @("-c", "Release", "-r", "win-x64", "--self-contained", ($(if ($FrameworkDependent) { "false" } else { "true" })))
    dotnet publish (Join-Path $repoRoot "src\DnsBlocker.Service\DnsBlocker.Service.csproj") -o (Join-Path $PublishDir "service") @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }
    dotnet publish (Join-Path $repoRoot "src\DnsBlocker.UI\DnsBlocker.UI.csproj") -o (Join-Path $PublishDir "ui") @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "UI publish failed." }
}

$serviceSrc = Join-Path $PublishDir "service"
$uiSrc = Join-Path $PublishDir "ui"
if (-not (Test-Path (Join-Path $serviceSrc "DnsBlocker.Service.exe"))) {
    throw "Missing DnsBlocker.Service.exe under $serviceSrc"
}
if (-not (Test-Path (Join-Path $uiSrc "DnsBlocker.UI.exe"))) {
    throw "Missing DnsBlocker.UI.exe under $uiSrc"
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot "UI") | Out-Null
New-Item -ItemType Directory -Force -Path "C:\ProgramData\DnsBlocker\logs" | Out-Null
icacls "C:\ProgramData\DnsBlocker" /grant "NT AUTHORITY\SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "BUILTIN\Users:(OI)(CI)RX" | Out-Null

Write-Host "Copying binaries to $InstallRoot ..."
Copy-Item -Path (Join-Path $serviceSrc "*") -Destination $InstallRoot -Recurse -Force
Copy-Item -Path (Join-Path $uiSrc "*") -Destination (Join-Path $InstallRoot "UI") -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot "doctor.ps1") -Destination (Join-Path $InstallRoot "doctor.ps1") -Force
Copy-Item -Path (Join-Path $PSScriptRoot "Verify-Install.cmd") -Destination (Join-Path $InstallRoot "Verify-Install.cmd") -Force -ErrorAction SilentlyContinue

$svcExe = Join-Path $InstallRoot "DnsBlocker.Service.exe"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service $ServiceName..."
# Quote path for spaces under Program Files
New-Service -Name $ServiceName `
    -BinaryPathName "`"$svcExe`"" `
    -DisplayName $ServiceDisplayName `
    -Description "Synchronizes Steve Jenkins-style DNS blocklists into Windows DNS Query Resolution Policies across a farm of DNS servers." `
    -StartupType Automatic | Out-Null

try {
    Start-Service -Name $ServiceName -ErrorAction Stop
    Start-Sleep -Seconds 2
    $status = (Get-Service -Name $ServiceName).Status
    if ($status -ne "Running") {
        throw "Service status is $status"
    }
    Write-Host "Service started." -ForegroundColor Green
}
catch {
    Write-Host "ERROR: service failed to start: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "See C:\ProgramData\DnsBlocker\logs\service-startup-failure.log and Event Viewer."
    & (Join-Path $InstallRoot "doctor.ps1")
    throw
}

$doctor = Join-Path $InstallRoot "doctor.ps1"
Write-Host "Running doctor..." -ForegroundColor Cyan
& $doctor -StartServiceIfStopped
if ($LASTEXITCODE -ne 0) {
    Write-Host "Install completed with doctor failures — fix before using the UI." -ForegroundColor Yellow
    exit $LASTEXITCODE
}

$ui = Join-Path $InstallRoot "UI\DnsBlocker.UI.exe"
Write-Host "UI: $ui"
Write-Host "Config: C:\ProgramData\DnsBlocker\config.json"
Write-Host "Done."
