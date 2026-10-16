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
| boot (FAT) | firmware, `slotA/` and `slotB/` (kernel, device tree, `cmdline.txt`), `applpi.json` until first boot | OS updates (the inactive slot only) |
| root A | the OS, read-only | OS updates |
| root B | the other OS slot, read-only | OS updates |
| data (ext4) | `/data/apps/<name>`, app settings, Wi-Fi details, SSH host keys | app deploys |

The firmware's `os_prefix` setting in `config.txt` picks the slot. Both root slots have a fixed size (1.25 GiB,
`ROOT_SLOT_MIB` in `image\config`; the root uses about 600 MB), so an update always fits. With the 256 MiB boot
partition, a 4 GB card keeps about 1 GiB for data. If the root outgrows its slot, apt's package lists, other
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

Before this works, nothing may name the image's own partition IDs: `cmdline.txt` and `fstab` use the PARTUUIDs of the
image they came from, which an update to another image changes. They have to come from the card itself, as the data
partition's mount does.

### Apps

- Each app is its own repo. It deploys to `/data/apps/<name>` over SSH with a `deploy.ps1`, like the Pico
  firmware's, and is run by a shared systemd service (`applpi-app@<name>`) that restarts it if it stops.
- A small manifest per app: name, port, whether it needs root (`rpi_ws281x` does), and hardware groups.
- Apps are plain Python. Pure-Python dependencies go with the app; compiled ones belong in the OS image.
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

### Tools on Windows

`applpi`, a .NET command line: list USB card readers (removable disks only, with their size and name, and a
confirmation before writing), download a release image and check it, write it, add `applpi.json`, and update a Pi's
OS over SSH. A small WinUI app for flashing, on the same code, comes once the steps are settled. Writing the card
needs admin rights; nothing else does.

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

The data partition, the `applpi-app@` service and the app manifest, proved with a "hello" app: a Microdot page in
the Pico style and a `deploy.ps1`.

### 4. OS updates

The A/B slots, the update command and the automatic switch back. Check it by updating to a deliberately broken
image.

### 5. Flash app

The WinUI flashing app.

### 6. Check the test Pi

What's listening, memory and CPU at rest, boot time, and pulling the power while it runs (repeatedly).

## Then

- **Unicorn HAT Pi**: an 8×8 status and alert display (CPU temperature and load, alerts posted to it by other
  devices). `rpi_ws281x` on GPIO 18 needs the onboard audio off, and the LEDs need a brightness cap: 64 at full white
  is about 3.5 A, more than a Pi 2 can supply.
- **Touch screen Pi**: the official 7" DSI display as a full-screen touch controller (pygame on KMS, no desktop)
  that drives other devices through their HTTP APIs: the Unicorn HAT Pi and the PlasmaStick's JSON API.
- The other Pi 2s, as uses come up.
