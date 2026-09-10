#!/usr/bin/env python3
"""Validate the embedded Ansible runtime before it enters a release package."""

from __future__ import annotations

import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import sys


REQUIRED_ROLES = {"qemu", "linux-image", "python", "ansible-core"}
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")
ANSIBLE_216 = re.compile(r"^2\.16\.\d+$")


def fail(message: str) -> None:
    print(f"Ansible runtime invalid: {message}", file=sys.stderr)
    raise SystemExit(1)


def safe_file(root: pathlib.Path, relative: object, label: str) -> pathlib.Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        fail(f"{label} must be a non-empty portable relative path")
    pure = pathlib.PurePosixPath(relative)
    if pure.is_absolute() or ".." in pure.parts:
        fail(f"{label} escapes the runtime directory: {relative}")
    path = root.joinpath(*pure.parts)
    try:
        path.resolve().relative_to(root)
    except ValueError:
        fail(f"{label} resolves outside the runtime directory: {relative}")
    if not path.is_file() or path.is_symlink():
        fail(f"missing regular file for {label}: {relative}")
    return path


def verify_guest_agent(image: pathlib.Path) -> None:
    debugfs = shutil.which("debugfs")
    if not debugfs:
        fail("debugfs is required to verify the guest agent inside the Linux image")
    result = subprocess.run(
        [debugfs, "-R", "cat /usr/local/sbin/kitty-ansible-agent", str(image)],
        text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
    )
    if result.returncode != 0:
        fail("cannot read guest agent from Linux image: " + result.stderr.strip())
    source = pathlib.Path(__file__).resolve().parent / "ansible-runtime" / "guest" / "kitty-ansible-agent.py"
    if not source.is_file():
        fail("guest agent source is missing: " + str(source))
    if result.stdout.encode("utf-8") != source.read_bytes():
        fail("guest agent in Linux image does not exactly match its source")


def main() -> None:
    if len(sys.argv) != 2:
        fail("usage: verify-ansible-runtime.py RUNTIME_DIRECTORY")
    root = pathlib.Path(sys.argv[1]).resolve()
    manifest_path = root / "manifest.json"
    if not manifest_path.is_file() or manifest_path.is_symlink():
        fail(f"missing manifest: {manifest_path}")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"cannot read manifest.json: {error}")

    if manifest.get("version") != 1:
        fail("manifest version must be 1")
    ansible_version = manifest.get("ansibleCoreVersion")
    if not isinstance(ansible_version, str) or not ANSIBLE_216.fullmatch(ansible_version):
        fail("ansibleCoreVersion must pin one exact 2.16.x version")
    components = manifest.get("components")
    if not isinstance(components, list) or not components:
        fail("components must be a non-empty array")

    roles: set[str] = set()
    paths: set[str] = set()
    for index, component in enumerate(components):
        if not isinstance(component, dict):
            fail(f"components[{index}] must be an object")
        role = component.get("role")
        if role not in REQUIRED_ROLES:
            fail(f"components[{index}].role is unknown: {role!r}")
        roles.add(role)
        path_value = component.get("relativePath")
        if path_value in paths:
            fail(f"duplicate component path: {path_value}")
        paths.add(path_value)
        path = safe_file(root, path_value, f"components[{index}].relativePath")
        safe_file(root, component.get("licenseRelativePath"),
                  f"components[{index}].licenseRelativePath")
        expected = component.get("sha256")
        if not isinstance(expected, str) or not SHA256.fullmatch(expected):
            fail(f"components[{index}].sha256 must contain 64 hexadecimal characters")
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual.lower() != expected.lower():
            fail(f"SHA-256 mismatch for {path_value}: expected {expected}, got {actual}")

    missing = REQUIRED_ROLES - roles
    if missing:
        fail("missing required component roles: " + ", ".join(sorted(missing)))
    tracked = paths | {item.get("licenseRelativePath") for item in components}
    tracked.add("manifest.json")
    symlinks = [path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_symlink()]
    if symlinks:
        fail("symbolic links are not allowed: " + ", ".join(sorted(symlinks)))
    actual_files = {
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() and not path.is_symlink()
    }
    untracked = actual_files - tracked
    if untracked:
        fail("files absent from manifest: " + ", ".join(sorted(untracked)))
    verify_guest_agent(root / "images" / "ansible-base.img")
    print(f"Ansible runtime verified: ansible-core {ansible_version}, {len(components)} components")


if __name__ == "__main__":
    main()
