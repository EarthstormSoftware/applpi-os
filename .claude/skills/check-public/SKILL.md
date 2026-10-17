---
name: check-public
description: Check uncommitted changes and unpushed commits for personal details before they reach the public repo (Wi-Fi, keys, hostnames, IPs, usernames, serials). Use before committing or pushing.
argument-hint: "[git range, default: unpushed commits plus the working tree]"
---

This repository is public. Check that nothing personal is about to be committed or pushed.

What to check: $ARGUMENTS if a git range is given; otherwise the working tree (`git diff HEAD`, plus untracked files
from `git status --porcelain`) and unpushed commits (`git log @{u}..HEAD -p`, or every commit if there's no upstream).
Include commit messages, not just file contents.

Use the `public-repo-guard` agent to do the review and give it the diffs or the commands to produce them. Then report
its findings: for each one the file, the line and what it looks like. If nothing turns up, say so in one line.

Never fix a finding in a commit that has been pushed yourself. For unpushed commits, suggest how to fix it (amend or
rebase) and wait for the user to decide. Rewriting history is theirs to choose.
