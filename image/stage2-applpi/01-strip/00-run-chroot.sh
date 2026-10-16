#!/bin/bash -e
# Removes what stages 0 and 1 add that an ApplPi doesn't need

# Kernels: v7 (Pi 2, Pi 3) and v8 (Pi 3, Pi 4 and later run a 64-bit kernel under 32-bit Raspberry Pi OS) stay.
# v6 is the Pi 1 and Zero. No kernel headers (or the compilers they bring): nothing is compiled on the Pi.
# cron and logrotate: nothing runs on a schedule, and the logs are in RAM, kept small by journald.
apt-get purge -y cron cron-daemon-common logrotate \
	$(dpkg-query -W -f '${db:Status-Abbrev} ${Package}\n' 'linux-image-*-rpi-v6' 'linux-headers-*' 'linux-kbuild-*' \
		'linux-base-*rpi-v6' | awk '$1 == "ii" { print $2 }')
apt-get autoremove --purge -y
apt-get clean
# What the header packages leave behind of the v6 kernel's modules directory
rm -rf /usr/lib/modules/*-rpi-v6

# No timers that change the system: no package updates, backups or scheduled disk checks. No firewall rules either
# (nftables loads an empty rule set): nothing listens that shouldn't.
for unit in apt-daily.timer apt-daily-upgrade.timer dpkg-db-backup.timer e2scrub_all.timer e2scrub_reap.service \
	fstrim.timer man-db.timer nftables.service; do
	if [ -e "/usr/lib/systemd/system/${unit}" ]; then
		systemctl disable "${unit}"
		systemctl mask "${unit}"
	fi
done
