---
name: build-image
description: Build the ApplPi OS image with Docker (image\build.ps1) and report the version, size and package changes. Use when the image needs (re)building.
argument-hint: "[quick]"
---

Build the ApplPi OS image. Arguments: $ARGUMENTS (`quick` reuses pi-gen's stages 0 and 1: about 6 minutes instead
of 20; never use it for a release).

1. Check Docker is running (`docker info --format '{{.ServerVersion}}'`). If it isn't, ask the user whether to start
   Docker Desktop (`Start-Process "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"`), then wait until
   `docker info` succeeds.
2. Note `git describe --tags --match 'v[0-9]*' --always --dirty`: that's the version the image gets. Say so if it ends
   in `-dirty` (uncommitted changes) so the user isn't surprised.
3. Run `.\image\build.ps1` (add `-Quick` for `quick`) from the repo root in PowerShell, in the background, with its
   output redirected to a log in the scratchpad directory. Wait for the completion notification; don't poll.
4. On failure, read the end of the log and `deploy\build.log`, find the first real error (pi-gen errors often come
   well before the last lines) and report it.
5. On success, report:
   - the image `deploy\applpi-os-<version>.img.xz`, its size, and that its `.bmap` is there
   - packages added or removed, by comparing the new `deploy\applpi-os-<version>.info` package list with the previous
     image's `.info`. Every added package needs a reason (see the project rules), so point out any that weren't asked for.
6. Suggest `/test-image` next.
