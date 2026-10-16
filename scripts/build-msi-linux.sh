#!/usr/bin/env bash
# Build a Windows MSI on Linux using wixl (msitools). Produces artifacts/DnsBlocker-<Version>.msi
# and artifacts/version.json. Not identical to WiX Toolset 5, but installable on Windows.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props" | head -1 || echo 1.0.0)}"
UPDATE_FEED_BASE="${UPDATE_FEED_BASE_URL:-https://github.com/vincenttisseront/WinDNS-Blocker/releases/download/v${VERSION}}"
CONFIGURATION="${CONFIGURATION:-Release}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

PUBLISH="$ROOT/publish"
SERVICE_OUT="$PUBLISH/service"
UI_OUT="$PUBLISH/ui"
ARTIFACTS="$ROOT/artifacts"
STAGE="$ROOT/artifacts/msi-stage"
WXS="$ARTIFACTS/DnsBlocker.generated.wxs"
MSI="$ARTIFACTS/DnsBlocker-$VERSION.msi"
ICON_SRC="$ROOT/src/DnsBlocker.UI/Assets/DnsBlocker.ico"
if [[ ! -f "$ICON_SRC" ]]; then
  ICON_SRC="$ROOT/installer/Assets/DnsBlocker.ico"
fi

command -v wixl >/dev/null || { echo "wixl required (apt install wixl)"; exit 1; }
[[ -f "$ICON_SRC" ]] || { echo "missing icon: $ICON_SRC"; exit 1; }

rm -rf "$STAGE" "$ARTIFACTS"
mkdir -p "$SERVICE_OUT" "$UI_OUT" "$ARTIFACTS" "$STAGE/DnsBlocker/UI" "$STAGE/ProgramData/DnsBlocker/logs"

echo "==> Publishing Service + UI (win-x64 self-contained)"
dotnet publish "$ROOT/src/DnsBlocker.Service/DnsBlocker.Service.csproj" \
  -c "$CONFIGURATION" -r win-x64 --self-contained true -o "$SERVICE_OUT" \
  -p:Version="$VERSION"
dotnet publish "$ROOT/src/DnsBlocker.UI/DnsBlocker.UI.csproj" \
  -c "$CONFIGURATION" -r win-x64 --self-contained true -o "$UI_OUT" \
  -p:EnableWindowsTargeting=true \
  -p:Version="$VERSION"

echo "==> Embedding Start Menu / exe icon (Linux cross-publish does not apply ApplicationIcon)"
chmod +x "$ROOT/scripts/embed-win-icon.sh"
if ! "$ROOT/scripts/embed-win-icon.sh" "$UI_OUT/DnsBlocker.UI.exe" "$ICON_SRC"; then
  echo "WARNING: icon embed skipped (install wine for PE icon embedding). MSI ProductIcon.ico still used for Start Menu/ARP." >&2
fi
# Ship the .ico beside the UI so shortcuts/tools can resolve a real file path if needed.
cp -a "$ICON_SRC" "$UI_OUT/DnsBlocker.ico"

# Stage payload (exclude PDBs to keep MSI smaller)
mkdir -p "$STAGE/DnsBlocker/UI"
find "$SERVICE_OUT" -maxdepth 1 -type f ! -name '*.pdb' -exec cp -a {} "$STAGE/DnsBlocker/" \;
# copy any service subdirs except pdb-only noise
find "$SERVICE_OUT" -mindepth 1 -maxdepth 1 -type d -exec cp -a {} "$STAGE/DnsBlocker/" \;
find "$STAGE/DnsBlocker" -name '*.pdb' -delete
find "$UI_OUT" -maxdepth 1 -type f ! -name '*.pdb' -exec cp -a {} "$STAGE/DnsBlocker/UI/" \;
find "$UI_OUT" -mindepth 1 -maxdepth 1 -type d -exec cp -a {} "$STAGE/DnsBlocker/UI/" \;
find "$STAGE/DnsBlocker/UI" -name '*.pdb' -delete
# Post-install tools (REQUIRED — v1.0.3 logo MSI omitted these; never skip)
for tool in doctor.ps1 Verify-Install.cmd Repair-DnsBlocker.ps1 Install-DnsBlocker.ps1; do
  if [[ ! -f "$ROOT/scripts/$tool" ]]; then
    echo "FAIL: missing required tool scripts/$tool" >&2
    exit 1
  fi
  cp -a "$ROOT/scripts/$tool" "$STAGE/DnsBlocker/$tool"
done
if [[ ! -f "$STAGE/DnsBlocker/doctor.ps1" ]] || [[ ! -f "$STAGE/DnsBlocker/Verify-Install.cmd" ]]; then
  echo "FAIL: tools not staged under $STAGE/DnsBlocker" >&2
  exit 1
fi
echo "Staged tools: doctor.ps1 + Verify-Install.cmd + Repair-DnsBlocker.ps1 + Install-DnsBlocker.ps1"
# ensure logs placeholder
: > "$STAGE/ProgramData/DnsBlocker/logs/.keep"

python3 - "$STAGE" "$WXS" "$VERSION" "$ICON_SRC" <<'PY'
import sys, uuid, hashlib
from collections import defaultdict
from pathlib import Path

stage, wxs_path, version = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
icon_src = Path(sys.argv[4]) if len(sys.argv) > 4 else None
if icon_src is None or not icon_src.is_file():
    raise SystemExit(f"App icon not found: {icon_src}")
icon_src = icon_src.resolve()

UPGRADE_CODE = "8F3A2C1D-9B4E-4F6A-A7C8-1D2E3F4A5B6C"
PRODUCT_CODE = str(uuid.uuid5(uuid.NAMESPACE_URL, f"DnsBlocker/{version}"))

def gid(name: str) -> str:
    return str(uuid.uuid5(uuid.NAMESPACE_URL, "DnsBlocker/" + name)).upper()

def xml_escape(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
             .replace('"', "&quot;"))

root_files = stage / "DnsBlocker"
all_files = sorted(p for p in root_files.rglob("*") if p.is_file())
dir_ids = {Path("."): "INSTALLDIR", Path("UI"): "UIDIR"}
for p in all_files:
    parent = p.relative_to(root_files).parent
    while parent != Path(".") and parent not in dir_ids:
        dir_ids[parent] = "d" + hashlib.sha1(parent.as_posix().encode()).hexdigest()[:12]
        parent = parent.parent

by_dir: dict[str, list[str]] = defaultdict(list)
component_ids: list[str] = []
service_exe_cid = service_exe_fid = None
ui_exe_fid = None

for p in all_files:
    rel = p.relative_to(root_files)
    did = dir_ids[rel.parent]
    cid = "c" + hashlib.sha1(rel.as_posix().encode()).hexdigest()[:16]
    fid = "f" + hashlib.sha1(rel.as_posix().encode()).hexdigest()[:16]
    component_ids.append(cid)
    body = f'''      <Component Id="{cid}" Guid="{gid(rel.as_posix())}">
        <File Id="{fid}" Source="{xml_escape(str(p))}" KeyPath="yes" />
'''
    if rel.as_posix().lower() == "dnsblocker.service.exe":
        service_exe_cid, service_exe_fid = cid, fid
        body += '''        <ServiceInstall Id="DnsBlockerServiceInstall" Name="DnsBlocker"
          DisplayName="WinDNS Blocker Hub"
          Description="WinDNS Blocker hub service"
          Account="LocalSystem" Start="auto" Type="ownProcess" ErrorControl="normal" Vital="yes" />
        <ServiceControl Id="DnsBlockerServiceControl" Name="DnsBlocker"
          Start="install" Stop="both" Remove="uninstall" Wait="yes" />
'''
    if rel.as_posix().lower() == "ui/dnsblocker.ui.exe":
        ui_exe_fid = fid
    body += "      </Component>"
    by_dir[did].append(body)

if not service_exe_cid:
    raise SystemExit("DnsBlocker.Service.exe not found in publish output")
if not ui_exe_fid:
    raise SystemExit("UI/DnsBlocker.UI.exe not found in publish output (needed for /setup launch)")

logs_cid = "cLogsFolder"
component_ids.append(logs_cid)
by_dir["LOGSDIR"].append(f'''      <Component Id="{logs_cid}" Guid="{gid("ProgramData/logs")}">
        <CreateFolder />
        <RegistryValue Root="HKLM" Key="Software\\DnsBlocker" Name="LogsFolder" Type="string" Value="[LOGSDIR]" KeyPath="yes" />
      </Component>''')

shortcut_cid = "cShortcut"
component_ids.append(shortcut_cid)
by_dir["ApplicationProgramsFolder"].append(f'''      <Component Id="{shortcut_cid}" Guid="{gid("shortcut")}">
        <Shortcut Id="UIStartMenu" Name="WinDNS Blocker"
          Description="WinDNS Blocker administration UI"
          Target="[UIDIR]DnsBlocker.UI.exe"
          WorkingDirectory="UIDIR"
          Icon="ProductIcon.ico"
          IconIndex="0" />
        <Shortcut Id="SetupStartMenu" Name="Assistant d'installation WinDNS Blocker"
          Description="Diagnostic et réparation (sans PowerShell)"
          Target="[UIDIR]DnsBlocker.UI.exe"
          Arguments="/setup"
          WorkingDirectory="UIDIR"
          Icon="ProductIcon.ico"
          IconIndex="0" />
        <RemoveFolder Id="RemoveProgramMenu" On="uninstall" />
        <RegistryValue Root="HKLM" Key="Software\\DnsBlocker" Name="Installed" Type="integer" Value="1" KeyPath="yes" />
      </Component>''')

# Post-install: launch Setup GUI (doctor/repair + firstrun handoff). Skip silent installs (UILevel<=2).
launch_setup_xml = f'''
    <CustomAction Id="LaunchSetupAssistant" FileKey="{ui_exe_fid}" ExeCommand="/setup" Return="asyncNoWait" Impersonate="yes" />
    <InstallExecuteSequence>
      <Custom Action="LaunchSetupAssistant" After="InstallFinalize">NOT REMOVE="ALL" AND UILevel &gt; 2</Custom>
    </InstallExecuteSequence>
    <!-- Write firstrun.pending via a tiny Type 51 + deferred is hard in wixl; Setup.exe marks the flag itself. -->
'''

children: dict[Path, list[Path]] = defaultdict(list)
for d in dir_ids:
    if d != Path("."):
        children[d.parent].append(d)

def render_dirs(d: Path, indent: int) -> str:
    pad = " " * indent
    chunks = []
    for child in sorted(children.get(d, []), key=lambda x: x.name.lower()):
        chunks.append(f'{pad}<Directory Id="{dir_ids[child]}" Name="{xml_escape(child.name)}">')
        nested = render_dirs(child, indent + 2)
        if nested:
            chunks.append(nested)
        chunks.append(f"{pad}</Directory>")
    return "\n".join(chunks)

dir_refs = []
for did, blocks in sorted(by_dir.items()):
    dir_refs.append(f'    <DirectoryRef Id="{did}">')
    dir_refs.extend(blocks)
    dir_refs.append("    </DirectoryRef>")

feature_refs = "\n".join(f'        <ComponentRef Id="{cid}" />' for cid in component_ids)
wxs = f'''<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Product Id="{PRODUCT_CODE}" Name="WinDNS Blocker" Language="1033"
           Version="{version}" Manufacturer="WinDNS Blocker"
           UpgradeCode="{UPGRADE_CODE}">
    <Package InstallerVersion="500" Compressed="yes" InstallScope="perMachine"
             Description="WinDNS Blocker hub (service + UI)" />
    <MajorUpgrade DowngradeErrorMessage="A newer version of [ProductName] is already installed."
                  AllowSameVersionUpgrades="yes" Schedule="afterInstallInitialize" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />
    <Icon Id="ProductIcon.ico" SourceFile="{xml_escape(str(icon_src))}" />
    <Property Id="ARPPRODUCTICON" Value="ProductIcon.ico" />
    <Feature Id="ProductFeature" Title="WinDNS Blocker" Level="1">
{feature_refs}
    </Feature>
{launch_setup_xml}
    <Directory Id="TARGETDIR" Name="SourceDir">
      <!-- Explicit Name avoids wixl emitting DefaultDir='.' which can break Program Files resolution -->
      <Directory Id="ProgramFiles64Folder" Name="ProgramFiles64">
        <Directory Id="INSTALLDIR" Name="DnsBlocker">
{render_dirs(Path("."), 10)}
        </Directory>
      </Directory>
      <Directory Id="CommonAppDataFolder" Name="CommonAppData">
        <Directory Id="APPDATAROOT" Name="DnsBlocker">
          <Directory Id="LOGSDIR" Name="logs" />
        </Directory>
      </Directory>
      <Directory Id="ProgramMenuFolder" Name="ProgramMenu">
        <Directory Id="ApplicationProgramsFolder" Name="WinDNS Blocker" />
      </Directory>
    </Directory>
  </Product>
  <Fragment>
{chr(10).join(dir_refs)}
  </Fragment>
</Wix>
'''
wxs_path.write_text(wxs, encoding="utf-8")
print(f"Generated {wxs_path} with {len(component_ids)} components across {len(by_dir)} directories")
PY

echo "==> Compiling MSI with wixl"
# wixl: -a x64 for 64-bit
wixl -v -a x64 -o "$MSI" "$WXS"

# Verify Icon table + UI exe icon landed in the package
echo "==> Verifying MSI icon wiring"
if command -v msiinfo >/dev/null 2>&1; then
  if ! msiinfo export "$MSI" Icon 2>/dev/null | head -5 | grep -qi 'ProductIcon'; then
    echo "WARNING: Icon table may be missing ProductIcon.ico" >&2
    msiinfo tables "$MSI" 2>/dev/null | head -40 || true
  else
    echo "Icon table contains ProductIcon.ico"
  fi
fi
TMP_EXTRACT=$(mktemp -d)
trap 'rm -rf "$TMP_EXTRACT"' EXIT
if msiextract -C "$TMP_EXTRACT" "$MSI" >/dev/null 2>&1; then
  DOCTOR=$(find "$TMP_EXTRACT" -iname 'doctor.ps1' | head -1 || true)
  VERIFY=$(find "$TMP_EXTRACT" -iname 'Verify-Install.cmd' | head -1 || true)
  SVC_EXE=$(find "$TMP_EXTRACT" -path '*/DnsBlocker/DnsBlocker.Service.exe' | head -1 || true)
  UI_EXE=$(find "$TMP_EXTRACT" -path '*/DnsBlocker/UI/DnsBlocker.UI.exe' | head -1 || true)
  HOSTFXR=$(find "$TMP_EXTRACT" -path '*/DnsBlocker/hostfxr.dll' | head -1 || true)
  if [[ -z "$DOCTOR" ]]; then
    echo "FAIL: MSI is missing doctor.ps1 (would install without C:\\Program Files\\DnsBlocker\\doctor.ps1)" >&2
    exit 1
  fi
  if [[ -z "$VERIFY" ]]; then
    echo "FAIL: MSI is missing Verify-Install.cmd" >&2
    exit 1
  fi
  if [[ -z "$SVC_EXE" ]]; then
    echo "FAIL: MSI is missing DnsBlocker/DnsBlocker.Service.exe (sparse-install risk)" >&2
    exit 1
  fi
  if [[ -z "$UI_EXE" ]]; then
    echo "FAIL: MSI is missing DnsBlocker/UI/DnsBlocker.UI.exe" >&2
    exit 1
  fi
  if [[ -z "$HOSTFXR" ]]; then
    echo "FAIL: MSI is missing DnsBlocker/hostfxr.dll (self-contained runtime)" >&2
    exit 1
  fi
  # Directory table must not use DefaultDir='.' for ProgramFiles64Folder (breaks PF resolution)
  if command -v msiinfo >/dev/null 2>&1; then
    DIR_ROW=$(msiinfo export "$MSI" Directory 2>/dev/null | awk -F'\t' '$1=="ProgramFiles64Folder"{print $0}')
    if echo "$DIR_ROW" | grep -Eq $'\t\\.\t|\t\\.$'; then
      echo "FAIL: ProgramFiles64Folder DefaultDir is '.' — sparse Program Files installs" >&2
      echo "$DIR_ROW" >&2
      exit 1
    fi
    echo "OK: Directory ProgramFiles64Folder -> $DIR_ROW"
  fi
  echo "OK: MSI contains $DOCTOR"
  echo "OK: MSI contains $VERIFY"
  echo "OK: MSI contains $SVC_EXE"
  echo "OK: MSI contains $UI_EXE"
  echo "OK: MSI contains $HOSTFXR"

  if [[ -n "$UI_EXE" ]] && command -v wrestool >/dev/null 2>&1; then
    wrestool -l "$UI_EXE" | grep -Eq 'group_icon|--type=14' \
      && echo "OK: extracted UI exe has group_icon" \
      || { echo "FAIL: extracted UI exe missing group_icon"; exit 1; }
  fi
  find "$TMP_EXTRACT" -iname 'DnsBlocker.ico' | head -3 || true
else
  echo "FAIL: msiextract could not open $MSI" >&2
  exit 1
fi

SHA=$(sha256sum "$MSI" | awk '{print $1}')
PUBLISHED_AT=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
MSI_URL="${UPDATE_FEED_BASE%/}/DnsBlocker-$VERSION.msi"
cat > "$ARTIFACTS/version.json" <<EOF
{
  "version": "$VERSION",
  "msiUrl": "$MSI_URL",
  "sha256": "$SHA",
  "releaseNotes": "GUI Setup assistant (/setup): doctor+repair without PowerShell CLI; first-run handoff to UI; MSI launches assistant after install.",
  "publishedAt": "$PUBLISHED_AT"
}
EOF

echo "==> Done"
ls -lh "$MSI"
echo "SHA256: $SHA"
echo "Feed:   $ARTIFACTS/version.json"
