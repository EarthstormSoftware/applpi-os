# Project Guidelines

## Purpose
ApplPi OS builds a frozen, minimal OS image for older Raspberry Pis (Pi 2 first, 32-bit armhf) and the Windows
tools that flash it, set up each Pi and update its OS over SSH. Apps live in their own repos and are deployed to the
Pi's data partition; this repo only defines what they can rely on (the app service, the manifest, the libraries in
the image).

## Architecture
- Design, card layout and steps: `docs/roadmap.md`. Keep it up to date when a decision changes.
- The image is Raspberry Pi OS Lite (Trixie, armhf) built with pi-gen, by GitHub Actions or locally with Docker.
- Root filesystem read-only; per-Pi state (settings, Wi-Fi, SSH host keys, apps) lives on the data partition.
- Two OS slots (A/B), chosen by `os_prefix` in `config.txt`; an update only writes the inactive slot.
- Windows tools: `applpi`, a .NET command line, later with a WinUI flashing app on the same code.

## Rules
- Keep the image minimal: every package and enabled service needs a reason. Nothing listens on the network except
  SSH (key only), mDNS and apps.
- No automatic updates or timers that change the system.
- Compiled libraries belong in the image; apps are plain Python.
- Flashing must only ever offer removable USB disks, show their size and name, and confirm before writing.
- Shell scripts that run on the Pi or in the build use LF line endings.

## Public Repository Rules
- This repository is public. Never commit personal setup details: Wi-Fi names or passwords, SSH keys, hostnames,
  IP addresses, usernames or serial numbers. Images must not contain them either: they are added per Pi at flashing.
- Per-device settings live in `%APPDATA%\ApplPi`. Only `*.example.json` files are committed.
- Commit messages use Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`).
