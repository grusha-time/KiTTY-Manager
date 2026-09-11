#!/usr/bin/env bash
set -euo pipefail

# Usage: ./scripts/create-worktree.sh <branch-or-issue-name> [base-branch]
# Example: ./scripts/create-worktree.sh issue-12
# Example: ./scripts/create-worktree.sh issue-12 codex/firefox-containers

if [ $# -lt 1 ]; then
  echo "Usage: $0 <branch-or-issue-name> [base-branch]"
  echo "Example: $0 issue-12"
  exit 1
fi

BRANCH="$1"
BASE="${2:-HEAD}"

# Resolve main repository root
MAIN_REPO="$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
if [ -z "$MAIN_REPO" ]; then
  MAIN_REPO="$(cd "$(dirname "$0")/.." && pwd)"
else
  MAIN_REPO="$(cd "$MAIN_REPO/.." && pwd)"
fi

TARGET_DIR="$(cd "$MAIN_REPO/.." && pwd)/test4-worktree/$BRANCH"

if [ -d "$TARGET_DIR" ]; then
  echo "Error: Directory $TARGET_DIR already exists."
  exit 1
fi

mkdir -p "$(dirname "$TARGET_DIR")"

echo "==> Creating worktree at: $TARGET_DIR (branch: $BRANCH from $BASE)"
if git show-ref --verify --quiet "refs/heads/$BRANCH"; then
  git -C "$MAIN_REPO" worktree add "$TARGET_DIR" "$BRANCH"
else
  git -C "$MAIN_REPO" worktree add -b "$BRANCH" "$TARGET_DIR" "$BASE"
fi

echo "==> Linking runtime dependencies (symlinks to save space)..."
mkdir -p "$TARGET_DIR/vendor"

for item in .dotnet .dotnet_home .dotnet-home .nuget .tools downloads; do
  if [ -e "$MAIN_REPO/$item" ]; then
    ln -sf "$MAIN_REPO/$item" "$TARGET_DIR/$item"
    echo "  Linked: $item"
  fi
done

if [ -e "$MAIN_REPO/vendor/ansible-runtime" ]; then
  ln -sf "$MAIN_REPO/vendor/ansible-runtime" "$TARGET_DIR/vendor/ansible-runtime"
  echo "  Linked: vendor/ansible-runtime"
fi

if [ -e "$MAIN_REPO/vendor/KITTY-LICENCE.TXT" ]; then
  ln -sf "$MAIN_REPO/vendor/KITTY-LICENCE.TXT" "$TARGET_DIR/vendor/KITTY-LICENCE.TXT"
  echo "  Linked: vendor/KITTY-LICENCE.TXT"
fi

echo ""
echo " Worktree created successfully!"
echo " Directory: $TARGET_DIR"
echo " You can now open a new chat/terminal in $TARGET_DIR"
echo " To build ZIP in that worktree: cd $TARGET_DIR && ./packaging/build-package.sh [custom-name.zip]"
