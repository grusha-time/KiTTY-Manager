#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$ROOT/.dotnet/dotnet"
ANSIBLE_RUNTIME="$ROOT/vendor/ansible-runtime"
export DOTNET_CLI_HOME="$ROOT/.dotnet_home"
export NUGET_PACKAGES="$ROOT/.nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

python3 "$ROOT/packaging/verify-ansible-runtime.py" "$ANSIBLE_RUNTIME"

BRANCH_RAW="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo 'release')"
BRANCH_SAFE="$(echo "$BRANCH_RAW" | tr '/' '-')"

rm -rf "$ROOT/build/package" "$ROOT/build/app" "$ROOT/dist"
mkdir -p "$ROOT/build/package/KiTTY" "$ROOT/build/package/Runtime" "$ROOT/dist"

"$DOTNET" publish "$ROOT/src/KiTTYManager.App/KiTTYManager.App.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$ROOT/build/app"

cp "$ROOT/build/app/KiTTYManager.exe" "$ROOT/build/package/"
cp "$ROOT/downloads/kitty_portable-0.76.1.13.exe" "$ROOT/build/package/KiTTY/kitty.exe"
cp "$ROOT/packaging/kitty.ini" "$ROOT/build/package/KiTTY/kitty.ini"
cp "$ROOT/vendor/KITTY-LICENCE.TXT" "$ROOT/build/package/KiTTY/LICENCE.TXT"
cp -a "$ANSIBLE_RUNTIME" "$ROOT/build/package/Runtime/Ansible"

python3 "$ROOT/packaging/verify-ansible-runtime.py" "$ROOT/build/package/Runtime/Ansible"

ZIP_NAME="${1:-}"
if [ -z "$ZIP_NAME" ]; then
  ZIP_NAME="KiTTYManager-2.0.0-${BRANCH_SAFE}-windows-x64.zip"
fi

if [[ "$ZIP_NAME" != *.zip ]]; then
  ZIP_NAME="${ZIP_NAME}.zip"
fi

ZIP_PATH="$ROOT/dist/$ZIP_NAME"
find "$ROOT/build/package" -type f \( -name "*.md" -o -name "*sha256*" \) -delete
echo "==> Creating package archive: $ZIP_PATH"
jar --create --no-manifest --file "$ZIP_PATH" -C "$ROOT/build/package" .
echo "==> Built: $ZIP_PATH"

