---
name: test-image
description: Boot a built ApplPi OS image in QEMU (image\test\test-qemu.ps1) and report what passed and failed. Use after building an image.
argument-hint: "[quick] [image path]"
---

Test an ApplPi OS image in QEMU. Arguments: $ARGUMENTS (`quick` boots once, about 20 minutes; the full test boots
twice, about 35 minutes, and is needed for releases. An image path picks the image; the default is the newest in
`deploy\`.)

1. Check Docker is running. If not, ask the user whether to start Docker Desktop, as in `/build-image`.
2. Say which image will be tested and how long it takes.
3. Run `.\image\test\test-qemu.ps1` (with `-Quick` and/or `-Image <path>`) in PowerShell, in the background, with its
   output redirected to a log in the scratchpad directory. Wait for the completion notification; don't poll.
4. The script copies its results to `deploy\test\` (`summary.log`, `checks-N.log`, `console-N.log`) and then deletes
   the `applpi-os-test` volume, so read them there. If the user's window closed mid-test, the container keeps running:
   wait for it to finish (`docker ps`) and read `deploy\test\` afterwards. From Git Bash, put `MSYS_NO_PATHCONV=1`
   before any `docker run -v vol:/work ...`, or `/work` becomes a Windows path.
5. Report from `summary.log` and the checks logs: SSH up, system state and any failed units, listening ports (only
   SSH, mDNS and apps are allowed), the read-only root, the login screen, root SSH refused, and on boot 2 the same host
   key and the root's changes gone. For a failure, quote the lines from the checks log and the console log around it.
6. Remind the user that QEMU doesn't use the Pi's kernel, firmware or Wi-Fi: the boot logo, Wi-Fi and the screen
   still need checking on the real Pi.
