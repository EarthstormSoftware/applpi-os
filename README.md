# ApplPi OS

A frozen, minimal OS image that turns older Raspberry Pis (the Pi 2 first) into appliances: as little to look after
as a Pico W, with Python, the network, USB and GPIO.

- **Frozen:** each release is one image. The root filesystem is read-only, so pulling the power is harmless, and
  nothing updates by itself. To change the OS, build a new image and install it.
- **Safe updates:** a new image goes on over SSH into a second OS slot, and the Pi tries it once. It keeps it only if
  it starts properly, and otherwise goes back to the OS it had, by itself.
- **Minimal:** Raspberry Pi OS Lite with no desktop, no Bluetooth and no automatic updates. Nothing listens except
  SSH (by key only), mDNS and the app you deploy.
- **Apps on their own:** one app per Pi, like a Pico's firmware: plain Python, kept on a separate data partition and
  deployed from its own repo over SSH. Installing a new OS image leaves it alone.
- **No serial cable or screen needed:** the card is flashed from Windows with the Pi's name, Wi-Fi and SSH key, so it
  comes up on the network ready for SSH.

> **Status:** v0.3.0 runs on the Pi 2: set up with `applpi flash`, updated over SSH with `applpi update` (A/B
> slots, going back by itself if a new OS doesn't start properly), and running one app, deployed from its own repo.
> Next: the flashing app and a check of the Pi at rest. See the [roadmap](docs/roadmap.md)
> for the design and the steps.

## What you need

- A Raspberry Pi 2 and a microSD card of 4 GB or more (the card is laid out on first boot: two OS slots and a data
  partition filling the rest).
- Windows 10 or 11 with the [.NET 10 SDK](https://dotnet.microsoft.com/download), Windows' OpenSSH client (`ssh`,
  `ssh-keygen`, `scp`; installed by default) and a card reader.
- [Docker Desktop](https://www.docker.com/products/docker-desktop/), to build the image (there are no ready-made
  images yet).

```powershell
git clone https://github.com/EarthstormSoftware/applpi-os
cd applpi-os
.\image\build.ps1                                # the image, in deploy\: about 20 minutes
dotnet run --project src\ApplPi.Cli -- flash     # write it to a card, with the Pi's settings
```

## Setting up a Pi

Put the card in the PC's card reader and run:

```powershell
dotnet run --project src\ApplPi.Cli -- flash
```

It writes the newest image in `deploy\` to the card (only card readers are offered, and you type ERASE to confirm)
and checks it, writing only the blocks that hold data. Then it asks for the Pi's name, Wi-Fi and time zone (the last
Pi's are offered), makes you an SSH key if you don't have one, and puts the settings on the card. It also gives each
Pi its own SSH host key, kept with its settings and added to your `known_hosts`, so a reflashed Pi keeps its identity
and ssh never warns or asks about it. Boot the Pi with the card and, after a few minutes, `ssh applpi@<name>.local`.

Settings are kept in `%APPDATA%\ApplPi`, Wi-Fi passwords and host keys encrypted for your Windows account: never in
this repository, and never in the image.

## Updating a Pi's OS

Once a Pi runs ApplPi OS (v0.2.0 or later), a new image goes on over SSH, with no card reader:

```powershell
dotnet run --project src\ApplPi.Cli -- update <name>
```

It sends the newest image in `deploy\` (or the one named after the Pi's name) to the Pi, which writes it into its
other OS slot and checks it, while the running OS carries on. Then the Pi restarts into the new OS once, and keeps it
if it starts properly (no failed services, SSH and the network up, within 5 minutes). If it doesn't (or it hangs,
which the hardware watchdog catches), it restarts into the OS it had, and `applpi` says why. Apps, settings and the
host key are on the data partition, so they stay as they were. An update takes about 6 minutes over Wi-Fi.

## Apps

A Pi runs one app, like a Pico runs its firmware: plain Python in its own repo, deployed over SSH. Deploying a
different app replaces the one that was there. [ApplPi Hello](https://github.com/EarthstormSoftware/applpi-hello)
is the example, and the place to start a new one:

```powershell
.\deploy.ps1 <name>     # in the app's repo: copies it to the Pi, starts it, and says whether it's running
```

The app is in `/data/app`, with its manifest (`app.json`: name, version, and whether it must run as root),
`main.py` and its pure-Python libraries in `lib\`. The Pi runs it as the `app` user, which may use port 80 and the
hardware (GPIO, I2C, SPI, the display, touch, serial), restarts it if it stops, and keeps it through OS updates: an
update whose OS stops the app from running goes back. The app keeps what it writes in `/data/app-state`, as the
rest is read-only. On the Pi, `journalctl -u applpi-app` shows its output, and `sudo /usr/lib/applpi/app remove`
removes it.

## The `applpi` command

Run as `dotnet run --project src\ApplPi.Cli -- <command>`:

| Command | Does |
|---|---|
| `flash [image]` | Writes an image to a card in the card reader, checks it, then does what `setup` does. |
| `setup [name]` | Puts a Pi's settings (name, Wi-Fi, time zone, SSH key) on a card that's already written. |
| `update [name] [image] [--yes]` | Updates a Pi's OS over SSH (see above). `--yes` doesn't ask first. |
| `forget <name>` | Forgets a Pi you no longer use: its saved settings, host key and `known_hosts` entry. |
| `--version` | Shows the tool's version. |

## On the Pi

With a monitor connected, the Pi shows the ApplPi OS logo while it boots, then its name, addresses, image version,
app, OS slot and last update; an SSH login opens with the same information beside the apple. The last update shows as
**Successful** in green, or **Failed** in orange with the version that was tried and why it wasn't kept. The screen
blanks after 5 minutes without a key pressed. The boot messages are in the journal (`journalctl -b`); to see them on
screen, remove `quiet loglevel=3 splash` from the slot's `cmdline.txt` (`slotA\` or `slotB\`) on the card.

`sudo /usr/lib/applpi/os-update status` shows the slots: which is running, which is permanent, and what each holds.

## Building and testing the image

With Docker Desktop running:

```powershell
.\image\build.ps1          # full build, about 20 minutes: use for releases
.\image\build.ps1 -Quick   # reuses the base system from the last build: about 6 minutes
```

The image (`applpi-os-<version>.img.xz`, with its `.bmap` and package list) ends up in `deploy\`. Its version comes
from the latest git tag (`v0.2.0`, or `v0.2.0-3-g1ad84dc-dirty` between releases) and is recorded with its build
time in `/etc/applpi-release` on the Pi and `applpi-release.txt` on the card. A quick build reuses a base system with
its package lists; after a week or so the mirror has moved on, and a full build is needed.

To test it on a Pi that already runs ApplPi OS, `.\image\test\test-pi.ps1 [name]` updates the Pi to the newest image
and checks it over SSH (no failed services, nothing else listening, the read-only root, the slot, the watchdog and
more), in about 6 minutes. The log goes in `deploy\test-pi\`.

To test it without a Pi, `.\image\test\test-qemu.ps1` boots it in QEMU (generic ARM machine and kernel, so not the
Pi's kernel, firmware or Wi-Fi) and runs the same checks, and also tests the first boot: laying out the card and
reading `applpi.json`. The full test (use for releases) then updates the card to the same image and boots the other
slot; `-Quick` boots once, in about 20 minutes. The results go in `deploy\test\`. To check that a failed update goes
back, `.\image\test\broken-image.ps1` makes a copy of an image with a service that always fails.

## In this repository

| Folder | Holds |
|---|---|
| `image\` | The image build: pi-gen settings, our stage (`stage2-applpi\`) and the build scripts. |
| `image\test\` | The QEMU and Pi tests, and their shared checks. |
| `src\ApplPi.Cli\`, `src\ApplPi.Core\` | `applpi`, the Windows tool. |
| `tests\` | Its unit tests: `dotnet test`. |
| `docs\roadmap.md` | The design (card layout, updates, apps) and the steps. |

## Licence

MIT. See [LICENSE](LICENSE).
