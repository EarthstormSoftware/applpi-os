---
name: release
description: Release a new ApplPi OS version - status docs, annotated tag, full image build and full QEMU test.
argument-hint: "<version, e.g. v0.1.2>"
disable-model-invocation: true
---

Release ApplPi OS $ARGUMENTS.

Before starting, check all of these and stop if one fails:
- The version is `vX.Y.Z`, it's newer than the last tag (`git tag --sort=-v:refname`), and it isn't already tagged.
- The working tree is clean and you're on `main`.
- The work since the last tag has been checked on the real Pi 2. Ask the user if this conversation doesn't show it:
  QEMU can't check the boot logo, Wi-Fi or the screen.

Then:
1. Run `/check-public` on the commits since the last tag. Stop if it finds anything.
2. Update the version in the status line of `README.md`, and in `docs/roadmap.md` if a step is now done. Commit it as
   `docs: <version> ...`.
3. Make an annotated tag, like the earlier ones (`git tag -n5 <last tag>`): `ApplPi OS <version>: <what's new, in a
   few words>`.
4. Build the image in full with `/build-image` (not quick). Check the image is called `applpi-os-<version>.img.xz`
   with no `-dirty`.
5. Run the full QEMU test with `/test-image` (not quick) on that image.
6. Report the tag, the image and the test results. Don't push: ask the user whether to push `main` and the tag
   (`git push origin main <version>`). After the push, `/publish-release <version>` takes it from GitHub's draft to
   the published release and the website.
