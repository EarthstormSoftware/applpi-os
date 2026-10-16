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
# card is laid out on first boot by applpi-data.service
sed -i 's/ resize//; s/^/overlayroot=tmpfs /' "${ROOTFS_DIR}/boot/firmware/cmdline.txt"
install -d "${ROOTFS_DIR}/etc/systemd/system/systemd-remount-fs.service.d"
install -m 644 files/remount-fs.conf "${ROOTFS_DIR}/etc/systemd/system/systemd-remount-fs.service.d/applpi.conf"

# The data partition and per-Pi settings
install -d "${ROOTFS_DIR}/data"
install -d "${ROOTFS_DIR}/usr/lib/applpi"
install -m 755 files/mount-data files/apply-settings "${ROOTFS_DIR}/usr/lib/applpi/"
install -m 644 files/applpi-data.service files/applpi-settings.service "${ROOTFS_DIR}/etc/systemd/system/"

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
	systemctl enable applpi-data.service applpi-settings.service systemd-networkd.service wpa_supplicant@wlan0.service ssh.service avahi-daemon.service
	systemctl disable wpa_supplicant.service
	# Its host keys would be made at every boot (each boot of a read-only root is a "first boot"): ours are on /data
	systemctl mask sshd-keygen.service
EOF
