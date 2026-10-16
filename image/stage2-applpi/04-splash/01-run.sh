#!/bin/bash -e
# A clean screen for when a monitor is connected: the logo while booting, then the Pi's name and address. The boot
# messages still go to the journal and the kernel log; to see them on screen, remove "quiet loglevel=3" and "splash"
# from cmdline.txt on the card (or press Esc during boot).

# The firmware's rainbow square and the kernel's raspberries go; the console never blanks; the log only shows errors.
# HDMI is forced on: the Pi 2's hotplug signal is unreliable with some monitors, and the KMS driver would otherwise
# switch the display off when it takes over (it does no harm without a monitor).
echo "disable_splash=1" >> "${ROOTFS_DIR}/boot/firmware/config.txt"
sed -i '1s/$/ quiet loglevel=3 logo.nologo consoleblank=0 vc4.force_hotplug=1 splash plymouth.ignore-serial-consoles/' \
	"${ROOTFS_DIR}/boot/firmware/cmdline.txt"

# Plymouth theme: the logo over the spinner theme's animation, on the logo's dark background
theme="${ROOTFS_DIR}/usr/share/plymouth/themes/applpi"
install -d "$theme"
find "${ROOTFS_DIR}/usr/share/plymouth/themes/spinner" -name '*.png' ! -name watermark.png -exec cp {} "$theme/" \;
rsvg-convert -w 560 files/logo.svg -o "$theme/watermark.png"
install -m 644 files/applpi.plymouth "$theme/"
on_chroot <<- EOF
	plymouth-set-default-theme applpi
EOF
# The HDMI port reads the monitor's details over I2C: without its driver in the initramfs, the display driver takes
# the screen early in boot but can't use HDMI until the root is mounted, leaving the screen blank for the splash
echo i2c-bcm2835 >> "${ROOTFS_DIR}/etc/initramfs-tools/modules"

# The console: cleared, then the Pi's name, addresses and how to log in above the prompt (agetty fills these in and
# redraws them when an address changes)
install -d "${ROOTFS_DIR}/etc/systemd/system/getty@tty1.service.d"
install -m 644 files/getty.conf "${ROOTFS_DIR}/etc/systemd/system/getty@tty1.service.d/applpi.conf"
. "${ROOTFS_DIR}/etc/applpi-release"
sed "s/@VERSION@/${VERSION}/; s/@BUILD_DATE@/${BUILD_DATE}/" files/issue > "${ROOTFS_DIR}/etc/issue"
