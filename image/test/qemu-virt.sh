#!/bin/bash
# Test boots of an ApplPi OS image on QEMU's generic ARM machine ("virt"), run by test-qemu.ps1 in three containers
# sharing /work:
#   unpack <image.img.xz>  (applpi-os-tester, privileged)  the image as a 4 GB card, and overlayroot's initramfs files
#   kernel                 (applpi-os-test-kernel, ARM)     Debian's generic ARM kernel and an initramfs with overlayroot
#   boot full|quick <image.img.xz>
#                          (applpi-os-tester, privileged)  boots the card and checks it over SSH; results in /work/out.
#                                                          Full: then updates it to the same image (os-update write)
#                                                          and boots slot B as tryboot would.
#   setup <image.img.xz>   (applpi-os-tester, privileged)  boots the card with no settings, sets it up from the setup
#                                                          page, then boots it on those settings and checks it.
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

# A card with no settings, as written by any imager: setup mode, set up from its page (over QEMU's network, as it
# would be over the hotspot or Ethernet), then a boot on the settings saved there, checked as usual
setup() {
	local out=$work/out
	rm -rf "$out"; mkdir -p "$out"
	add_modules 2
	ssh-keygen -q -t ed25519 -N "" -f /tmp/key
	slot_cmdline A

	echo "== Boot 1 (no settings: setup mode)" | tee -a "$out/summary.log"
	start_qemu 1
	# Up to 40 minutes for the page (the first boot lays out the card)
	local up=
	for _ in $(seq 240); do
		if curl -sk -m 20 -o "$out/setup-form.html" https://localhost:8443/; then up=1; break; fi
		kill -0 $qemu 2>/dev/null || break
		sleep 10
	done
	if [ -z "$up" ]; then
		echo "The setup page never came up" | tee -a "$out/summary.log"
		stop_qemu
		return 0
	fi
	echo "Setup page up: $(grep -o 'This Pi has no settings yet.' "$out/setup-form.html" || echo 'not for a Pi without settings')" |
		tee -a "$out/summary.log"

	# Port 80 shows the certificate's fingerprint: it must be the one the page is served with
	local shown served
	shown=$(curl -s -m 20 http://localhost:8080/ | grep -o '<pre>[0-9A-F:]*</pre>' | sed 's/<[^>]*>//g' || true)
	served=$(openssl s_client -connect localhost:8443 < /dev/null 2> /dev/null |
		openssl x509 -noout -fingerprint -sha256 | cut -d= -f2 || true)
	if [ -n "$shown" ] && [ "$shown" = "$served" ]; then
		echo "Port 80 shows the certificate's fingerprint" | tee -a "$out/summary.log"
	else
		echo "FAILED  the fingerprint on port 80 ($shown) isn't the certificate's ($served)" | tee -a "$out/summary.log"
	fi

	# Set up as someone would: no Wi-Fi (QEMU has none), their SSH key, and the host key the page then gives
	local status
	status=$(curl -sk -m 60 -o /dev/null -w '%{http_code}' https://localhost:8443/save \
		--data hostname=applpi-qemu --data timezone=Europe/London --data country=GB --data ssid= --data keys=paste \
		--data-urlencode "public_key=$(cat /tmp/key.pub)" \
		--data fallback_enabled=on --data fallback_after=5 --data fallback_for=10 || true)
	curl -sk -m 20 -o "$out/setup-done.html" https://localhost:8443/ || true
	local line
	line=$(grep -o 'applpi-qemu\.local ssh-ed25519 [A-Za-z0-9+/=]*' "$out/setup-done.html" || true)
	if [ "$status" = 303 ] && [ -n "$line" ]; then
		echo "[localhost]:2222 ${line#* }" > /tmp/known_hosts
		echo "Saved, and the page gave the host key" | tee -a "$out/summary.log"
	else
		echo "FAILED  saving on the setup page (HTTP $status)" | tee -a "$out/summary.log"
	fi
	curl -sk -m 20 -o /dev/null -X POST https://localhost:8443/restart || true
	stop_qemu

	# The settings saved, and the host key as the page gave it
	run_boot 2 && checks 2 && ssh_pi 'hostname; sudo -n systemctl poweroff' >> "$out/checks-2.log" 2>&1 || true
	stop_qemu
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

start_qemu() {
	# SSH, and the setup page (HTTP and HTTPS) for setup mode
	qemu-system-arm -M virt,highmem=off -cpu cortex-a15 -m 1G -smp 4 \
		-kernel "$work/vmlinuz" -initrd "$work/initrd.img" -append "$cmdline" \
		-drive if=none,file="$img",format=raw,id=card -device virtio-blk-device,drive=card \
		-netdev user,id=net,hostfwd=tcp::2222-:22,hostfwd=tcp::8080-:80,hostfwd=tcp::8443-:443 \
		-device virtio-net-device,netdev=net \
		-nographic -no-reboot > "$work/out/console-$1.log" 2>&1 &
	qemu=$!
}

run_boot() {
	echo "== Boot $1" | tee -a "$work/out/summary.log"
	start_qemu "$1"
	# Up to 40 minutes for SSH (the first boot lays out the card, and everything is emulated)
	for _ in $(seq 120); do
		if ssh_pi true 2>/dev/null; then
			echo "SSH up" | tee -a "$work/out/summary.log"
			# SSH answers before the boot has finished: wait for that, so the checks see the booted system
			ssh_pi 'systemctl is-system-running --wait' 2>&1 | sed 's/^/System: /' | tee -a "$work/out/summary.log"
			[ "${PIPESTATUS[0]}" != 255 ] || echo "System: SSH failed straight after (exit 255)" | tee -a "$work/out/summary.log"
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

# The shared checks (checks.sh), into checks-<boot>.log; their FAILED lines go in the summary
checks() {
	# Retried when SSH fails: under emulation a connection can still fail now and then
	local attempt log=$work/out/checks-$1.log status
	for attempt in 1 2 3; do
		status=0
		ssh_pi 'bash -s -- qemu' < /test/checks.sh > "$log" 2>&1 || status=$?
		if [ "$status" != 255 ] && grep -q "^== Checks" "$log"; then
			grep "^FAILED" "$log" | tee -a "$work/out/summary.log" || echo "All checks passed" | tee -a "$work/out/summary.log"
			return 0
		fi
		echo "Checks on boot $1 couldn't run (attempt $attempt, ssh exit $status): $(tail -1 "$log")" |
			tee -a "$work/out/summary.log"
		sleep 20
	done
	return 1
}

"$@"
