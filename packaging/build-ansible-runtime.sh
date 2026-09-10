#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$ROOT/build/ansible-runtime"
DOWNLOADS="$ROOT/downloads/ansible-runtime"
TOOLS="$ROOT/.tools/ansible-runtime"
OUTPUT="$WORK/output"
QEMU_DATE=20260811
APK_VERSION=2.14.4-r0
SEVEN_Z_VERSION=2301
SEVEN_Z_SHA256=23babcab045b78016e443f862363e4ab63c77d75bc715c0b3463f6134cbcf318
APK_SHA256=14cc4c09fb53a34943e9aca77eacc0078f5ed74e2e09db02abd2a24bb5fd4cc8
PUTTY_VERSION=0.81-r0
PUTTY_SHA256=3a6f15b8cf2d0379cf6d46e81e4e4dd1bb43d1f039c9eb621a8366bc4f29029f
PUTTY_LICENSE_SHA256=643a9142655b672ef53f729dded917cff6e0f9dfa22d32629ff5ec137e15e3c5

mkdir -p "$DOWNLOADS" "$TOOLS" "$WORK"
exec 9>"$WORK/.build.lock"
flock -n 9 || { echo "Ansible runtime build is already running." >&2; exit 1; }
cleanup_rootfs_mounts() {
  if mountpoint -q "$WORK/rootfs/proc"; then umount "$WORK/rootfs/proc"; fi
}
trap cleanup_rootfs_mounts EXIT
cleanup_rootfs_mounts
download() { local url="$1" target="$2"; [[ -f "$target" ]] || curl -fL --retry 3 -o "$target" "$url"; }
download "https://www.7-zip.org/a/7z${SEVEN_Z_VERSION}-linux-x64.tar.xz" "$DOWNLOADS/7z.tar.xz"
echo "$SEVEN_Z_SHA256  $DOWNLOADS/7z.tar.xz" | sha256sum -c -
rm -rf "$TOOLS/7z"; mkdir -p "$TOOLS/7z"; tar -xJf "$DOWNLOADS/7z.tar.xz" -C "$TOOLS/7z"
download "https://qemu.weilnetz.de/w64/qemu-w64-setup-${QEMU_DATE}.exe" "$DOWNLOADS/qemu.exe"
download "https://qemu.weilnetz.de/w64/qemu-w64-setup-${QEMU_DATE}.sha512" "$DOWNLOADS/qemu.sha512"
(cd "$DOWNLOADS"; sed "s/qemu-w64-setup-${QEMU_DATE}.exe/qemu.exe/" qemu.sha512 | sha512sum -c -)
rm -rf "$WORK/qemu-extract"; mkdir -p "$WORK/qemu-extract"
"$TOOLS/7z/7zz" x -y "-o$WORK/qemu-extract" "$DOWNLOADS/qemu.exe" >/dev/null

download "https://dl-cdn.alpinelinux.org/alpine/v3.19/main/x86_64/apk-tools-static-${APK_VERSION}.apk" "$DOWNLOADS/apk-tools-static.apk"
echo "$APK_SHA256  $DOWNLOADS/apk-tools-static.apk" | sha256sum -c -
rm -rf "$TOOLS/apk" "$WORK/rootfs" "$OUTPUT"; mkdir -p "$TOOLS/apk" "$WORK/rootfs/etc/apk" "$OUTPUT"
tar -xzf "$DOWNLOADS/apk-tools-static.apk" -C "$TOOLS/apk"
printf '%s\n' 'https://dl-cdn.alpinelinux.org/alpine/v3.19/main' 'https://dl-cdn.alpinelinux.org/alpine/v3.19/community' > "$WORK/rootfs/etc/apk/repositories"
"$TOOLS/apk/sbin/apk.static" --arch x86_64 --root "$WORK/rootfs" --initdb --no-cache \
  --repositories-file "$WORK/rootfs/etc/apk/repositories" --allow-untrusted add \
  alpine-base linux-virt python3 ansible-core=2.16.1-r0 openssh-client openrc

# KiTTY imports may reference PuTTY PPK keys. Only the console converter is
# needed; installing the complete package would pull GTK and other GUI files.
download "https://dl-cdn.alpinelinux.org/alpine/v3.19/main/x86_64/putty-${PUTTY_VERSION}.apk" "$DOWNLOADS/putty.apk"
echo "$PUTTY_SHA256  $DOWNLOADS/putty.apk" | sha256sum -c -
tar -xzf "$DOWNLOADS/putty.apk" -C "$WORK/rootfs" usr/bin/puttygen

install -Dm755 "$ROOT/packaging/ansible-runtime/guest/kitty-ansible-agent.py" "$WORK/rootfs/usr/local/sbin/kitty-ansible-agent"
python3 "$ROOT/packaging/ansible-runtime/guest/kitty-ansible-agent.py" --self-test
install -Dm644 "$ROOT/packaging/ansible-runtime/guest/kitty_manager_callback.py" \
  "$WORK/rootfs/usr/local/share/kitty-ansible/callback_plugins/kitty_manager.py"
mkdir -p "$WORK/rootfs/etc/init.d" "$WORK/rootfs/etc/runlevels/default" "$WORK/rootfs/etc/network"
printf '%s\n' '#!/sbin/openrc-run' 'command=/usr/local/sbin/kitty-ansible-agent' 'command_background=true' 'pidfile=/run/kitty-ansible-agent.pid' > "$WORK/rootfs/etc/init.d/kitty-ansible"
chmod 755 "$WORK/rootfs/etc/init.d/kitty-ansible"
ln -s /etc/init.d/kitty-ansible "$WORK/rootfs/etc/runlevels/default/kitty-ansible"
ln -s /etc/init.d/networking "$WORK/rootfs/etc/runlevels/default/networking"
printf '%s\n' 'auto lo' 'iface lo inet loopback' 'auto eth0' 'iface eth0 inet dhcp' > "$WORK/rootfs/etc/network/interfaces"
printf '%s\n' 'ansible-vm' > "$WORK/rootfs/etc/hostname"

mkdir -p "$OUTPUT/qemu" "$OUTPUT/images" "$OUTPUT/linux" "$OUTPUT/licenses" "$OUTPUT/metadata"
python3 "$ROOT/packaging/collect-qemu-runtime.py" "$WORK/qemu-extract" "$OUTPUT/qemu"
cp "$WORK/rootfs/boot/vmlinuz-virt" "$OUTPUT/linux/"
cp "$WORK/rootfs/boot/initramfs-virt" "$OUTPUT/linux/"
truncate -s 384M "$OUTPUT/images/ansible-base.img"
mkfs.ext4 -q -F -L ansible-root -d "$WORK/rootfs" "$OUTPUT/images/ansible-base.img"
cp "$WORK/qemu-extract/COPYING" "$OUTPUT/licenses/QEMU.txt"
download "https://raw.githubusercontent.com/torvalds/linux/v6.6/COPYING" "$DOWNLOADS/Linux-COPYING"
download "https://raw.githubusercontent.com/python/cpython/v3.11.14/LICENSE" "$DOWNLOADS/Python-LICENSE"
download "https://raw.githubusercontent.com/ansible/ansible/v2.16.1/COPYING" "$DOWNLOADS/Ansible-COPYING"
download "https://www.chiark.greenend.org.uk/~sgtatham/putty/licence.html" "$DOWNLOADS/Putty-LICENCE.html"
echo "$PUTTY_LICENSE_SHA256  $DOWNLOADS/Putty-LICENCE.html" | sha256sum -c -
cp "$DOWNLOADS/Linux-COPYING" "$OUTPUT/licenses/Linux.txt"
cp "$DOWNLOADS/Python-LICENSE" "$OUTPUT/licenses/Python.txt"
cp "$DOWNLOADS/Ansible-COPYING" "$OUTPUT/licenses/Ansible.txt"
cp "$DOWNLOADS/Putty-LICENCE.html" "$OUTPUT/licenses/PuTTY.html"
printf '%s\n' '3.11.14' > "$OUTPUT/metadata/python.version"
printf '%s\n' '2.16.1' > "$OUTPUT/metadata/ansible-core.version"
printf '%s\n' '0.81-r0' > "$OUTPUT/metadata/puttygen.version"
python3 "$ROOT/packaging/create-ansible-manifest.py" "$OUTPUT"
python3 "$ROOT/packaging/verify-ansible-runtime.py" "$OUTPUT"
echo "Prepared runtime: $OUTPUT"
