#!/usr/bin/env python3
import pathlib, re, shutil, subprocess, sys

source, target = map(lambda value: pathlib.Path(value).resolve(), sys.argv[1:3])
target.mkdir(parents=True, exist_ok=True)
pending = ["qemu-system-x86_64.exe", "qemu-img.exe"]
copied = set()
while pending:
    name = pending.pop()
    if name.lower() in copied: continue
    path = source / name
    if not path.is_file(): raise SystemExit(f"missing QEMU dependency: {name}")
    shutil.copy2(path, target / path.name); copied.add(name.lower())
    output = subprocess.run(["objdump", "-p", str(path)], check=True, text=True,
                            stdout=subprocess.PIPE).stdout
    for dependency in re.findall(r"DLL Name:\s*([^\r\n]+)", output, re.I):
        candidate = source / dependency.strip()
        if candidate.is_file() and dependency.strip().lower() not in copied: pending.append(dependency.strip())

share = target / "share"; share.mkdir(exist_ok=True)
for name in ("bios.bin", "bios-256k.bin", "linuxboot_dma.bin", "kvmvapic.bin", "pxe-virtio.rom"):
    shutil.copy2(source / "share" / name, share / name)
print(f"QEMU runtime files: {len(copied) + 5}")
