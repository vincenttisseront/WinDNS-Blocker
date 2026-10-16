#!/usr/bin/env bash
# Embed a .ico into a Windows PE (.exe) after Linux cross-publish.
# .NET ApplicationIcon is not applied when publishing win-x64 from Linux.
set -euo pipefail

EXE="${1:?usage: embed-win-icon.sh <exe> <ico>}"
ICO="${2:?usage: embed-win-icon.sh <exe> <ico>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RCEDIT="${RCEDIT:-$ROOT/tools/rcedit-x64.exe}"

EXE="$(readlink -f "$EXE")"
ICO="$(readlink -f "$ICO")"

if [[ ! -f "$EXE" ]]; then
  echo "exe not found: $EXE" >&2
  exit 1
fi
if [[ ! -f "$ICO" ]]; then
  echo "ico not found: $ICO" >&2
  exit 1
fi

if [[ ! -f "$RCEDIT" ]]; then
  mkdir -p "$(dirname "$RCEDIT")"
  echo "Downloading rcedit-x64.exe..."
  curl -fsSL -o "$RCEDIT" \
    https://github.com/electron/rcedit/releases/download/v2.0.0/rcedit-x64.exe
fi

if ! command -v wine >/dev/null 2>&1; then
  echo "wine is required to embed icons on Linux (apt install wine)" >&2
  exit 1
fi

# Wine maps the Linux FS to Z:; absolute paths work when passed through.
WINEDEBUG=-all wine "$RCEDIT" "$EXE" --set-icon "$ICO"

if command -v wrestool >/dev/null 2>&1; then
  if ! wrestool -l "$EXE" 2>/dev/null | grep -Eq 'type=group_icon|--type=14'; then
    echo "WARNING: no group_icon resource found after embed: $EXE" >&2
    exit 1
  fi
fi

echo "Embedded icon into $EXE"
