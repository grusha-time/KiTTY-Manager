#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
PROJECT_NAME="$(basename -- "$PROJECT_DIR")"
DOTNET_VERSION="8.0"

usage() {
  cat <<USAGE
Usage:
  $0 pack [archive-path]
  $0 install
  $0 check

Commands:
  pack     Create a compact source archive without build outputs or dependencies.
  install  Install a local .NET 8 SDK and restore NuGet packages inside the project.
  check    Build the app and run the self-tests.
USAGE
}

dotnet_path() {
  if [ -x "$PROJECT_DIR/.dotnet/dotnet" ]; then
    printf '%s\n' "$PROJECT_DIR/.dotnet/dotnet"
  elif command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
  else
    return 1
  fi
}

export DOTNET_CLI_HOME="$PROJECT_DIR/.dotnet_home"
export NUGET_PACKAGES="$PROJECT_DIR/.nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

case "${1:-}" in
  pack)
    archive="${2:-$PROJECT_DIR/backups/kitty-manager-source-$(date -u +%Y%m%d-%H%M%S).tar.gz}"
    mkdir -p -- "$(dirname -- "$archive")"
    tar -C "$PROJECT_DIR" \
      --exclude='src/*/bin' \
      --exclude='src/*/obj' \
      --exclude='.dotnet' \
      --exclude='.dotnet_home' \
      --exclude='.nuget' \
      --exclude='.tools' \
      --exclude='build' \
      --exclude='dist' \
      --exclude='downloads' \
      --exclude='Data' \
      --exclude='TestResults' \
      -czf "$archive" src packaging scripts README.md install-instruction.md vendor/KITTY-LICENCE.TXT
    ls -lh -- "$archive"
    ;;
  install)
    if ! dotnet="$(dotnet_path)"; then
      mkdir -p "$PROJECT_DIR/.dotnet" "$PROJECT_DIR/.tools"
      installer="$PROJECT_DIR/.tools/dotnet-install.sh"
      if [ ! -f "$installer" ]; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
      fi
      bash "$installer" --channel "$DOTNET_VERSION" --install-dir "$PROJECT_DIR/.dotnet"
      dotnet="$PROJECT_DIR/.dotnet/dotnet"
    fi
    "$dotnet" restore "$PROJECT_DIR/src/KiTTYManager.App/KiTTYManager.App.csproj"
    "$dotnet" restore "$PROJECT_DIR/src/KiTTYManager.SelfTest/KiTTYManager.SelfTest.csproj"
    ;;
  check)
    dotnet="$(dotnet_path)" || { echo 'Run project-source.sh install first.' >&2; exit 1; }
    "$dotnet" build "$PROJECT_DIR/src/KiTTYManager.App/KiTTYManager.App.csproj" -c Release
    "$dotnet" run --project "$PROJECT_DIR/src/KiTTYManager.SelfTest/KiTTYManager.SelfTest.csproj" -c Release
    ;;
  -h|--help|help|'') usage ;;
  *) usage >&2; exit 2 ;;
esac
