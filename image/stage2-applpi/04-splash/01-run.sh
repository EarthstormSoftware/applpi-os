#!/bin/bash -e
# A clean screen for when a monitor is connected: the logo while booting, then the Pi's name and address. The boot
# messages still go to the journal and the kernel log; to see them on screen, remove "quiet loglevel=3" and "splash"
# from slotA/cmdline.txt on the card (or press Esc during boot).

# The firmware's rainbow square and the kernel's raspberries go; the console blanks (and the monitor sleeps) after 5
# minutes without a key pressed; the log only shows errors. HDMI is forced on: the Pi 2's hotplug signal is unreliable
# with some monitors, and the KMS driver would otherwise switch the display off when it takes over (it does no harm
# without a monitor).
echo "disable_splash=1" >> "${ROOTFS_DIR}/boot/firmware/config.txt"
sed -i '1s/$/ quiet loglevel=3 logo.nologo consoleblank=300 vc4.force_hotplug=1 splash plymouth.ignore-serial-consoles/' \
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
# The logo, smaller, in the console's top right corner: about the width of the version line (44 characters of the
# 8-pixel console font), rendered as raw RGB on the console's black background for console-logo to draw
install -d "${ROOTFS_DIR}/usr/share/applpi"
rsvg-convert -w 352 files/logo.svg -o /tmp/console-logo.png
magick /tmp/console-logo.png -background black -flatten -depth 8 "rgb:${ROOTFS_DIR}/usr/share/applpi/console-logo.rgb"
magick identify -format "%w %h" /tmp/console-logo.png > "${ROOTFS_DIR}/usr/share/applpi/console-logo.size"
rm /tmp/console-logo.png
install -m 755 files/console-logo "${ROOTFS_DIR}/usr/lib/applpi/"
install -m 644 files/applpi-console-logo.service "${ROOTFS_DIR}/etc/systemd/system/"
on_chroot <<- EOF
	systemctl enable applpi-console-logo.service
EOF

# The SSH login screen: cleared, then the apple (pixel art, as coloured half blocks) beside the same information as
# the console. Debian's uname line, overlayroot's mount table and the licence text go.
awk -f files/ansi-logo.awk files/apple.txt > "${ROOTFS_DIR}/usr/share/applpi/logo.ansi"
install -m 755 files/motd "${ROOTFS_DIR}/etc/update-motd.d/00-applpi"
chmod -x "${ROOTFS_DIR}/etc/update-motd.d/10-uname" "${ROOTFS_DIR}/etc/update-motd.d/97-overlayroot"
: > "${ROOTFS_DIR}/etc/motd"

# Both screens show the running OS slot: the console's text is written from this template at each boot
install -m 755 files/slot "${ROOTFS_DIR}/usr/lib/applpi/"
. "${ROOTFS_DIR}/etc/applpi-release"
sed "s/@VERSION@/${VERSION}/; s/@BUILD_STAMP@/${BUILD_STAMP}/" files/issue > "${ROOTFS_DIR}/usr/share/applpi/issue"
sed "s/@SLOT@/unknown/" "${ROOTFS_DIR}/usr/share/applpi/issue" > "${ROOTFS_DIR}/etc/issue"
