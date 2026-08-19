# Work order — retire the release write-back: stable `latest` URL + release PR

**Status (2026-08-19): design approved, not started.** Goal: CD stops writing to
`main` entirely. No publish App bypass of the `main-protect` ruleset, no release
commit pushed by a machine, no per-release `marketplace.json` rewrite. The
marketplace pointer becomes a permanent URL; the only per-release human action is
merging a release PR.

Context: today's CD (`.github/workflows/cd.yml`) writes exactly one commit per
release directly to `main` (version stamps, marketplace pin, changelog promotion)
and pushes it with a GitHub App because the ruleset requires PRs and
`github-actions[bot]` cannot be named as a bypass actor. That works — the header
comment in `cd.yml` records the failure modes it has survived — but the bypass is
setup we maintain only to let a machine around a rule that exists for humans.

---

## Key facts (all verified 2026-08-19)

1. **Update detection is version-based, not pointer-based.** Claude Code resolves
   a plugin's version from the first of: (1) `version` in the fetched source's
   `plugin.json`, (2) `version` in the marketplace entry, (3) the resolved commit
   SHA for git sources / the sha256 pin-or-digest for `archive` sources.
   `/plugin update` and auto-update compare the resolved version against the
   installed one. (Claude Code docs, *Plugins reference → Version management*.)
2. **The zip already carries the update signal.** `eng/pack-plugin.ps1` reads the
   version from the manifest; `build.yml` stamps it into the runner tree before
   packing and asserts at `build.yml:225-228` that the packed binary reports it.
   A moving pointer therefore needs nothing new to trigger updates: each release
   resolves to a new version string.
3. **GitHub serves a stable "latest" asset URL.** `HEAD
   https://github.com/vdebellabre/claudinine/releases/latest/download/claudinine-1.2.0.zip`
   returns `302 → .../releases/download/v1.2.0/...` — `releases/latest` resolves
   to the newest non-draft, non-prerelease release. Claude Code's `archive`
   source follows redirects and validates every hop (docs: *Plugin sources*).
   Requirement: the asset **name** must be stable across releases.
4. **The archive is ~10 MB** (`claudinine-1.2.0.zip` = 9,955,477 bytes). That is
   the per-update-check download cost of design point 6 below — periodic
   (auto-update cadence), not per session.
5. **Consumer floor unchanged:** `archive` sources need Claude Code v2.1.224+
   (currently recorded in `eng/set-archive-source.ps1`'s header; that note moves,
   the script goes).

---

## Decision

### Target marketplace entry (written once, never touched again)

```json
"source": {
  "source": "archive",
  "url": "https://github.com/vdebellabre/claudinine/releases/latest/download/claudinine-latest.zip"
}
```

No `sha256` pin: the pin changes per release, so it *is* the write-back. With no
pin and `version` declared in `plugin.json`, the version string is the update
signal (fact 1) and the digest fallback never engages.

### Target release flow

**Phase A — open the release PR** (`cd.yml`, `workflow_dispatch` with the existing
`bump: [patch, minor, major]` input):

1. Compute the version (`eng/bump-version.ps1 -Component <bump> -WhatIf`) — unchanged.
2. Refuse to republish — the existing release-and-tag checks, unchanged.
3. Refuse to race: fail if a `release/v*` branch or an open release PR already exists.
4. Create branch `release/v<version>` from the dispatch SHA; run
   `eng/set-version.ps1` and `eng/release-notes.ps1 -Version <v> -Promote`
   (still fail-closed on an empty `Unreleased` section); commit `Release <v>`.
5. Open the PR **with the minted publish-App token**, labeled `no-notes`
   (promotion reopens an empty `Unreleased` slot, which `changelog-gate.yml`
   would otherwise reject). PR body: the rendered release notes.

The human merge is the release gate. The diff is exactly the changelog that
ships plus two version stamps — a meaningfully better review surface than today's
post-hoc direct push.

**Phase B — publish on merge** (new `cd-publish.yml`,
`on: pull_request: types: [closed]`, guarded by `merged == true` and head branch
`release/v*`):

1. Read the version from `.claude-plugin/plugin.json` at
   `github.event.pull_request.merge_commit_sha`; fail if it disagrees with the
   branch name (a human edit in flight must stay coherent).
2. Re-run the republish check (guards re-runs after success).
3. Build via `./.github/workflows/build.yml` with `ref: <merge_commit_sha>` and
   **no `version` input** — the merged tree carries the version, the packer reads
   it from the manifest. The uncommitted-override dance disappears.
4. Tag `v<version>` at the merge commit; push the tag with `GITHUB_TOKEN`
   (`contents: write`; tags need no bypass and no PR).
5. `gh release create v<version>` with three assets:
   `claudinine-<v>.zip`, `claudinine-<v>.plugin`, and the same zip bytes again
   under the stable name — `gh release upload` renames with
   `file#claudinine-latest.zip`. Release notes from `eng/release-notes.ps1`,
   keeping the digest line (now informational, see trade-offs).

**Consequences:** CD is read-only on `main`. The `main-protect` ruleset loses its
bypass actor; the App is demoted from bypass-pusher to ordinary PR author (it
keeps its secrets, gains `pull_requests: write`). `marketplace.json` becomes a
static file; the "between releases the manifest keeps the previous release's
values" invariant is deleted — the pointer is correct by construction.

### Why the App survives at all (and only for PR creation)

PRs created with the workflow's `GITHUB_TOKEN` do **not** trigger other workflow
runs (GitHub's recursive-run prevention), so a `GITHUB_TOKEN` release PR would
never get its two required checks and could never merge under the ruleset. An
App-token PR triggers CI normally. Branch push in phase A uses the same App token
for symmetry. Phase B needs no App: it reacts to a human merge and only pushes a
tag and creates a release.

### What stays unchanged

- `changelog-gate.yml` and the per-PR `Unreleased` notes discipline (the release
  PR uses the existing `no-notes` escape hatch; nothing new).
- The six-RID matrix, pack/verify steps, `.plugin` hosted bundle.
- `eng/bump-version.ps1`, `eng/release-notes.ps1`, `eng/set-version.ps1`,
  `eng/pack-plugin.ps1`.
- Fail-closed ordering: everything fallible (version computation, republish and
  race checks, empty-notes check, build, pack) happens before the first
  irreversible step, which is now `gh release create` — same as today. Phase A's
  writes (branch + PR) are fully recoverable: close the PR, delete the branch.

---

## Trade-offs accepted

1. **The sha256 pin is gone.** Install-time integrity rests on HTTPS + GitHub's
   asset storage instead of a digest committed to git. The digest still prints in
   the release notes for manual verification. The pin's threat model on a single
   GitHub host was narrow (an actor who can swap the asset can usually rewrite
   the repo too), but it did catch a mis-uploaded wrong zip; that class is now
   caught only by the version assertions in `build.yml`, which verify version
   match, not content match.
2. **Every update check downloads the ~10 MB zip** to read its version (fact 4) —
   there is no local pin to compare against. Periodic, not per session.
3. **Rollback = publish a new release of the old content.** `releases/latest` is
   newest-by-date; there is no first-class "point the marketplace at v1.1.0".
   Acceptable: no multi-version requirement. Side benefit: `latest` ignores
   prereleases, so a future prerelease channel cannot hijack the pointer.
4. **Between PR merge and publish, `main`'s manifest names an unreleased
   version.** Window is minutes (merge → CD run); a dev build from `main` in that
   window reports the upcoming version. Harmless, recorded here so it surprises
   nobody.

---

## Transition order (load-bearing — do not reorder)

The stable URL 404s until some release carries a `claudinine-latest.zip` asset,
so the asset must exist **before** the marketplace entry switches:

1. Land the code changes (W2–W5 below) as one PR. Merging it changes nothing
   observable: `cd.yml` is replaced but no release runs on merge.
2. Attach the stable-named asset to the **existing** release:
   `gh release upload v1.2.0 "claudinine-1.2.0.zip#claudinine-latest.zip"`
   (same bytes as the versioned asset; no rebuild — re-packing would produce
   different bytes, which is fine now but pointless).
3. Verify the URL resolves: `curl -sI
   https://github.com/vdebellabre/claudinine/releases/latest/download/claudinine-latest.zip`
   → `302` → v1.2.0 asset → `200`.
4. Only then merge the marketplace.json switch (W1). Existing users keep their
   old entry until their next `/plugin marketplace update`; they then pick up the
   static entry, and the resolved version equals what they have → no spurious
   update. From the next release on, phase B uploads both asset names.

If a release is dispatched between steps 1 and 4, phase B publishes it correctly
but the marketplace entry still points at the old versioned URL — users lag by
one release until W1 merges. Tolerable; avoid by doing steps 1–4 in one sitting.

---

## Work items

### W1 — marketplace.json becomes static

`.claude-plugin/marketplace.json`: replace the plugin entry's `source` with the
target object above (URL only, no `sha256`). Ordinary PR, needs its own
`Unreleased` changelog entry (changelog-gate). Merge only after transition step 3.

### W2 — split cd.yml into PR-opener and publisher

- `cd.yml` becomes phase A only (dispatch → release PR). Rewrite the header
  comment: the branch model paragraph, the ordering paragraph, and the App
  justification all change (the App is now a PR author, not a bypass actor).
  Delete: the app-token push plumbing tied to direct push, the `Commit bump and
  pin` step, the `Push release commit and tag to main` step, the
  `set-archive-source.ps1` call. Keep: version computation, republish checks
  (plus the new race check), App-token minting.
  `permissions: { contents: write, pull-requests: write }`.
- New `cd-publish.yml`, phase B as specified above.
  `permissions: { contents: write }`. Trigger guard:
  `github.event.pull_request.merged == true && startsWith(github.event.pull_request.head.ref, 'release/v')`.
- `build.yml`: no changes. Its `version` input simply stops being passed by CD;
  update its header comment where it describes the uncommitted-override trick.

### W3 — demote the App, delete the bypass

- `main-protect` ruleset: remove the publish App from bypass actors (keep
  deletion/force-push block, PR requirement, the two required checks).
- App installation permissions: confirm `contents: write`, add
  `pull_requests: write`.
- Keep `PUBLISH_APP_ID` / `PUBLISH_APP_PEM` secrets (phase A still mints).

### W4 — delete dead machinery

- Delete `eng/set-archive-source.ps1` (its only job was the per-release pin
  rewrite). Salvage its consumer-floor note (Claude Code v2.1.224+) into
  `cd.yml`'s header comment first.
- `eng/set-version.ps1`: update the header comment — it is now called once, by
  phase A; the "cd.yml calls it again to make the write permanent" paragraph is
  stale.

### W5 — documentation

- `CHANGELOG.md` preamble: "CD renames that heading to the computed version in
  the release commit" → "in the release PR"; "fails the dispatch before anything
  is built or pushed" → "before any PR is opened".
- The switchover PR's own `Unreleased` entry describes the new model (this
  change is user-visible in the sense that the 1.2.0 entry's "Single-branch
  releases" paragraph is now superseded).

### W6 — verification

1. Transition steps 2–3 pass (asset attached, URL 302→200).
2. Scratch-profile install after W1 merges: `/plugin marketplace add
   vdebellabre/claudinine`, install `claudinine` → resolves via the `latest`
   URL, `claudinine version` matches the current release.
3. Dispatch the next release: PR opens with `no-notes`, both required checks
   run and pass, merge triggers phase B, release appears with three assets
   (both zip names byte-identical — compare digests).
4. **The core claim:** after that release, `/plugin update` in the scratch
   profile (which still has the previous version) offers the new one. This is
   the test the whole design stands or falls on.
5. `.plugin` bundle still uploads to a Cowork account unchanged.
6. Ruleset audit: no bypass actors remain on `main-protect`; a direct push to
   `main` with an admin token is refused.

---

## Failure modes and edge cases

- **Release PR closed without merging:** nothing shipped, tree untouched.
  Re-dispatch; delete the stale `release/v*` branch first (the race check
  refuses while it exists). Optional nicety: a cleanup step on `pull_request
  closed && !merged`.
- **main moves between dispatch and branch push:** phase A's push creates the
  branch from the dispatch SHA; the PR then shows the gap. Same discipline as
  today's fast-forward rule: close, re-dispatch. Do not rebase — the PR should
  be reviewed against the tree it was computed from.
- **Two dispatches racing:** the race check (open PR / branch exists) plus the
  republish check cover it; the second dispatch fails before writing anything.
- **Phase B fails after merge:** re-run it. Nothing is published until
  `gh release create` succeeds; the tag push is idempotent-on-failure in the
  same sense as today (tag exists but no release → the republish check names it
  as debris; delete tag, re-run). The zip is not byte-reproducible across
  builds, but with no pin to reconcile, a re-run simply publishes the freshly
  packed bytes — strictly simpler than today's asset-reattachment story.
- **Human edits the release PR in flight:** allowed and reviewed like any PR.
  Phase B's manifest-vs-branch-name check fails loudly if the edit changed the
  version; anything else (changelog wording) flows through untouched.
- **Merge method:** squash, merge, or rebase all work — phase B tags
  `merge_commit_sha`, which exists for all three.

## Out of scope (revisit only if the trade-offs bite)

- **Conservative variant:** keep the versioned URL + sha256 pin and replace only
  the direct push with a PR (pipeline opens the release commit as a PR, verifies
  main hasn't moved, merges with the App token, then tags + publishes). Kills
  the bypass but keeps one machine write per release through the gate. Relevant
  only if the lost pin (trade-off 1) turns out to matter.
- **Separate marketplace repo:** only relevant if this repo ever needs the
  manifest to move faster than the source or to be machine-writable without PRs
  at all. Not needed under this design — the manifest is static.
