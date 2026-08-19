---
name: ship-release
description: Ship a Claudinine release end to end — dispatch phase A, watch the release PR go green, present the shipping diff for confirmation, merge (which publishes via phase B), and verify the published release. Use when the user wants to release, ship, or publish a new version.
argument-hint: major | minor | patch
---

# Ship a release

Drives the two-phase release flow (`cd.yml` opens a release PR; merging it makes
`cd-publish.yml` tag and publish). The merge is the release gate: **never merge
without the user's explicit confirmation in step 5, and never click or invoke
"Update branch" on a release PR** — a drifted release PR is closed and
re-dispatched, nothing else.

## 1. Preflight

The bump component comes from the arguments (`major`, `minor`, or `patch`).
If missing, show what each would produce and ask which to ship:

```bash
pwsh eng/bump-version.ps1 -Component patch -WhatIf
```

(same for `minor` / `major` — the script reads `gh release list`, local state
does not matter).

Refuse to proceed (report, don't dispatch) if either:
- an open PR with head `release/v*` exists: `gh pr list --json headRefName,number`
- `## Unreleased` in CHANGELOG.md is empty on origin/main — shipping an empty
  release is almost certainly a mistake; ask the user to confirm if they
  really want it.

## 2. Dispatch phase A

```bash
gh workflow run cd.yml -f bump=<component>
```

Then find and watch the run:

```bash
gh run list --workflow=cd.yml --limit 1 --json databaseId,status
gh run watch <run-id> --exit-status
```

On failure: `gh run view <run-id> --log-failed`, report the cause, and check
for staged debris before any retry — a draft release `v<V>` and/or branch
`release/v<V>` may exist. Clean with `gh release delete v<V> --yes` and
`gh api -X DELETE repos/{owner}/{repo}/git/refs/heads/release/v<V>` (only
after confirming with the user), then stop.

## 3. Find the release PR

Phase A's last step prints the PR URL; or:

```bash
gh pr list --search "head:release/v" --json number,title,headRefName
```

The version `<V>` is the branch name after `release/v`.

## 4. Wait for required checks

Poll until BOTH `ci-ok` and `unreleased-notes` report `pass` (do not rely on
`gh pr checks --watch --required` — it returns as soon as the first required
check reports, before `ci-ok` exists):

```bash
gh pr checks <n>
```

`unreleased-notes` passes via the `no-notes` label phase A applied. If `ci-ok`
fails, report the failing job log and stop — the PR stays open for a fix-less
close + re-dispatch after the underlying problem is fixed on main.

## 5. Present the shipping diff and confirm

Show the user, from `gh pr view <n> --json body,files` and the PR diff:
- the version and the changelog section being shipped (the release notes in
  the PR body),
- the four files changed (must be exactly `plugin.json`, `Claudinine.csproj`,
  `CHANGELOG.md`, `marketplace.json`),
- the zip sha256 recorded in the body.

Then ask explicitly: **"Ship v\<V\>?"** (AskUserQuestion is fine). This
confirmation IS the release decision.

- On **no**: ask whether to leave the PR open (note: any other merge to main
  will invalidate it — strict up-to-date) or close and clean up (close the PR,
  delete the draft release and the branch).
- On **yes**:

```bash
gh pr merge <n> --squash
```

(the repo auto-deletes the branch on merge; any merge method works.)

## 6. Watch phase B and verify

```bash
gh run list --workflow=cd-publish.yml --limit 1 --json databaseId,status
gh run watch <run-id> --exit-status
```

On success, verify and report:
- `gh release view v<V> --json isDraft,isImmutable,assets,url` — published,
  immutable, exactly two assets;
- the tag points at the merge commit:
  `git ls-remote origin "refs/tags/v<V>^{}"` vs the PR's `mergeCommit`;
- the pin in `.claude-plugin/marketplace.json` on origin/main equals the
  `zip sha256` recorded in the PR body.

Report the release URL. On failure: `gh run view <run-id> --log-failed`; the
run's own error messages name the recovery (phase B is re-runnable — the
draft and tag steps are idempotent). Never edit the published release by hand.
