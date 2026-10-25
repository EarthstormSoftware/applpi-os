# ApplPi OS

A frozen, minimal OS image that turns older Raspberry Pis (the Pi 2 first) into appliances: as little to look after
as a Pico W, with Python, the network, USB and GPIO.

- **Frozen:** each release is one image. The root filesystem is read-only, so pulling the power is harmless, and
  nothing updates by itself. To change the OS, build a new image and install it.
- **Minimal:** Raspberry Pi OS Lite with no desktop, no Bluetooth and no automatic updates. Nothing listens except
  SSH (by key only), mDNS and the apps you deploy.
- **Apps on their own:** apps are plain Python, kept on a separate data partition and deployed from their own repos
  over SSH. Installing a new OS image leaves them alone.
- **No serial cable or screen needed:** the card is flashed from Windows with the Pi's name, Wi-Fi and SSH key, so it
  comes up on the network ready for SSH.

> **Status:** v0.1.1 runs on the Pi 2 and is set up with `applpi flash`. Next: OS updates over SSH (A/B slots). See
> the [roadmap](docs/roadmap.md) for the design and the steps.

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

To change the settings on a card that's already written: `dotnet run --project src\ApplPi.Cli -- setup`. Settings
are kept in `%APPDATA%\ApplPi`, Wi-Fi passwords and host keys encrypted for your Windows account.

## Updating a Pi's OS

Once a Pi runs ApplPi OS (from the first version with A/B slots), a new image goes on over SSH, with no card reader:

```powershell
dotnet run --project src\ApplPi.Cli -- update <name>
```

It sends the newest image in `deploy\` (or the one named after the Pi's name) to the Pi, which writes it into its
other OS slot and checks it, while the running OS carries on. Then the Pi restarts into the new OS once; if it comes
back on it, `applpi` keeps it. If it doesn't (power it off and on if it's stuck), it goes back to the OS it had. Apps,
settings and the host key are on the data partition, so they stay as they were.

With a monitor connected, the Pi shows the ApplPi OS logo while it boots, then its name, addresses, image version and
build time; an SSH login opens with the same information beside the apple. The screen blanks after 5 minutes without
a key pressed. The boot messages are in the journal
(`journalctl -b`); to see them on screen, remove `quiet loglevel=3 splash` from `slotA\cmdline.txt` on the card.

## Building the image

With Docker Desktop running:

```powershell
.\image\build.ps1          # full build, about 20 minutes: use for releases
.\image\build.ps1 -Quick   # reuses the base system from the last build: about 6 minutes
```

The image (`applpi-os-<version>.img.xz`, with its `.bmap` and package list) ends up in `deploy\`. Its version comes
from the latest git tag (`v0.1.0`, or `v0.1.0-3-g1ad84dc-dirty` between releases) and is recorded with its build
time in `/etc/applpi-release` on the Pi and `applpi-release.txt` on the card.

To test it without a Pi, `.\image\test\test-qemu.ps1` boots it in QEMU (generic ARM machine and kernel, so not the
Pi's kernel, firmware or Wi-Fi) and checks first boot, the settings, SSH and the read-only root. The full test (use
for releases) then updates the card to the same image and boots the other slot; `-Quick` boots once, in about 20
minutes. The results go in `deploy\test\`.

## Licence

MIT. See [LICENSE](LICENSE).
