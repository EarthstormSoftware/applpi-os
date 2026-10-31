<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/logo-dark.svg">
  <img src="docs/logo-light.svg" alt="" width="160">
</picture>

# Juggle OS

A frozen, minimal OS image that turns older Raspberry Pis (the Pi 2 first) into appliances: as little to look after
as a Pico W, with Python, the network, USB and GPIO.

- **Frozen:** each release is one image. The root filesystem is read-only, so pulling the power is harmless, and
  nothing updates by itself. To change the OS, build a new image and install it.
- **Safe updates:** a new image goes on over SSH into a second OS slot, and the Pi tries it once. It keeps it only if
  it starts properly, and otherwise goes back to the OS it had, by itself.
- **Minimal:** Raspberry Pi OS Lite with no desktop, no Bluetooth and no automatic updates. Nothing listens except
  SSH (by key only), mDNS and the app you deploy (and, only in setup mode, the setup page).
- **Apps on their own:** one app per Pi, like a Pico's firmware: plain Python, kept on a separate data partition and
  deployed from its own repo over SSH. Installing a new OS image leaves it alone.
- **No serial cable or screen needed:** the card is flashed from Windows with the Pi's name, Wi-Fi and SSH key, so it
  comes up on the network ready for SSH. Or, written by any imager, it starts a setup hotspot, as a Pico does.

> **Status:** v0.8.0 runs on the Pi 2: set up with the Juggle Flash app or `juggle flash` on Windows or Linux (from a
> published image, or one you built) or from a phone in setup mode, updated over SSH from Juggle Flash or with
> `juggle update` (A/B slots, going back by itself if a new OS doesn't start properly, hangs or loses power on
> trial), and running one app, deployed from its own repo, which can ask the OS for a few things (its own updates,
> the clock, a restart, setup mode, and a new Wi-Fi network on trial). See the [roadmap](docs/roadmap.md) for the
> design and the steps.

> **New name:** Juggle OS was called ApplPi OS up to v0.6.0, and those releases keep the old names (the
> `applpi-os-*.img.xz` images, the `applpi` command and the `applpi` user). From v0.7.0 it's Juggle OS
> throughout; a Pi with an ApplPi OS release is flashed again rather than updated.

## What you need

- A Raspberry Pi 2 and a microSD card of 4 GB or more (the card is laid out on first boot: two OS slots and a data
  partition filling the rest).
- Windows 10 or 11 with Windows' OpenSSH client (`ssh`, `ssh-keygen`, `scp`; installed by default) and a card
  reader, and either [Juggle Flash](#with-juggle-flash) from a release or the
  [.NET 10 SDK](https://dotnet.microsoft.com/download) to run `juggle` from this repository. Or Linux: see
  [On Linux](#on-linux).
- [Docker Desktop](https://www.docker.com/products/docker-desktop/), only to build the image yourself: the
  [releases](https://github.com/EarthstormSoftware/juggle-os/releases) have ready-made images.

```powershell
git clone https://github.com/EarthstormSoftware/juggle-os
cd juggle-os
dotnet run --project src\Juggle.Cli -- flash latest     # the latest release, with the Pi's settings
```

## Setting up a Pi

Put the card in the PC's card reader and run:

```powershell
dotnet run --project src\Juggle.Cli -- flash
```

It writes the newest image you've built in `deploy\`, or else the latest release (`flash latest` or `flash v0.7.0`
picks a release, downloaded and checked against its `SHA256SUMS`), to a card in a card reader (only card readers
are offered). First it asks for the Pi's name, Wi-Fi and time zone (the default Pi's are offered, see below), and
makes you an SSH key if you don't have one; then you type ERASE to confirm, and only writing the card asks for
administrator rights. It writes and checks the image (only the blocks that hold data), puts the settings on the
card and ejects it safely (`--no-eject` leaves it in). It also gives each Pi its own SSH host key, kept with its
settings and added to your `known_hosts`, so a reflashed Pi keeps its identity and ssh never warns or asks about it.
Boot the Pi with the card and, after a few minutes, `ssh juggle@<name>.local`.

Settings are kept in `%APPDATA%\Juggle`, Wi-Fi passwords and host keys encrypted for your Windows account: never in
this repository, and never in the image. A new Pi starts from the default Pi's settings: the one you chose with
`juggle default <name>` (or Use as default in Juggle Flash), or else the last one set up.

### With Juggle Flash

Juggle Flash does the same in a window, on Windows and Linux (x64): flash a card (the latest release, another
version or a file), set up a card that already has Juggle OS, and the Pis you've set up, each to update, forget or
use as the default. Releases after v0.7.0 have it ready to run: `juggle-flash-<version>-win-x64.zip` and
`-linux-x64.tar.gz`, one program each with `juggle` beside it, as it writes cards with `juggle` (started with
administrator rights for that alone: Windows asks, through UAC). They aren't code-signed, so Windows' SmartScreen
warns the first time. To build them yourself, run `.\src\Juggle.Flash\publish.ps1 -Runtime win-x64` (or `linux-x64`,
on Linux). If your SSH key has a passphrase, add it to ssh-agent (`ssh-add`) for Juggle Flash's updates: it has no
terminal for ssh to ask in.

On Linux, Juggle Flash needs a desktop's usual libraries (fontconfig and X11's) and `pkexec`, which asks for your
password with the desktop's own prompt before writing a card.

### On Linux

From v0.7.0, `juggle` runs on Linux too (x64, or 32-bit ARM such as a Pi). Build it on any machine with the .NET 10
SDK, as one self-contained file that needs no .NET on the Linux machine:

```sh
dotnet publish src/Juggle.Cli -r linux-x64 --self-contained -p:PublishSingleFile=true    # or -r linux-arm
```

It works as on Windows, except that writing a card needs root, so `flash` and `setup` run `juggle` again with
`sudo` for that alone (the settings, SSH key and `known_hosts` stay yours, in your home); card readers are found with `lsblk` (USB
disks with removable media, or an SD slot, never the disk the system runs from); `juggle` mounts the card's boot
partition itself, so no desktop is needed; and settings are kept in `~/.config/juggle`, readable only by you, with
Wi-Fi passwords and host keys not encrypted, as Linux has no per-user key like Windows' to encrypt them with.

### Without juggle: setup mode

A card written by any imager (Raspberry Pi Imager, balenaEtcher) from a release's image, with no settings on it,
starts in setup mode, as a Pico does (from v0.5.0):

1. The Pi starts an open Wi-Fi network, **Juggle-SETUP** (with the Pi's Wi-Fi dongle). Join it from a phone or
   laptop: a phone shows its sign-in page, or open `http://192.168.4.1`. On Ethernet, the page is also at
   `https://juggle-<last 4 digits of the serial number>.local/`.
2. The setup page is HTTPS, so the Wi-Fi password can't be read by anyone listening to the open network. The browser
   warns that it can't check the certificate (the Pi made it itself): it's this Pi if the certificate's SHA-256
   fingerprint is the one on the Pi's screen, and on the plain `http://` page.
3. Fill in the name, Wi-Fi, time zone and your SSH public key (paste it, or choose your `.pub` file), or let the Pi
   make a key for you to download. Save, and the page shows the Pi's own SSH host key, to check the first time SSH
   asks. Restart, and the Pi is at `ssh juggle@<name>.local`.

If Wi-Fi doesn't connect (a new router, a changed password), the Pi comes back to setup mode by itself: by default
after 6 hours without a network (up to a day can be set, as a router can be slow back after a power cut), for 10
minutes, keeping its settings until new ones are saved, then it tries Wi-Fi again. Wi-Fi settings it hasn't connected
with yet, which may be mistyped, get 5 minutes instead. On the setup page this can be switched off, or the times
changed (as `wifi_fallback` in `juggle.json`, see `image\juggle.example.json`). An app can also ask for setup mode, to
rename the Pi, or for a factory reset, and from v0.6.0 it can scan for networks and change the Pi's Wi-Fi without
setup mode: on trial, so if the Pi can't connect within 5 minutes it goes back to the network it had.

## Updating a Pi's OS

Once a Pi runs Juggle OS (v0.2.0 or later), a new image goes on over SSH, with no card reader:

```powershell
dotnet run --project src\Juggle.Cli -- update <name>
```

It sends the newest image in `deploy\` (or the one named after the Pi's name, or a release, as for `flash`) to the
Pi, which writes it into its other OS slot and checks it, while the running OS carries on. Then the Pi restarts into
the new OS once, and keeps it if it starts properly (no failed services, SSH and the network up, within 5 minutes).
If it doesn't (or it hangs, which the hardware watchdog catches), it restarts into the OS it had, and `juggle` says
why. Apps, settings and the host key are on the data partition, so they stay as they were. An update takes about 6
minutes over Wi-Fi.

## Apps

A Pi runs one app, like a Pico runs its firmware: plain Python in its own repo, deployed over SSH. Deploying a
different app replaces the one that was there. [Juggle Hello](https://github.com/EarthstormSoftware/juggle-hello)
is the example, and the place to start a new one:

```powershell
.\deploy.ps1 <name>     # in the app's repo: copies it to the Pi, starts it, and says whether it's running
```

The app is in `/data/app`, with its manifest (`app.json`: name, version, whether it must run as root, and whether
it uses the PWM hardware, as WS2812 LEDs on GPIO 18 do, which turns the onboard audio off while it runs),
`main.py` and its pure-Python libraries in `lib\`. The Pi runs it as the `app` user, which may use port 80 and the
hardware (GPIO, I2C, SPI, the display, touch, serial), restarts it if it stops, and keeps it through OS updates: an
update whose OS stops the app from running goes back. The app keeps what it writes in `/data/app-state`, as the
rest is read-only. On the Pi, `journalctl -u juggle-app` shows its output, and `sudo /usr/lib/juggle/app remove`
removes it.

## The `juggle` command

Run as `dotnet run --project src\Juggle.Cli -- <command>`:

| Command | Does |
|---|---|
| `flash [image]` | Writes an image (a file, or a release: `latest` or a version) to a card in the card reader, checks it, then does what `setup` does. |
| `setup [name]` | Puts a Pi's settings (name, Wi-Fi, time zone, SSH key) on a card that's already written. |
| `update [name] [image] [--yes]` | Updates a Pi's OS over SSH (see above). `--yes` doesn't ask first. |
| `forget <name>` | Forgets a Pi you no longer use: its saved settings, host key and `known_hosts` entry. |
| `--version` | Shows the tool's version. |

## On the Pi

With a monitor connected, the Pi shows the Juggle OS logo while it boots, then a row of icons (one for each CPU core)
above its name, addresses, image version, app, OS slot and last update; an SSH login opens with the same information
beside the icon. The last update shows as **Successful** in green, or **Failed** in orange with the version that was
tried and why it wasn't kept. The screen blanks after 5 minutes without a key pressed. The boot messages are in the journal (`journalctl -b`); to see them on
screen, remove `quiet loglevel=3 splash` from the slot's `cmdline.txt` (`slotA\` or `slotB\`) on the card.

`sudo /usr/lib/juggle/os-update status` shows the slots: which is running, which is permanent, and what each holds.

## Building and testing the image

With Docker Desktop running:

```powershell
.\image\build.ps1          # full build, about 20 minutes: use for releases
.\image\build.ps1 -Quick   # reuses the base system from the last build: about 6 minutes
```

The image (`juggle-os-<version>.img.xz`, with its `.bmap` and package list) ends up in `deploy\`. Its version comes
from the latest git tag (`v0.2.0`, or `v0.2.0-3-g1ad84dc-dirty` between releases) and is recorded with its build
time in `/etc/juggle-release` on the Pi and `juggle-release.txt` on the card. A quick build reuses a base system with
its package lists; after a week or so the mirror has moved on, and a full build is needed.

To test it on a Pi that already runs Juggle OS, `.\image\test\test-pi.ps1 [name]` updates the Pi to the newest image
and checks it over SSH (no failed services, nothing else listening, the read-only root, the slot, the watchdog and
more), in about 6 minutes. The log goes in `deploy\test-pi\`.

To test it without a Pi, `.\image\test\test-qemu.ps1` boots it in QEMU (generic ARM machine and kernel, so not the
Pi's kernel, firmware or Wi-Fi) and runs the same checks, and also tests the first boot: laying out the card and
reading `juggle.json`. The full test (use for releases) then updates the card to the same image and boots the other
slot; `-Quick` boots once, in about 20 minutes; `-Setup` starts with no settings and sets the Pi up from its setup
page. The results go in `deploy\test\`. To check that a failed update goes
back, `.\image\test\broken-image.ps1` makes a copy of an image with a service that always fails.

## In this repository

| Folder | Holds |
|---|---|
| `image\` | The image build: pi-gen settings, our stage (`stage2-juggle\`) and the build scripts. |
| `image\test\` | The QEMU and Pi tests, and their shared checks. |
| `src\Juggle.Cli\`, `src\Juggle.Core\` | `juggle`, the Windows tool. |
| `tests\` | Its unit tests: `dotnet test`. |
| `docs\roadmap.md` | The design (card layout, updates, apps) and the steps. |

## Licence

MIT. See [LICENSE](LICENSE).
