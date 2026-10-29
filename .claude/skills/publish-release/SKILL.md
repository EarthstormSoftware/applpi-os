---
name: publish-release
description: Publish a tagged ApplPi OS release - check the draft GitHub Actions made, write its notes, publish it on approval, then update the website. Use after /release, once main and the tag are pushed.
argument-hint: "<version, e.g. v0.6.0>"
disable-model-invocation: true
---

Publish ApplPi OS $ARGUMENTS. `/release` has tagged it and tested the local build, and the user has pushed `main` and
the tag: the tag starts `.github/workflows/image.yml`, which builds the image again and makes a draft release with
`applpi-os-<version>.img.xz`, its `.bmap` and `SHA256SUMS`.

1. **The draft.** Find the tag's run (`gh run list --workflow image.yml --limit 3`) and wait for it with
   `gh run watch <id> --exit-status` in the background (a build takes about half an hour). On failure, report the
   failing step (`gh run view <id> --log-failed`) and stop. Then `gh release view <version> --json isDraft,assets`:
   a draft with those three files.
2. **Check what will be downloaded.** Download the draft's files into a new folder in the scratchpad
   (`gh release download <version> --dir <folder>`), check them with `sha256sum -c SHA256SUMS`, and run
   `/test-image quick` on that image: the published image is GitHub's build, not the one tested locally.
3. **The notes**, in the layout of the last release's (`gh release view <last tag> --json body`):
   - a line on what the images are for, and the `applpi flash <version>` and `applpi update <pi> <version>` commands
   - **Based on**: Raspberry Pi OS Lite, its Debian release, pi-gen's release tag (`image\config`), the kernel and
     Python versions (from the image's `.info` in `deploy\`)
   - **What's new**, from the commits since the last tag (`git log <last tag>..<version>`), written for people using
     the Pis, not as a list of commits
   - **Tested**: what QEMU checked and what was checked on the real Pi 2 (from this conversation or the roadmap)
   Write them to a file in the scratchpad. Have the `public-repo-guard` agent check them: no Pi names, networks,
   addresses or keys. Show the user the notes and wait for their approval.
4. **Publish**, once approved: `gh release edit <version> --notes-file <file> --draft=false --latest`. Check
   `gh release view <version>` shows it published and as the latest: `applpi flash latest` and `applpi update` now
   offer it.
5. **The website** (`E:\Projects\EarthstormSoftware\website`, its own repo and skills): on a new branch, update the
   ApplPi OS card in `src/content/pages/home.md` and the section in `src/content/pages/projects.md` for what's new,
   with `npm run check`. Then its `/preview` makes the pull request and a preview. The user looks at the preview;
   only they say when to run its `/publish`, which merges it.
6. Update the project's notes (memory) with the release, and say what's left.

Never push, publish or merge without the user's go-ahead at that step.
