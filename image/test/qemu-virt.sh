#!/bin/bash
# Test boots of an ApplPi OS image on QEMU's generic ARM machine ("virt"), run by test-qemu.ps1 in three containers
# sharing /work:
#   unpack <image.img.xz>  (amd64, privileged)  the image as a 4 GB card, and overlayroot's initramfs files
#   kernel                 (arm/v7)             Debian's generic ARM kernel and an initramfs with overlayroot
#   boot                   (amd64, privileged)  boots the card twice and checks it over SSH; results in /work/out
# QEMU's Pi 2 model can't read the SD card with the Pi kernel, so this tests everything of ours (first boot, settings,
# SSH, read-only root) but not the Pi's kernel, firmware or Wi-Fi.
set -euo pipefail

work=/work
img=$work/sd.img

# Byte offset of partition $1 in the card image
offset() {
	echo $(( $(sfdisk -d "$img" | awk -F'[=,]' -v p="${img}$1" '$1 ~ p" " {print $2}') * 512 ))
}

unpack() {
	apt-get -qq update && apt-get -qq install -y --no-install-recommends xz-utils fdisk >/dev/null
	xz -dc "$1" > "$img"
	# The size of a "4 GB" card, the smallest that fits root B and the data partition
	truncate -s 3780M "$img"
	mkdir -p /mnt/root "$work/overlayroot"
	mount -o ro,loop,offset="$(offset 2)" "$img" /mnt/root
	grep -E '^/(etc|usr)/' /mnt/root/var/lib/dpkg/info/overlayroot.list | while read -r f; do
		if [ -f "/mnt/root$f" ]; then
			install -D "/mnt/root$f" "$work/overlayroot$f"
		fi
	done
	umount /mnt/root
}

kernel() {
	# (overlayroot's initramfs hook copies cryptsetup and mke2fs in, and its script needs busybox, as on the Pi)
	apt-get -qq update && apt-get -qq install -y --no-install-recommends linux-image-armmp initramfs-tools cryptsetup-bin \
		e2fsprogs busybox >/dev/null
	cp -a "$work/overlayroot/." /
	# The virtual card and network, loaded in the initramfs; no swap to resume from
	printf '%s\n' virtio_mmio virtio_blk virtio_net >> /etc/initramfs-tools/modules
	echo RESUME=none > /etc/initramfs-tools/conf.d/resume
	version=$(basename /usr/lib/modules/*)
	mkinitramfs -o "$work/initrd.img" "$version"
	cp "/boot/vmlinuz-$version" "$work/vmlinuz"
	rm -rf "$work/modules"; mkdir -p "$work/modules"
	cp -a "/usr/lib/modules/$version" "$work/modules/"
}

boot() {
	apt-get -qq update
	apt-get -qq install -y --no-install-recommends qemu-system-arm mtools openssh-client fdisk >/dev/null
	local out=$work/out
	rm -rf "$out"; mkdir -p "$out"

	# The generic kernel's modules go into the test card's root, so the Pi image needn't carry them
	mkdir -p /mnt/root
	mount -o loop,offset="$(offset 2)" "$img" /mnt/root
	cp -a "$work/modules/." /mnt/root/usr/lib/modules/
	umount /mnt/root

	# A throwaway key and test settings (no Wi-Fi in QEMU)
	local boot_img="$img@@$(offset 1)"
	ssh-keygen -q -t ed25519 -N "" -f /tmp/key
	cat > /tmp/applpi.json <<-EOF
		{ "hostname": "applpi-qemu", "timezone": "Europe/London", "ssh_keys": ["$(cat /tmp/key.pub)"] }
	EOF
	mcopy -o -i "$boot_img" /tmp/applpi.json ::applpi.json
	mcopy -o -i "$boot_img" ::cmdline.txt /tmp/cmdline.txt
	# Emulation is slow: finding the devices can take minutes, longer than systemd's usual 90 s wait
	cmdline="$(sed 's/console=[^ ]*//g' /tmp/cmdline.txt | tr -s ' ' | tr -d '\n') console=ttyAMA0 net.ifnames=0"
	cmdline="$cmdline systemd.default_device_timeout_sec=900"
	echo "Kernel command line: $cmdline"

	# The file written to /etc on the first boot must be gone on the second (the root's changes are in RAM)
	run_boot 1 && checks 1 && ssh_pi 'sudo -n touch /etc/test-overlay; sudo -n systemctl poweroff' || true
	stop_qemu
	run_boot 2 && checks 2 && ssh_pi 'ls /etc/test-overlay; sudo -n systemctl poweroff' >> "$out/checks-2.log" 2>&1 || true
	stop_qemu
	echo "== Boot partition after" | tee -a "$out/summary.log"
	mdir -i "$boot_img" :: | grep -iE "applpi(\.json|-error)" | tee -a "$out/summary.log" || echo "no settings or error file" | tee -a "$out/summary.log"
}

run_boot() {
	echo "== Boot $1" | tee -a "$work/out/summary.log"
	qemu-system-arm -M virt,highmem=off -cpu cortex-a15 -m 1G -smp 4 \
		-kernel "$work/vmlinuz" -initrd "$work/initrd.img" -append "$cmdline" \
		-drive if=none,file="$img",format=raw,id=card -device virtio-blk-device,drive=card \
		-netdev user,id=net,hostfwd=tcp::2222-:22 -device virtio-net-device,netdev=net \
		-nographic -no-reboot > "$work/out/console-$1.log" 2>&1 &
	qemu=$!
	# Up to 40 minutes for SSH (the first boot lays out the card, and everything is emulated)
	for _ in $(seq 120); do
		if ssh_pi true 2>/dev/null; then echo "SSH up" | tee -a "$work/out/summary.log"; return 0; fi
		kill -0 $qemu 2>/dev/null || break
		sleep 10
	done
	echo "SSH never came up" | tee -a "$work/out/summary.log"
	return 1
}

ssh_pi() {
	# The host key must stay the same across boots: a changed key fails the second boot
	ssh -q -i /tmp/key -p 2222 -o UserKnownHostsFile=/tmp/known_hosts -o StrictHostKeyChecking=accept-new \
		-o ConnectTimeout=20 applpi@localhost "$@"
}

stop_qemu() {
	for _ in $(seq 18); do kill -0 $qemu 2>/dev/null || return 0; sleep 10; done
	kill $qemu; wait $qemu 2>/dev/null || true
}

checks() {
	ssh_pi 'set -x
		cat /etc/applpi-release; hostname; uptime
		findmnt -n / ; findmnt -n /data ; findmnt -n /boot/firmware
		lsblk -o NAME,SIZE,FSTYPE,LABEL
		ls /boot/firmware | grep -iE "applpi(\.json|-error)" || echo "no settings or error file on boot"
		sudo -n ls -l /data /data/settings /data/ssh
		ls -l /run/applpi /run/applpi/authorized_keys
		readlink /etc/localtime; date
		ls -l /etc/resolv.conf; resolvectl status --no-pager | head -5
		systemctl --failed --no-legend
		systemctl list-timers --all --no-legend
		sudo -n ss -tulpn
		free -m
		systemd-analyze
		systemd-analyze blame | head -12
		sudo -n journalctl -b -u applpi-data -u applpi-settings --no-pager
		sudo -n true && echo "sudo works"
		ssh -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null root@localhost true 2>&1 | tail -1' \
		> "$work/out/checks-$1.log" 2>&1
}

"$@"
