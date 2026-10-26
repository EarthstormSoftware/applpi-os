#!/bin/bash
# Test boots of an ApplPi OS image on QEMU's generic ARM machine ("virt"), run by test-qemu.ps1 in three containers
# sharing /work:
#   unpack <image.img.xz>  (applpi-os-tester, privileged)  the image as a 4 GB card, and overlayroot's initramfs files
#   kernel                 (applpi-os-test-kernel, ARM)     Debian's generic ARM kernel and an initramfs with overlayroot
#   boot full|quick <image.img.xz>
#                          (applpi-os-tester, privileged)  boots the card and checks it over SSH; results in /work/out.
#                                                          Full: then updates it to the same image (os-update write)
#                                                          and boots slot B as tryboot would.
# QEMU's Pi 2 model can't read the SD card with the Pi kernel, so this tests everything of ours (first boot, settings,
# SSH, read-only root, writing an update) but not the Pi's kernel, firmware (os_prefix, tryboot) or Wi-Fi.
set -euo pipefail

work=/work
img=$work/sd.img

# Byte offset of partition $1 in the card image
offset() {
	echo $(( $(sfdisk -d "$img" | awk -F'[=,]' -v p="${img}$1" '$1 ~ p" " {print $2}') * 512 ))
}

unpack() {
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
	cp -a "$work/overlayroot/." /
	version=$(basename /usr/lib/modules/*)
	mkinitramfs -o "$work/initrd.img" "$version"
	cp "/boot/vmlinuz-$version" "$work/vmlinuz"
	rm -rf "$work/modules"; mkdir -p "$work/modules"
	cp -a "/usr/lib/modules/$version" "$work/modules/"
}

boot() {
	local quick=$1 image_xz=$2
	local out=$work/out
	rm -rf "$out"; mkdir -p "$out"

	add_modules 2

	# Throwaway keys and test settings (no Wi-Fi in QEMU). The Pi's host key is sent with them, as applpi does, and
	# ssh trusts only that key: a Pi that made its own wouldn't be reachable.
	local boot_img="$img@@$(offset 1)"
	ssh-keygen -q -t ed25519 -N "" -f /tmp/key
	ssh-keygen -q -t ed25519 -N "" -C "" -f /tmp/hostkey
	echo "[localhost]:2222 $(cut -d' ' -f1,2 /tmp/hostkey.pub)" > /tmp/known_hosts
	cat > /tmp/applpi.json <<-EOF
		{ "hostname": "applpi-qemu", "timezone": "Europe/London", "ssh_keys": ["$(cat /tmp/key.pub)"],
		  "host_key": "$(awk '{printf "%s\\n", $0}' /tmp/hostkey)" }
	EOF
	mcopy -o -i "$boot_img" /tmp/applpi.json ::applpi.json
	slot_cmdline A

	# The file written to /etc on the first boot must be gone on the second (the root's changes are in RAM)
	if run_boot 1 && checks 1; then
		ssh_pi 'sudo -n touch /etc/test-overlay' || true
		if [ "$quick" != quick ]; then
			update "$image_xz"
		fi
		ssh_pi 'sudo -n systemctl poweroff' || true
	fi
	stop_qemu
	if [ "$quick" != quick ]; then
		# As tryboot would: slot B's cmdline.txt, whose root is partition 3
		add_modules 3
		slot_cmdline B
		run_boot 2 && checks 2 && ssh_pi 'ls /etc/test-overlay; sudo -n /usr/lib/applpi/os-update commit; sudo -n systemctl poweroff' \
			>> "$out/checks-2.log" 2>&1 || true
		stop_qemu
	fi
	echo "== Boot partition after" | tee -a "$out/summary.log"
	mdir -i "$boot_img" :: | grep -iE "applpi(\.json|-error)" | tee -a "$out/summary.log" || echo "no settings or error file" | tee -a "$out/summary.log"
}

# The generic kernel's modules go into the root of slot partition $1, so the Pi image needn't carry them
add_modules() {
	mkdir -p /mnt/root
	mount -o loop,offset="$(offset "$1")" "$img" /mnt/root
	cp -a "$work/modules/." /mnt/root/usr/lib/modules/
	umount /mnt/root
}

# The kernel command line from slot $1's cmdline.txt, for QEMU's serial console. Emulation is slow: finding the
# devices can take minutes, longer than systemd's usual 90 s wait. AppArmor is off for the test boots only: loading
# its profiles takes minutes when emulated, and tests nothing of ours.
slot_cmdline() {
	mcopy -o -i "$img@@$(offset 1)" "::slot$1/cmdline.txt" /tmp/cmdline.txt
	cmdline="$(sed 's/console=[^ ]*//g' /tmp/cmdline.txt | tr -s ' ' | tr -d '\n') console=ttyAMA0 net.ifnames=0"
	cmdline="$cmdline systemd.default_device_timeout_sec=900 apparmor=0"
	echo "Kernel command line (slot $1): $cmdline"
}

# An OS update to the same image, as applpi update sends it
update() {
	echo "== Update" | tee -a "$work/out/summary.log"
	if ssh_pi 'sudo -n /usr/lib/applpi/os-update write' < "$1" > "$work/out/update.log" 2>&1; then
		echo "Written to slot B" | tee -a "$work/out/summary.log"
	else
		echo "Update failed: $(tail -1 "$work/out/update.log")" | tee -a "$work/out/summary.log"
	fi
	ssh_pi 'sudo -n /usr/lib/applpi/os-update status; grep ^os_prefix /boot/firmware/tryboot.txt; ls /boot/firmware/slotB | grep -v dtb' \
		>> "$work/out/update.log" 2>&1 || true
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
		if ssh_pi true 2>/dev/null; then
			echo "SSH up" | tee -a "$work/out/summary.log"
			# SSH answers before the boot has finished: wait for that, so the checks see the booted system
			ssh_pi 'systemctl is-system-running --wait' 2>&1 | sed 's/^/System: /' | tee -a "$work/out/summary.log"
			return 0
		fi
		kill -0 $qemu 2>/dev/null || break
		sleep 10
	done
	echo "SSH never came up" | tee -a "$work/out/summary.log"
	return 1
}

ssh_pi() {
	# Only the host key sent in applpi.json is trusted, on both boots
	ssh -i /tmp/key -p 2222 -o UserKnownHostsFile=/tmp/known_hosts -o StrictHostKeyChecking=yes \
		-o LogLevel=ERROR -o ConnectTimeout=20 -o ServerAliveInterval=15 applpi@localhost "$@"
}

stop_qemu() {
	for _ in $(seq 18); do kill -0 $qemu 2>/dev/null || return 0; sleep 10; done
	kill $qemu; wait $qemu 2>/dev/null || true
}

checks() {
	# Retried: under emulation a connection can still fail now and then
	local attempt
	for attempt in 1 2 3; do
		run_checks "$1" && [ -s "$work/out/checks-$1.log" ] && return 0
		echo "Checks on boot $1 failed (attempt $attempt): $(tail -1 "$work/out/checks-$1.log")" | tee -a "$work/out/summary.log"
		sleep 20
	done
	return 1
}

run_checks() {
	ssh_pi 'set -x
		cat /etc/applpi-release; hostname; uptime
		findmnt -n / ; findmnt -n /media/root-ro ; findmnt -n /data ; findmnt -n /boot/firmware
		sudo -n /usr/lib/applpi/os-update status
		grep -c PARTUUID /etc/fstab /media/root-ro/etc/fstab; systemctl is-active boot-firmware.mount
		ls /boot/firmware /boot/firmware/slotA | grep -vE "\.dtb$"; grep "^os_prefix" /boot/firmware/config.txt
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
		bash /etc/update-motd.d/00-applpi | sed "s/\x1b\[[0-9;]*m//g" | tail -7
		grep "OS slot" /etc/issue
		ls -l /etc/update-motd.d; wc -c < /etc/motd
		ssh -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null root@localhost true 2>&1 | tail -1' \
		> "$work/out/checks-$1.log" 2>&1
}

"$@"
