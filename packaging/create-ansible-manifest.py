#!/usr/bin/env python3
import hashlib, json, pathlib, sys

root = pathlib.Path(sys.argv[1]).resolve()
license_for = {
    "qemu": "licenses/QEMU.txt", "linux-image": "licenses/Linux.txt",
    "python": "licenses/Python.txt", "ansible-core": "licenses/Ansible.txt"
}
components = []
for path in sorted(item for item in root.rglob("*") if item.is_file() and item.name != "manifest.json"):
    relative = path.relative_to(root).as_posix()
    if relative.startswith("licenses/"): continue
    if relative == "metadata/python.version": role = "python"
    elif relative == "metadata/ansible-core.version": role = "ansible-core"
    elif relative == "metadata/puttygen.version": role = "linux-image"
    elif relative.startswith("qemu/"): role = "qemu"
    else: role = "linux-image"
    component_license = "licenses/PuTTY.html" if relative == "metadata/puttygen.version" else license_for[role]
    components.append({"name": relative, "role": role, "relativePath": relative,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "licenseRelativePath": component_license})
manifest = {"version": 1, "ansibleCoreVersion": "2.16.1", "components": components}
(root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
