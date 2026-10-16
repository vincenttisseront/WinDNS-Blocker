#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
  Reliable MSI install wrapper with verbose logging and post-install doctor check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,
    [string]$LogPath = (Join-Path $env:TEMP ('dnsblocker-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log'))
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw "MSI not found: $MsiPath"
}

Write-Host ('Installing ' + $MsiPath) -ForegroundColor Cyan
Write-Host ('Log: ' + $LogPath)

$args = @('/i', $MsiPath, '/l*v', $LogPath, '/qb')
$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $args -Wait -PassThru
if ($p.ExitCode -ne 0) {
    Write-Host ('msiexec exit code: ' + $p.ExitCode) -ForegroundColor Red
    Write-Host 'Search the log for "return value 3" and "InstallFiles".'
    throw ("Install failed with exit code " + $p.ExitCode)
}

Write-Host 'msiexec completed. Running doctor...' -ForegroundColor Cyan
$doctor = 'C:\Program Files\DnsBlocker\doctor.ps1'
if (-not (Test-Path -LiteralPath $doctor)) {
    throw 'doctor.ps1 missing after install - MSI did not deploy tools/binaries correctly.'
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $doctor -StartServiceIfStopped
exit $LASTEXITCODE
