#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="${1:-}"
TARGET="$ROOT/vendor/ansible-runtime"

if [[ -z "$SOURCE" ]]; then
  echo "Usage: $0 PREPARED_RUNTIME_DIRECTORY" >&2
  echo "The source must contain real binaries, licenses and manifest.json; see packaging/ansible-runtime/README.md." >&2
  exit 2
fi

SOURCE="$(cd "$SOURCE" && pwd)"
if [[ -e "$TARGET" ]]; then
  echo "Refusing to overwrite existing runtime: $TARGET" >&2
  echo "Remove or archive that exact directory explicitly, then retry." >&2
  exit 1
fi

python3 "$ROOT/packaging/verify-ansible-runtime.py" "$SOURCE"
mkdir -p "$TARGET"
cp -a "$SOURCE/." "$TARGET/"
python3 "$ROOT/packaging/verify-ansible-runtime.py" "$TARGET"
echo "Prepared embedded runtime: $TARGET"
