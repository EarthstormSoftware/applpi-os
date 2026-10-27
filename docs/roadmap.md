# Roadmap

ApplPi OS turns older Raspberry Pis (the Pi 2 first) into appliances that need as little looking after as a Pico W:
a frozen, read-only OS image, one or more Python apps on a separate data partition, and nothing running or
listening that isn't needed. The OS is only changed by building and installing a new image; apps are deployed on
their own, from their own repos, and an OS update never touches them.

## Design

### Target hardware

- Raspberry Pi 2 Model B (v1.1 BCM2836 or v1.2 BCM2837). The image is 32-bit (armhf) so it runs on both, and on
  other 32-bit-capable Pis.
- The Pi 2 has no Wi-Fi or Bluetooth of its own: each Pi has a USB Wi-Fi adapter. The test Pi's is the official
  Raspberry Pi dongle (Broadcom BCM43143, USB `0a5c:bd1e`, `brcmfmac`, 2.4 GHz only). Check any other adapter's
  USB ID before relying on it: some Realtek chips need drivers the image doesn't have.
- The Pi 2 has no USB device mode and there is no serial cable, so the only way in is SSH over Wi-Fi. Wi-Fi and the
  SSH key are set on the card when it is flashed, so the first boot comes up ready for SSH.

### Base OS

Raspberry Pi OS Lite (Debian 13 "Trixie", 32-bit), built with [pi-gen](https://github.com/RPi-Distro/pi-gen),
the official image builder, so the Pi's own kernel, firmware and drivers (HATs, the DSI display) work as they do on
Raspberry Pi OS.

Stripped and locked down:
- No desktop, Bluetooth, printing, swap or package timers (no automatic updates). Kernels for the Pi 2/3 (v7) and
  the Pi 3/4 and later (v8) only, and no kernel headers.
- Root filesystem read-only, with changes kept in RAM (`overlayroot=tmpfs`, as raspi-config's overlay file system),
  so pulling the power is harmless and the card isn't worn. Logs are kept in RAM too.
- SSH by key only, no passwords, for one admin user, `applpi` (passwordless sudo; root can't log in). Listening: SSH,
  mDNS (for `<name>.local`) and the apps' own ports. Nothing else.
- Network: systemd-networkd (DHCP on Ethernet and Wi-Fi), wpa_supplicant, systemd-resolved (local stub only, no
  LLMNR), Avahi for mDNS.
- Compiled libraries the apps need are part of the OS (for example `rpi_ws281x` for the Unicorn HAT and SDL2/pygame
  drawing straight to the display), so an app is plain Python and a deploy never compiles anything on the Pi.

Alternatives considered: Alpine Linux in diskless mode (smaller and runs from RAM, but musl and no prebuilt
Python wheels make the Pimoroni and display libraries hard work on a 900 MHz Pi), Buildroot (the most
firmware-like, but every package change is a rebuild of a whole toolchain), DietPi and Ubuntu Core (not lighter
in the way that matters here).

### Card layout

| Partition | Holds | Changed by |
|---|---|---|
| boot (FAT) | firmware, `config.txt`, `slotA/` and `slotB/` (kernels, initramfs, device trees, overlays, `cmdline.txt`), `applpi.json` until first boot | OS updates (the inactive slot only) |
| root A | the OS, read-only | OS updates |
| root B | the other OS slot, read-only | OS updates |
| data (ext4) | `/data/apps/<name>`, app settings, Wi-Fi details, SSH host keys | app deploys |

The firmware's `os_prefix` setting in `config.txt` picks the slot (the build moves the OS's boot files into
`slotA/`: `image\export-slots.sh`). The firmware itself (`bootcode.bin`, `start*.elf`, `fixup*.dat`) is shared by
both slots, so an OS update doesn't change it. Both root slots have a fixed size (1.25 GiB,
`ROOT_SLOT_MIB` in `image\config`; the root uses about 600 MB), so an update always fits. The boot partition is 384 MiB: the
firmware is about 23 MB and a slot's boot files about 57 MB, so it holds both slots with room for them to double, and
the build checks the other slot would fit twice over. (It can't grow once a Pi is in use, as the root slots follow
it.) A 4 GB card keeps about 0.8 GiB for data. If the root outgrows its slot, apt's package lists, other
locales and the docs (about 190 MB) can go: nothing is installed on the Pi itself. Root B and the data partition are created on first
boot (`applpi-data.service`), the data partition filling the rest of the card. The SSH host key lives on the data
partition, so a Pi keeps the same identity across OS updates.

### Device settings and first boot

Windows can't write ext4, so the flash tool writes the device's settings to `applpi.json` on the boot partition
(format: `image\applpi.example.json`): hostname, time zone, Wi-Fi country, network and password, SSH public key(s).
At boot, `applpi-settings.service` checks the file, moves the settings to the data partition (keeping the Wi-Fi key
made from the password, not the password) and deletes `applpi.json`, so the password doesn't stay on the FAT
partition. `applpi.json` can also carry the Pi's SSH host key: `applpi` makes one per Pi, keeps it (encrypted) with
the Pi's settings and adds it to `known_hosts`, so a reflashed Pi keeps its identity; without one, the Pi makes its
own. If the file is wrong, it is deleted anyway and the reason is left in `applpi-error.txt` on the boot
partition. Then, at every boot, it sets the hostname, time zone, Wi-Fi and SSH keys from the data partition: the root
is read-only, so they are written to RAM.

Per-device details are kept outside the repo, in `%APPDATA%\ApplPi` (only `*.example.json` files are committed).

### OS updates (A/B)

An update writes the new image into the slot that isn't running, over SSH (root partition and the slot's boot
files), then tries it once with the firmware's tryboot: `tryboot.txt` names the new slot (`os_prefix=slotB/`) and the
Pi reboots with `systemctl --reboot-argument="0 tryboot" reboot` (the usual `reboot "0 tryboot"` form is rejected).
The new slot sees it was tried (`/proc/device-tree/chosen/bootloader/tryboot` is 1) and, once healthy (Wi-Fi up, SSH
answering), makes itself permanent by switching `config.txt`. Any reset before that (the hardware watchdog if it
hangs, a power cut, a kernel that doesn't start) boots `config.txt`, the old slot, again. Checked on the Pi 2 (firmware
of Sep 2026): tryboot.txt was used for exactly one boot.

`applpi update <name>` sends the `.img.xz` over SSH to `os-update write` on the Pi
(`/usr/lib/applpi/os-update`, Python, as root). It decompresses as the image arrives, writes the image's root
partition straight into the other slot's partition and reads it back to compare checksums, and keeps the boot
partition (as a sparse file on the data partition) just long enough to copy its `slotA/` into the other slot's
folder, with `root=` in its `cmdline.txt` set for that slot. Then it writes `tryboot.txt`: the new image's
`config.txt`, starting the new slot. `os-update try` restarts with tryboot, and `applpi` waits for the Pi to answer
from a new boot (the boot ID changes). The Pi then decides for itself whether to keep the new slot:
`applpi-os-check.service` runs `os-update check` at every boot, and on a trial boot it waits (up to 5 minutes) for
the system to finish starting with no failed units, SSH running and a routable network. Then `os-update commit`
makes the slot permanent (`tryboot.txt` becomes `config.txt`); otherwise the Pi restarts, which starts the old slot.
`applpi` shows the verdict (`check=` in `os-update status`). The other ways a trial boot can fail also end in a
restart into the old slot: a kernel panic or an initramfs that can't mount the root (`panic=10` on the kernel command
line), a hang (systemd drives the BCM2835 hardware watchdog, 15 s), and emergency or rescue mode (their shells can't
be used anyway, as root has no password). The running slot is never written, and a slot on trial can't be updated
until it's kept or left. The firmware isn't updated (only flashing changes it). The full QEMU test writes an update
and boots the other slot, but QEMU can't do tryboot: the trial boot is checked on the Pi.

Nothing in the root names the image's own partition IDs, because an update puts another image (with other IDs) in
the other slot. `fstab` only has `/proc`: the initramfs mounts the root named by `root=PARTUUID=` on the kernel
command line, and a systemd generator (`applpi-boot`) mounts partition 1 of the same card (the disk ID from `root=`)
at `/boot/firmware`, as `mount-data` finds the data partition. The one ID left is `root=` in the slot's
`cmdline.txt`: the image's own on a newly flashed card (the card has the image's disk ID), and written by the update
from the card's disk ID and the slot's partition (`-02` for A, `-03` for B).

### Apps

- One app per Pi, like a Pico: a Pi does one job, and a second job means another Pi. Deploying a different app
  replaces the one that was there. (Several apps would need a port each, sharing of GPIO pins and the LED driver,
  and a deploy tool that lists and removes apps. If it's ever needed, the service becomes one per app.)
- Each app is its own repo. Its `deploy.ps1`, like the Pico firmware's, copies it over SSH to `/data/app` and
  restarts it. The app starts at `main.py`, as on a Pico, with its pure-Python libraries (Microdot among them) in
  `lib/`. Compiled libraries belong in the OS image.
- It runs as one systemd service, `applpi-app`, which restarts it if it stops. With no app in `/data/app`, the
  service doesn't start.
- The app's web page is on port 80, as on the Pico, so it's at `http://<name>.local`. systemd gives the app the
  right to use port 80 without it being root.
- A small manifest, `app.json`: name, version and whether it needs root (`rpi_ws281x` does). Otherwise it runs as
  its own user, `app` (not `applpi`), which may use all the hardware, as on a Pico: GPIO, I2C and SPI (udev rules
  give their groups the devices), the display, touch and serial ports. Switching on I2C or SPI in `config.txt` is
  for when an app needs it. `"pwm": true` says the app uses the PWM hardware (WS2812 LEDs on GPIO 18, as the Unicorn
  HAT), which the onboard audio also uses: its driver (`snd_bcm2835`) is unloaded while that app runs, and loaded
  for any other app or none, so Pis without one keep their audio.
- Compiled libraries for apps come in the image, from piwheels' wheels for Raspberry Pi OS, each pinned to its
  SHA-256 (`image/stage2-applpi/05-libraries`): `rpi_ws281x` for WS2812 LEDs.
- The image's `app` command installs an app from a `.tar` of its folder (which `deploy.ps1` copies over), checks
  its manifest first and that it keeps running after, and removes it. A different app (another name) starts with
  empty state.
- An OS update checks the app too: if it was running before the update, the trial boot is only kept if the app
  keeps running for 30 seconds.
- To come (for App Base, so an ApplPi app's page can change what a Pico's can): the app may ask the OS, and only
  for these, to install a new version of itself sent over the network (on trial: if it doesn't keep running, the
  old one goes back), to set the time zone and NTP server, to restart the Pi, and to start setup mode (step 7: the
  name and Wi-Fi are changed there, as the name is how SSH and `applpi` know the Pi). `systemd`'s watchdog for the
  app, so a hung app (not only one that stops) is restarted.
- The root filesystem is read-only, so the app keeps its settings and anything else it writes in `/data/app-state`,
  which a deploy leaves alone.
- Both login screens show the app's name, version and whether it's running, beside the slot and last update.
- Web UI as on the Pico devices: [Microdot](https://github.com/miguelgrinberg/microdot) (it runs on CPython
  too), the same tabs, `/api/info` for health, a JSON API, optional web password, POSTs from other sites refused.
  The Wi-Fi setup hotspot, watchdog and Wi-Fi update code aren't needed: Wi-Fi is set at flashing, systemd restarts
  apps, and the deploy script updates them.

### Builds

- `image\build.ps1` builds the image in Docker (Docker Desktop on Windows): pi-gen at a fixed release tag, its stages
  0 and 1 (a bare bootable system) and our `stage2-applpi` in place of the Lite stage, which brings in compilers,
  Bluetooth, cloud-init and swap. The image goes in `deploy\`.
- Builds are local until the image works on the test Pi. Then GitHub Actions (free for a public repo) runs the same
  container, from a tag for releases or by hand from any branch for test builds. Each release keeps its image
  (compressed) and checksum. A rebuild fetches whatever packages are current, so the kept images, not the build,
  are what's frozen.
- Versions are semantic (`vMAJOR.MINOR.PATCH`) and come from git tags, for the image and the `applpi` tool alike:
  `git describe` gives `v0.1.0` for a release and `v0.1.0-3-g1ad84dc` (plus `-dirty` for uncommitted changes) for a
  development build. Major: something apps rely on changes; minor: features and new libraries; patch: fixes. 0.x
  until the app manifest and OS updates are settled. Each image records `VERSION`, `COMMIT` and `BUILD_STAMP` (UTC,
  `20261016-2249`; `v0.1.0` has `BUILD_DATE`) in
  `/etc/applpi-release` and in `applpi-release.txt` on the boot partition, shows them on the console, and is named
  `applpi-os-<version>.img.xz`.
- The Synology DS420+ was ruled out as a builder: 2 cores emulating ARM, and an old (4.4) kernel that may lack the
  `binfmt_misc` support the build needs. It is a good place to keep a copy of the images.
- While developing, changes are tried on the test Pi first (temporarily writable), then put into the build, so a
  new image is built for a batch of working changes rather than for each one.

### Tools on Windows (and Linux)

`applpi`, a .NET command line: list USB card readers (removable disks only, with their size and name, and a
confirmation before writing), download a release image and check it, write it, add `applpi.json`, and update a Pi's
OS over SSH. A small app for flashing, on the same code, comes once the steps are settled. Writing the card
needs admin rights; nothing else does.

Linux too (assessed 2026-10-27, about 2 to 3 days, most of it flashing and testing it): most of `applpi` is plain
.NET 10 and already runs there. `update` and `forget` only use OpenSSH, `%APPDATA%` becomes `~/.config`, and the
PowerShell scripts run on `pwsh` with Docker native (they may need path fixes or short `.sh` versions). What is
Windows-only, about 300 of the tool's 1,600 lines:

- Listing card readers (WMI): `lsblk --json` instead (removable or USB, size, model, mount points), with the same
  rules (no system disk, confirmation before writing).
- Writing the card (`\\.\PhysicalDriveN`, locking and dismounting volumes, kernel32): unmount the card's
  partitions, write `/dev/sdX`, sync, and have the kernel re-read the partition table.
- Admin rights (UAC): `flash` runs with, or re-runs itself with, `sudo`.
- The boot partition after writing (Windows gives it a drive letter): mount it (`udisksctl` on a desktop, or a
  temporary mount).
- Wi-Fi passwords and host keys (DPAPI): a file only the user can read (as `~/.ssh`), or the desktop's keyring
  (`secret-tool`).
- `net10.0-windows` becomes `net10.0`, with Windows and Linux versions of the parts above.

Testing it needs a Linux machine with a card reader (WSL2 doesn't see USB card readers without `usbipd`). macOS
would be about as much again (`diskutil`, `/dev/rdiskN`, the Keychain). Best done before v1 and before the flash
app (step 5), so the app is chosen once, and alongside published images, so a release can be downloaded and
flashed on either without Docker.

## Steps

Start with one test Pi (a plain Pi 2 with the official Wi-Fi dongle). The Pis with a job come after it works.

### 1. First image

pi-gen configuration that builds the stripped, read-only image locally in Docker, with first boot laying out the
card and reading `applpi.json`. Booting from slot A only for now (the firmware's slot directories come in step 4).
Until the flash tool exists, the image is written with Raspberry Pi Imager (no customisation) and `applpi.json` is
copied onto the boot partition by hand. The GitHub Actions workflow comes once the build is settled.

### 2. Flash tool

`applpi` on the command line: card readers, writing the image, `applpi.json` from the device settings in
`%APPDATA%\ApplPi`. Done when the test Pi joins Wi-Fi on first boot and SSH works with the key.

- Done: `applpi setup [name]` asks for the Pi's name, Wi-Fi and time zone (defaults from the last Pi), makes an SSH
  key if there isn't one, and writes `applpi.json` to an ApplPi card's boot partition, checked against the same rules
  as the Pi. Settings are kept in `%APPDATA%\ApplPi\devices.json`, Wi-Fi passwords encrypted with DPAPI.
- Done: `applpi flash [image]` writes the image (default: the newest in `deploy\`) and reads it back to check it,
  then carries on as `setup`. It only offers disks on the USB or SD bus that Windows calls removable media and that
  aren't the system or boot disk, shows their name, size and drive letters, and needs ERASE typed to write. It asks
  for administrator rights (UAC) and continues in a new window. The partition table is written last, so Windows
  doesn't mount a half-written card.

### 3. Apps

The `applpi-app` service, the app manifest and the app's place on the data partition, proved with a "hello" app in
its own repo: a Microdot page in the Pico style with `/api/info`, and a `deploy.ps1`. `checks.sh` checks the app
runs and answers on port 80, so `test-pi.ps1` covers it. Released as v0.3.0.

- Done (v0.3.0): one app per Pi, as above, with [ApplPi Hello](https://github.com/EarthstormSoftware/applpi-hello)
  as the example. Checked on the Pi 2: deploying, a crash (restarted), a bad manifest (refused, the old app kept), an
  app that won't start (reported with its output), removing, and two OS updates with the app running (kept, its
  settings too, the trial boot waiting for it). Not yet checked: an OS update that stops the app.

### 4. OS updates

The A/B slots, the update command and the automatic switch back. Check it by updating to a deliberately broken
image.

- Done (v0.2.0): the slots, `applpi update` and `os-update` (writing only the root's blocks that hold data, checked
  by reading back and `e2fsck`), the Pi's own check of a trial boot, the watchdog and `panic=10`, and both login
  screens showing the slot and the last update. Checked on the Pi 2: updates both ways, and an update to an image
  with a failing service (`image\test\broken-image.ps1`) going back by itself, with the reason shown. Not yet
  checked on the Pi: a hang or a kernel panic during a trial boot.
- Testing: `image\test\test-pi.ps1` updates the test Pi and runs the checks (`image\test\checks.sh`) on it, in
  about 7 minutes; the QEMU test, with the same checks, stays for what only a first boot does and for releases.

### 5. Flash app

The flashing app: WinUI if it's for Windows only, [Avalonia](https://avaloniaui.net) (also .NET, on Windows,
Linux and macOS) if `applpi` goes to Linux too (see Tools on Windows and Linux).

### 6. Check the test Pi

What's listening, memory and CPU at rest, boot time, and pulling the power while it runs (repeatedly).

- Done (v0.3.0, the Pi 2 with the Wi-Fi dongle and hello running), 2026-10-27:
  - Listening, scanned from another computer (every TCP port): SSH and the app (80) over IPv4, SSH over IPv6 (the
    app now listens on IPv6 too). UDP: only mDNS and the DHCP clients (`ss`, in the checks).
  - At rest: 80 MiB of 920 used (840 available), no swap. The app (Microdot) is 16 MiB. CPU about 95% idle; most of
    the rest is the USB controller's interrupts (about 760 a second, `dwc_otg`, as on any Pi 2 with USB Wi-Fi).
  - Two services ran that shouldn't: `paxctld` (for PaX kernels, now purged) and a second `wpa_supplicant` (the
    D-Bus one, now masked). The checks now fail on any service that isn't expected.
  - Boot: 23 s to the system running (9 s kernel and initramfs, 14 s userspace), 36 s from a restart command to SSH
    and the app answering (with the shutdown and the firmware). `ldconfig` ran at every boot (1.3 s), as the
    read-only root looks freshly updated to systemd each time: now masked. The rest is checking and mounting the
    partitions (about 4.5 s, which a power cut needs) and the splash.
- Still to do: pulling the power, repeatedly (needs a switchable plug, or hands).

### 7. Setup mode (before v1)

A second way to set a Pi up, as the Picos are (decided 2026-10-27). `applpi flash` stays as the quick way: a card
with `applpi.json` skips setup mode.

- A card with no settings (written by any imager, from a published image) starts an `ApplPi-SETUP` hotspot for 10
  minutes, as the Picos do: join it and open the setup page. It asks for the name, Wi-Fi, time zone and SSH key,
  and the settings go on the data partition as `applpi.json`'s do.
- The page is HTTPS, with a certificate the Pi makes on its first boot, so nothing on it can be read by someone
  listening to the open hotspot (the Wi-Fi password, above all). The browser can't check the certificate (no
  public authority issues them for `.local` names), so it warns once; the certificate's fingerprint is shown on
  the console, for checking against the browser's, which is what stops someone pretending to be the Pi in those
  minutes.
- SSH keys: paste your public key (or upload `id_ed25519.pub`), which needs nothing secret sent; or, for someone
  without one, the Pi makes a key pair and offers the private key to download (safe over HTTPS from listeners) and
  then forgets it. Either way the page offers the Pi's host key as a `known_hosts` line, and its fingerprint: if an
  impostor gave a false one, SSH would refuse the real Pi, so it can't go unnoticed.
- If Wi-Fi doesn't connect after repeated tries (a new router, a changed password), the Pi starts the hotspot again,
  as the Picos do, keeping its settings until new ones are saved, so it can be set up again without reflashing.
  The same makes renaming the Pi and a factory reset possible (App Base offers those through it).
- Probably no new packages: `wpa_supplicant` can run the hotspot and `systemd-networkd` has a DHCP server. The setup
  page is a small Python server that runs only in setup mode, so the rule on what listens holds the rest of the
  time.
- First: check the Pi 2's Wi-Fi dongle can run a hotspot (its driver usually can), on the test Pi.
- With this, the Linux side of `applpi` may only need `update`, which is plain SSH (see Tools on Windows and Linux).

## Then

- **HTTPS for apps** (after v1, optional): the app's page over HTTPS, so its web password (HTTP Basic auth) isn't
  sent in the clear. `applpi` would be a small certificate authority of your own: made once on the PC, it signs each
  Pi's certificate, and each PC and phone trusts it once (easy on Windows, fiddly on phones); tools such as
  `deploy.ps1 -WiFi` and Home Assistant would trust it too. Plain HTTP with the web password stays the default, as
  on the Picos, which can't serve HTTPS (a Pico W struggles even to make HTTPS requests).

- **Unicorn HAT Pi** (started: [applpi-unicorn](https://github.com/EarthstormSoftware/applpi-unicorn), on App
  Base): an 8×8 light, controlled like PlasmaStick, with the modes of the old `uhcontrol.py` and later status and
  alerts. `rpi_ws281x` is in the image, `"pwm": true` frees the PWM hardware from the audio, and the LEDs need a
  brightness cap: 64 at full white is about 3.5 A, more than a Pi 2 can supply.
- **Touch screen Pi**: the official 7" DSI display as a full-screen touch controller (pygame on KMS, no desktop)
  that drives other devices through their HTTP APIs: the Unicorn HAT Pi and the PlasmaStick's JSON API.
- The other Pi 2s, as uses come up.
