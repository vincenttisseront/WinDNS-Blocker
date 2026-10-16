#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the DnsBlocker Windows Service and copies the WPF UI.
.NOTES
  Run on the hub server (one of the DNS servers or a domain member with DNS admin rights).
  Prerequisites: .NET 8 Runtime (or SDK), DNS Server tools / RSAT DnsServer module, WinRM to targets.
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\publish"),
    [string]$InstallRoot = "C:\Program Files\DnsBlocker",
    [string]$ServiceName = "DnsBlocker",
    [string]$ServiceDisplayName = "WinDNS Blocker Hub"
)

$ErrorActionPreference = "Stop"

Write-Host "Publishing projects..." -ForegroundColor Cyan
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
dotnet publish (Join-Path $repoRoot "src\DnsBlocker.Service\DnsBlocker.Service.csproj") -c Release -o (Join-Path $PublishDir "service")
dotnet publish (Join-Path $repoRoot "src\DnsBlocker.UI\DnsBlocker.UI.csproj") -c Release -o (Join-Path $PublishDir "ui")

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
New-Item -ItemType Directory -Force -Path "C:\ProgramData\DnsBlocker\logs" | Out-Null

Copy-Item -Path (Join-Path $PublishDir "service\*") -Destination $InstallRoot -Recurse -Force
Copy-Item -Path (Join-Path $PublishDir "ui\*") -Destination (Join-Path $InstallRoot "UI") -Recurse -Force

$svcExe = Join-Path $InstallRoot "DnsBlocker.Service.exe"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service $ServiceName..."
New-Service -Name $ServiceName `
    -BinaryPathName "`"$svcExe`"" `
    -DisplayName $ServiceDisplayName `
    -Description "Synchronizes Steve Jenkins-style DNS blocklists into Windows DNS Query Resolution Policies across a farm of DNS servers." `
    -StartupType Automatic | Out-Null

# Allow the service account to manage DNS remotely as needed (default LocalSystem).
Start-Service -Name $ServiceName
Write-Host "Service started." -ForegroundColor Green

$ui = Join-Path $InstallRoot "UI\DnsBlocker.UI.exe"
Write-Host "UI: $ui"
Write-Host "Config: C:\ProgramData\DnsBlocker\config.json"
Write-Host "Done. Configure DNS targets in the UI, then click 'Mettre a jour maintenant'."
