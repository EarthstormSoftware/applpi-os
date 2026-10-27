#!/bin/bash -e
# The ApplPi system: read-only root, the data partition, per-Pi settings, the network, SSH and logs

# What this image is: on the Pi, and on the boot partition where Windows can read it
cat > "${ROOTFS_DIR}/etc/applpi-release" <<- EOF
	VERSION=${APPLPI_VERSION:-dev}
	COMMIT=${APPLPI_COMMIT:-unknown}
	BUILD_STAMP=${APPLPI_BUILD_STAMP:-$(date -u +%Y%m%d-%H%M)}
EOF
chmod 644 "${ROOTFS_DIR}/etc/applpi-release"
install -m 644 "${ROOTFS_DIR}/etc/applpi-release" "${ROOTFS_DIR}/boot/firmware/applpi-release.txt"

# Read-only root, with changes kept in RAM (overlayroot, as raspi-config's overlay file system), and no resize: the
# card is laid out on first boot by applpi-data.service. A kernel panic, or an initramfs that can't mount the root,
# restarts the Pi after 10 s instead of stopping (after an OS update's trial boot, that starts the permanent slot).
sed -i 's/ resize//; s/^/overlayroot=tmpfs panic=10 /' "${ROOTFS_DIR}/boot/firmware/cmdline.txt"
install -d "${ROOTFS_DIR}/etc/systemd/system/systemd-remount-fs.service.d"
install -m 644 files/remount-fs.conf "${ROOTFS_DIR}/etc/systemd/system/systemd-remount-fs.service.d/applpi.conf"

# No partition IDs in fstab: pi-gen's would be this image's, and an OS update puts another image in the other slot.
# The initramfs mounts the root named on the kernel command line, and the boot partition is mounted by a generator
# that finds it on the same card. (A writable root while developing then needs rw on the kernel command line too.)
cat > "${ROOTFS_DIR}/etc/fstab" <<- EOF
	proc            /proc           proc    defaults          0       0
EOF
install -m 755 files/boot-generator "${ROOTFS_DIR}/usr/lib/systemd/system-generators/applpi-boot"

# The data partition and per-Pi settings
install -d "${ROOTFS_DIR}/data"
install -d "${ROOTFS_DIR}/usr/lib/applpi"
install -m 755 files/mount-data files/apply-settings files/os-update "${ROOTFS_DIR}/usr/lib/applpi/"
install -m 644 files/applpi-data.service files/applpi-settings.service "${ROOTFS_DIR}/etc/systemd/system/"

# OS updates: a trial boot is kept once it's healthy, and otherwise (including a hang, by the hardware watchdog, or
# emergency mode) the Pi restarts into the permanent slot
install -m 644 files/applpi-os-check.service "${ROOTFS_DIR}/etc/systemd/system/"
install -d "${ROOTFS_DIR}/etc/systemd/system.conf.d"
install -m 644 files/watchdog.conf "${ROOTFS_DIR}/etc/systemd/system.conf.d/applpi-watchdog.conf"
for unit in emergency rescue; do
	install -d "${ROOTFS_DIR}/etc/systemd/system/${unit}.service.d"
	install -m 644 files/emergency.conf "${ROOTFS_DIR}/etc/systemd/system/${unit}.service.d/applpi.conf"
done

# The app (one per Pi, in /data/app), run by applpi-app.service as the app user, which can use the hardware: GPIO,
# I2C and SPI by the udev rules, the display, touch and serial ports by Debian's groups. The user's ID is fixed, as
# /data/app-state is the app's and outlasts OS updates (Debian gives out system IDs from 999 down).
install -m 755 files/app "${ROOTFS_DIR}/usr/lib/applpi/"
install -m 644 files/applpi-app.service "${ROOTFS_DIR}/etc/systemd/system/"
install -m 644 files/99-applpi-hardware.rules "${ROOTFS_DIR}/etc/udev/rules.d/"

# Logs in RAM
install -d "${ROOTFS_DIR}/etc/systemd/journald.conf.d"
install -m 644 files/journald.conf "${ROOTFS_DIR}/etc/systemd/journald.conf.d/applpi.conf"

# Network: systemd-networkd, wpa_supplicant for wlan0 with the config apply-settings writes, mDNS by Avahi
install -m 644 files/10-wired.network files/20-wireless.network "${ROOTFS_DIR}/etc/systemd/network/"
install -d "${ROOTFS_DIR}/etc/systemd/system/wpa_supplicant@wlan0.service.d"
install -m 644 files/wpa_supplicant.conf "${ROOTFS_DIR}/etc/systemd/system/wpa_supplicant@wlan0.service.d/applpi.conf"
# The hostname is set at boot and resolved by nss-myhostname, so it isn't in /etc/hosts
sed -i '/^127\.0\.1\.1/d' "${ROOTFS_DIR}/etc/hosts"

# SSH by key only, for the applpi user, with host keys on the data partition
install -m 644 files/sshd.conf "${ROOTFS_DIR}/etc/ssh/sshd_config.d/applpi.conf"
rm -f "${ROOTFS_DIR}/etc/ssh/"ssh_host_*

# The admin user has no password (SSH keys only), so sudo doesn't ask for one; root can't log in
install -m 440 files/sudoers "${ROOTFS_DIR}/etc/sudoers.d/010_applpi-nopasswd"

# ('*', not '!': sshd can treat '!' as a locked account and refuse keys too)
on_chroot <<- EOF
	usermod --pass='*' root
	usermod --pass='*' --shell /bin/bash -aG sudo,adm,video ${FIRST_USER_NAME}
	for group in gpio i2c spi; do getent group \$group > /dev/null || groupadd --system \$group; done
	groupadd --system --gid 800 app
	useradd --system --uid 800 --gid app --groups gpio,i2c,spi,video,input,dialout --home-dir /data/app-state \
		--no-create-home --shell /usr/sbin/nologin app
	systemctl enable applpi-data.service applpi-settings.service applpi-os-check.service applpi-app.service systemd-networkd.service wpa_supplicant@wlan0.service ssh.service avahi-daemon.service
	# Only wlan0's: the D-Bus one was still enabled in the image after this disable, so it's masked, its D-Bus name too
	systemctl disable wpa_supplicant.service
	systemctl mask wpa_supplicant.service
	rm -f /etc/systemd/system/dbus-fi.w1.wpa_supplicant1.service
	# Its host keys would be made at every boot (each boot of a read-only root is a "first boot"): ours are on /data
	systemctl mask sshd-keygen.service
EOF
