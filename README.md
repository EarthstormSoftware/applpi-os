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

> **Status:** planning. See the [roadmap](docs/roadmap.md) for the design and the steps.

## Licence

MIT. See [LICENSE](LICENSE).
