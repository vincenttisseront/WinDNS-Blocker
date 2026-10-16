<#
.SYNOPSIS
  Publishes DnsBlocker Service + UI and builds the WiX MSI release artifacts.

.DESCRIPTION
  On Windows (with .NET SDK + WiX Toolset 5 SDK restore):
    - Publishes win-x64 framework-dependent binaries to publish/service and publish/ui
    - Builds installer/DnsBlocker.Installer.wixproj -> artifacts/DnsBlocker-<Version>.msi
    - Writes artifacts/version.json (updater feed metadata) including MSI SHA-256

  On non-Windows hosts (e.g. Linux CI / Cloud Agent):
    - Publishes the Service for win-x64 when possible
    - Skips WPF UI publish and MSI build with a clear message
    - Leaves WiX sources ready to build on Windows

.PARAMETER Version
  Product version (SemVer-ish). Defaults to Directory.Build.props <Version> or 1.0.0.

.PARAMETER UpdateFeedBaseUrl
  Base URL for msiUrl in version.json (e.g. https://example.com/releases).
  Final URL: {UpdateFeedBaseUrl}/DnsBlocker-{Version}.msi

.PARAMETER SelfContained
  When set, publish self-contained win-x64 instead of framework-dependent.

.PARAMETER Configuration
  Build configuration (default Release).

.PARAMETER ReleaseNotes
  Optional release notes string written into version.json.
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$UpdateFeedBaseUrl = "https://example.com/releases",
    [switch]$SelfContained,
    [string]$Configuration = "Release",
    [string]$ReleaseNotes = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Join-Path $repoRoot "publish"
$serviceOut = Join-Path $publishRoot "service"
$uiOut = Join-Path $publishRoot "ui"
$artifactsDir = Join-Path $repoRoot "artifacts"
$installerProj = Join-Path $repoRoot "installer\DnsBlocker.Installer.wixproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"

function Get-DefaultVersion {
    if (Test-Path $propsPath) {
        [xml]$xml = Get-Content -Raw $propsPath
        $node = $xml.SelectSingleNode("//Version")
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            return $node.InnerText.Trim()
        }
    }
    return "1.0.0"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-DefaultVersion
}

$runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)

Write-Host "WinDNS Blocker release build" -ForegroundColor Cyan
Write-Host "  Version:       $Version"
Write-Host "  Configuration: $Configuration"
Write-Host "  SelfContained: $SelfContained"
Write-Host "  Host:          $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"

New-Item -ItemType Directory -Force -Path $serviceOut, $uiOut, $artifactsDir | Out-Null

$publishArgs = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", ($(if ($SelfContained) { "true" } else { "false" })),
    "-p:Version=$Version"
)

Write-Host "`nPublishing DnsBlocker.Service (win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "src\DnsBlocker.Service\DnsBlocker.Service.csproj") `
    -o $serviceOut @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Service publish failed (exit $LASTEXITCODE)." }

$uiPublished = $false
if ($runningOnWindows) {
    Write-Host "`nPublishing DnsBlocker.UI (win-x64)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repoRoot "src\DnsBlocker.UI\DnsBlocker.UI.csproj") `
        -o $uiOut @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "UI publish failed (exit $LASTEXITCODE)." }
    # Ensure on-disk .ico ships next to UI (Shortcut Icon table + ARP use installer Assets separately).
    $uiIcon = Join-Path $repoRoot "src\DnsBlocker.UI\Assets\DnsBlocker.ico"
    if (Test-Path $uiIcon) {
        Copy-Item -Force $uiIcon (Join-Path $uiOut "DnsBlocker.ico")
    }
    $uiPublished = $true
}
else {
    Write-Host "`nSkipping DnsBlocker.UI publish: WPF (net8.0-windows) requires Windows." -ForegroundColor Yellow
}

$msiPath = Join-Path $artifactsDir "DnsBlocker-$Version.msi"
$msiBuilt = $false

if (-not $runningOnWindows) {
    Write-Host "`nSkipping MSI build: WiX MSI packaging requires Windows + WiX Toolset 5 SDK." -ForegroundColor Yellow
    Write-Host "  WiX project is ready at: installer/DnsBlocker.Installer.wixproj"
    Write-Host "  On Windows, re-run: .\scripts\build-release.ps1 -Version $Version"
}
elseif (-not $uiPublished) {
    Write-Host "`nSkipping MSI build: UI publish output is required." -ForegroundColor Yellow
}
else {
    Write-Host "`nBuilding WiX MSI..." -ForegroundColor Cyan
    $servicePublishDir = $serviceOut.TrimEnd('\') + '\'
    $uiPublishDir = $uiOut.TrimEnd('\') + '\'

    dotnet build $installerProj `
        -c $Configuration `
        -p:Version=$Version `
        -p:ServicePublishDir=$servicePublishDir `
        -p:UiPublishDir=$uiPublishDir
    if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed (exit $LASTEXITCODE)." }

    if (-not (Test-Path $msiPath)) {
        # WiX SDK may place output under artifacts/ or artifacts/Release/
        $found = Get-ChildItem -Path $artifactsDir -Filter "DnsBlocker-$Version.msi" -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) {
            if ($found.FullName -ne $msiPath) {
                Copy-Item -Force $found.FullName $msiPath
            }
        }
        else {
            throw "MSI not found after build. Expected: $msiPath"
        }
    }
    $msiBuilt = $true
    Write-Host "MSI: $msiPath" -ForegroundColor Green
}

# version.json — always written; sha256/msiUrl filled when MSI exists
$sha256 = $null
if ($msiBuilt -and (Test-Path $msiPath)) {
    $hash = Get-FileHash -Path $msiPath -Algorithm SHA256
    $sha256 = $hash.Hash.ToLowerInvariant()
}

$baseUrl = $UpdateFeedBaseUrl.TrimEnd('/')
$msiUrl = "$baseUrl/DnsBlocker-$Version.msi"
if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = "WinDNS Blocker $Version"
}

$versionInfo = [ordered]@{
    version      = $Version
    msiUrl       = $msiUrl
    sha256       = $(if ($sha256) { $sha256 } else { $null })
    releaseNotes = $ReleaseNotes
    publishedAt  = (Get-Date).ToUniversalTime().ToString("o")
}

$versionJsonPath = Join-Path $artifactsDir "version.json"
$versionInfo | ConvertTo-Json -Depth 5 | Set-Content -Path $versionJsonPath -Encoding utf8
Write-Host "`nWrote $versionJsonPath" -ForegroundColor Green

if (-not $msiBuilt) {
    Write-Host @"

Linux / non-Windows summary
  - Service publish: $serviceOut
  - UI / MSI: skipped (need Windows)
  - To produce the MSI on Windows:
      .\scripts\build-release.ps1 -Version $Version -UpdateFeedBaseUrl '$UpdateFeedBaseUrl'
"@ -ForegroundColor Yellow
    exit 0
}

Write-Host @"

Release artifacts
  MSI:          $msiPath
  SHA-256:      $sha256
  version.json: $versionJsonPath
"@ -ForegroundColor Green
