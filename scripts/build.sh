#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build "$ROOT/src/DnsBlocker.Core/DnsBlocker.Core.csproj" -c "${1:-Release}"
dotnet build "$ROOT/src/DnsBlocker.Service/DnsBlocker.Service.csproj" -c "${1:-Release}"
dotnet test "$ROOT/tests/DnsBlocker.Core.Tests/DnsBlocker.Core.Tests.csproj" -c "${1:-Release}" --no-build || \
  dotnet test "$ROOT/tests/DnsBlocker.Core.Tests/DnsBlocker.Core.Tests.csproj" -c "${1:-Release}"

if [[ "$(uname -s)" == "MINGW"* || "$(uname -s)" == "CYGWIN"* || "$(uname -s)" == MSYS* || -n "${WINDIR:-}" ]]; then
  dotnet build "$ROOT/src/DnsBlocker.UI/DnsBlocker.UI.csproj" -c "${1:-Release}"
else
  echo "Skipping WPF UI build on non-Windows host."
fi
