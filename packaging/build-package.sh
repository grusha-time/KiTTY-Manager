#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$ROOT/.dotnet/dotnet"
ANSIBLE_RUNTIME="$ROOT/vendor/ansible-runtime"
export DOTNET_CLI_HOME="$ROOT/.dotnet_home"
export NUGET_PACKAGES="$ROOT/.nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

python3 "$ROOT/packaging/verify-ansible-runtime.py" "$ANSIBLE_RUNTIME"

rm -rf "$ROOT/build/package" "$ROOT/dist"
mkdir -p "$ROOT/build/package/KiTTY" "$ROOT/build/package/Runtime" "$ROOT/dist"

"$DOTNET" publish "$ROOT/src/KiTTYManager.App/KiTTYManager.App.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$ROOT/build/app"

cp "$ROOT/build/app/KiTTYManager.exe" "$ROOT/build/package/"
cp "$ROOT/downloads/kitty_portable-0.76.1.13.exe" "$ROOT/build/package/KiTTY/kitty.exe"
cp "$ROOT/packaging/kitty.ini" "$ROOT/build/package/KiTTY/kitty.ini"
cp "$ROOT/vendor/KITTY-LICENCE.TXT" "$ROOT/build/package/KiTTY/LICENCE.TXT"
cp -a "$ANSIBLE_RUNTIME" "$ROOT/build/package/Runtime/Ansible"



python3 "$ROOT/packaging/verify-ansible-runtime.py" "$ROOT/build/package/Runtime/Ansible"

jar --create --no-manifest --file "$ROOT/dist/KiTTYManager-2.0.0-windows-x64.zip" -C "$ROOT/build/package" .
