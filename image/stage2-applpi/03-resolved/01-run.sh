#!/bin/bash -e
# DNS by systemd-resolved, installed last: it makes /etc/resolv.conf a link that only works on the Pi

# Only the local stub resolver: no LLMNR or mDNS listening (Avahi answers mDNS)
install -d "${ROOTFS_DIR}/etc/systemd/resolved.conf.d"
install -m 644 files/resolved.conf "${ROOTFS_DIR}/etc/systemd/resolved.conf.d/applpi.conf"

# The rest of the build still needs DNS, and pi-gen's export puts its own resolv.conf in, so the link is made at boot
install -m 644 files/tmpfiles.conf "${ROOTFS_DIR}/etc/tmpfiles.d/applpi-resolv.conf"
rm -f "${ROOTFS_DIR}/etc/resolv.conf"
cp /etc/resolv.conf "${ROOTFS_DIR}/etc/resolv.conf"

on_chroot <<- EOF
	systemctl enable systemd-resolved.service
EOF
