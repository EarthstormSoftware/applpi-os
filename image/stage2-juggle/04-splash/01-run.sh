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
theme="${ROOTFS_DIR}/usr/share/plymouth/themes/juggle"
install -d "$theme"
find "${ROOTFS_DIR}/usr/share/plymouth/themes/spinner" -name '*.png' ! -name watermark.png -exec cp {} "$theme/" \;
rsvg-convert -w 560 files/logo.svg -o "$theme/watermark.png"
install -m 644 files/juggle.plymouth "$theme/"
on_chroot <<- EOF
	plymouth-set-default-theme juggle
EOF
# The HDMI port reads the monitor's details over I2C: without its driver in the initramfs, the display driver takes
# the screen early in boot but can't use HDMI until the root is mounted, leaving the screen blank for the splash
echo i2c-bcm2835 >> "${ROOTFS_DIR}/etc/initramfs-tools/modules"

# The console: cleared, then the Pi's name, addresses and how to log in above the prompt (agetty fills these in and
# redraws them when an address changes)
install -d "${ROOTFS_DIR}/etc/systemd/system/getty@tty1.service.d"
install -m 644 files/getty.conf "${ROOTFS_DIR}/etc/systemd/system/getty@tty1.service.d/juggle.conf"
# The icon in the console's top left corner, once for each CPU core (console-logo draws them in a row): 96 pixels
# wide, 12 characters of the 8-pixel console font, rendered as raw RGB on the console's black background
install -d "${ROOTFS_DIR}/usr/share/juggle"
rsvg-convert -w 96 files/icon.svg -o /tmp/console-logo.png
magick /tmp/console-logo.png -background black -flatten -depth 8 "rgb:${ROOTFS_DIR}/usr/share/juggle/console-logo.rgb"
magick identify -format "%w %h" /tmp/console-logo.png > "${ROOTFS_DIR}/usr/share/juggle/console-logo.size"
rm /tmp/console-logo.png
install -m 755 files/console-logo "${ROOTFS_DIR}/usr/lib/juggle/"
install -m 644 files/juggle-console-logo.service "${ROOTFS_DIR}/etc/systemd/system/"
on_chroot <<- EOF
	systemctl enable juggle-console-logo.service
EOF

# The SSH login screen: cleared, then the icon (pixel art, as coloured half blocks) beside the same information as
# the console. Debian's uname line, overlayroot's mount table and the licence text go.
awk -f files/ansi-logo.awk files/logo.txt > "${ROOTFS_DIR}/usr/share/juggle/logo.ansi"
install -m 755 files/motd "${ROOTFS_DIR}/etc/update-motd.d/00-juggle"
chmod -x "${ROOTFS_DIR}/etc/update-motd.d/10-uname" "${ROOTFS_DIR}/etc/update-motd.d/97-overlayroot"
: > "${ROOTFS_DIR}/etc/motd"

# Both screens show the app and the running OS slot: the console's text is written from this template at each boot,
# and when the app starts or stops
install -m 755 files/slot "${ROOTFS_DIR}/usr/lib/juggle/"
. "${ROOTFS_DIR}/etc/juggle-release"
sed "s/@VERSION@/${VERSION}/; s/@BUILD_STAMP@/${BUILD_STAMP}/" files/issue > "${ROOTFS_DIR}/usr/share/juggle/issue"
sed "s/@SLOT@/unknown/; s/ (@MODEL@)//; /@UPDATE@/d; /@APP@/d; /@SETUP@/d" "${ROOTFS_DIR}/usr/share/juggle/issue" > "${ROOTFS_DIR}/etc/issue"
