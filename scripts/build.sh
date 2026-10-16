#!/usr/bin/env bash
# Build / test what we can on this host.
# MSI packaging (WiX Toolset 5) requires Windows — see scripts/build-release.ps1.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${1:-Release}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

VERSION=""
if [[ -f "$ROOT/Directory.Build.props" ]]; then
  VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props" | head -1 || true)"
fi
VERSION="${VERSION:-1.0.0}"

echo "Building DnsBlocker (config=$CONFIG, version=$VERSION)"

dotnet build "$ROOT/src/DnsBlocker.Core/DnsBlocker.Core.csproj" -c "$CONFIG" -p:Version="$VERSION"
dotnet build "$ROOT/src/DnsBlocker.Service/DnsBlocker.Service.csproj" -c "$CONFIG" -p:Version="$VERSION"
dotnet test "$ROOT/tests/DnsBlocker.Core.Tests/DnsBlocker.Core.Tests.csproj" -c "$CONFIG" --no-build || \
  dotnet test "$ROOT/tests/DnsBlocker.Core.Tests/DnsBlocker.Core.Tests.csproj" -c "$CONFIG"

is_windows=0
case "$(uname -s)" in
  MINGW*|CYGWIN*|MSYS*) is_windows=1 ;;
esac
if [[ -n "${WINDIR:-}" ]]; then
  is_windows=1
fi

if [[ "$is_windows" -eq 1 ]]; then
  dotnet build "$ROOT/src/DnsBlocker.UI/DnsBlocker.UI.csproj" -c "$CONFIG" -p:Version="$VERSION"
  echo ""
  echo "To publish + build the MSI on Windows:"
  echo "  pwsh ./scripts/build-release.ps1 -Version $VERSION"
else
  echo ""
  echo "Skipping WPF UI build on non-Windows host."
  echo "Publishing Service for win-x64 (framework-dependent) so release folders are partially ready..."
  mkdir -p "$ROOT/publish/service"
  dotnet publish "$ROOT/src/DnsBlocker.Service/DnsBlocker.Service.csproj" \
    -c "$CONFIG" \
    -r win-x64 \
    --self-contained false \
    -p:Version="$VERSION" \
    -o "$ROOT/publish/service"
  echo ""
  echo "MSI packaging requires Windows + WiX Toolset 5 (SDK-style installer/DnsBlocker.Installer.wixproj)."
  echo "  On Windows:  pwsh ./scripts/build-release.ps1 -Version $VERSION"
  echo "  WiX sources: installer/Package.wxs (ready to build; UI publish + MSI skipped here)."
fi
