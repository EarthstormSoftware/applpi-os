#!/bin/bash
# Builds the image inside the build container (started by build.ps1). The image and logs end up in /pi-gen/deploy.
set -euo pipefail
cd /pi-gen
# shellcheck source=config
source ./config

# Run armhf programs: use the host's emulation if it works in a chroot, otherwise register the container's qemu-arm
# for the length of the build (as pi-gen's build-docker.sh does)
mount -t binfmt_misc binfmt_misc /proc/sys/fs/binfmt_misc 2>/dev/null || true
if ! arch-test -c "$(mktemp -d)" armhf; then
	[ ! -e /proc/sys/fs/binfmt_misc/qemu-arm-rpi ] || echo -1 > /proc/sys/fs/binfmt_misc/qemu-arm-rpi
	echo ':qemu-arm-rpi:M::\x7fELF\x01\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\x28\x00:\xff\xff\xff\xff\xff\xff\xff\x00\xff\xff\xff\xff\xff\xff\xff\xff\xfe\xff\xff\xff:/usr/bin/qemu-arm:F' \
		> /proc/sys/fs/binfmt_misc/register
	trap 'echo -1 > /proc/sys/fs/binfmt_misc/qemu-arm-rpi' EXIT
fi

# Our partition sizes instead of pi-gen's: a smaller boot partition, and root A the fixed slot size instead of
# "contents plus 20% plus 200 MB". The root is read-only, so it only needs room for its contents and the file
# system's overhead (10% here).
prerun=export-image/prerun.sh
for setting in BOOT_SIZE ROOT_PART_SIZE; do
	if ! grep -q "^${setting}=" "$prerun"; then
		echo "pi-gen's $prerun has changed: ${setting} not found" >&2
		exit 1
	fi
done
sed -i "s|^BOOT_SIZE=.*|BOOT_SIZE=$((BOOT_MIB * 1024 * 1024))|" "$prerun"
sed -i "s|^ROOT_PART_SIZE=.*|ROOT_PART_SIZE=$((ROOT_SLOT_MIB * 1024 * 1024))\n\
if [ \$((ROOT_SIZE * 11 / 10)) -gt \$ROOT_PART_SIZE ]; then\n\
\techo \"The root filesystem (\$ROOT_SIZE bytes) doesn't fit its ${ROOT_SLOT_MIB} MiB slot\" >\&2\n\
\texit 1\n\
fi|" "$prerun"

# The OS's boot files go into slot A once the boot partition is complete: after the last update-initramfs, before
# the image is unmounted
finalise=export-image/05-finalise/01-run.sh
anchor='rm -f "${ROOTFS_DIR}/usr/bin/qemu-arm-static"'
line=$(grep -nxF "$anchor" "$finalise" | cut -d: -f1)
if [ -z "$line" ] || [ "$(grep -n 'update-initramfs' "$finalise" | head -1 | cut -d: -f1)" -gt "$line" ]; then
	echo "pi-gen's $finalise has changed: can't place the slot files" >&2
	exit 1
fi
sed -i "${line}i source /pi-gen/export-slots.sh" "$finalise"

# Quick builds reuse stages 0 and 1 (the base system, most of the build's time) from the last build, kept in the
# applpi-os-work volume: only our stage and the export run again. A full build starts from nothing.
work="work/${IMG_NAME}"
if [ "${APPLPI_QUICK:-0}" = 1 ] && [ -d "$work/stage1/rootfs" ]; then
	echo "Quick build: reusing stages 0 and 1 from the last build"
	touch stage0/SKIP stage1/SKIP
else
	rm -rf work/*
fi
rm -rf "$work/stage2-applpi" "$work/export-image" "$work/build.log"

./build.sh
cp "$work/build.log" deploy/
