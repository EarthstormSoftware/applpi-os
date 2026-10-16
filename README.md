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

> **Status:** building the first image. See the [roadmap](docs/roadmap.md) for the design and the steps.

## Setting up a Pi

Put the card in the PC's card reader and run:

```powershell
dotnet run --project src\ApplPi.Cli -- flash
```

It writes the newest image in `deploy\` to the card (only card readers are offered, and you confirm first), checks
it, then asks for the Pi's name, Wi-Fi and time zone, makes an SSH key if you don't have one, and puts the settings
on the card. Then boot the Pi with it and, after a few minutes, `ssh applpi@<name>.local`.

To change the settings on a card that's already written: `dotnet run --project src\ApplPi.Cli -- setup`.

## Building the image

With Docker Desktop running: `.\image\build.ps1`. The image ends up in `deploy\`.

To test it without a Pi: `.\image\test\test-qemu.ps1` boots it twice in QEMU (generic ARM machine and kernel, so not
the Pi's kernel, firmware or Wi-Fi) and checks first boot, the settings, SSH and the read-only root. It takes about
45 minutes; the results go in `deploy\test\`.

## Licence

MIT. See [LICENSE](LICENSE).
