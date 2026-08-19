# Work order — retire the release write-back: pin-in-release-PR

**Status (2026-08-19): v2 design approved, not started** — except two settings
already flipped (see *Already done* below). Supersedes v1 of this document
(static `latest` URL, formerly `release-latest-url-workorder.md`); why v1 was
dropped is recorded first because its reasoning is the strongest argument for
this design.

Goal, unchanged from v1: CD stops writing to `main` entirely. No publish-App
bypass of the `main-protect` ruleset, no release commit pushed by a machine.
The per-release human action is merging a release PR, and that PR **carries the
marketplace pin**, which is the difference from v1.

---

## Why v1 (static `latest` URL, no pin) was dropped

Verified against the Claude Code docs (Plugin marketplaces / Plugins reference,
2026-08-19):

1. **v1's update detection was mispriced, not broken.** v1's claim that a
   moving `releases/latest` pointer "needs nothing new to trigger updates" is
   doc-supported — the Version-management table's digest row says that without
   a pin "users get updates whenever the hosted zip file's bytes change", and
   a version declared in `plugin.json` likewise lives in the fetched bytes.
   But that support comes only via the expensive path: for a static marketplace
   entry the signal is inside the archive, so **every update check downloads
   the ~10 MB zip**, for every user, at auto-update cadence, forever. v2's
   signal is the pin in `marketplace.json`, so checks ride the marketplace git
   refresh — metadata-only. *(Correction 2026-08-19: an earlier revision
   claimed the docs were silent on stable-URL re-fetch and updates might never
   be detected; the digest row contradicts that — detection was documented,
   just download-priced.)*
2. **v1's pin threat model was backwards.** It argued "an actor who can swap
   the asset can usually rewrite the repo too". Under a PR-gated `main` with
   zero bypass actors, that inverts: `main` becomes the hardest thing to write
   and release assets the easiest (`contents: write` suffices). The pin is the
   one thing tying the bytes users install to the PR-reviewed repo state —
   v1 deleted it at the moment it gained the most value.
3. **The pin never required the bypass.** What requires a per-release write to
   `main` is the pin; what this work order kills is the *bypass push*. Those
   decouple: the pin rides the release PR.

Revisit v1 only if Claude Code someday gains cheap update detection for
stable-URL archives (e.g. HEAD/ETag checks) or signature verification for
plugin sources.

---

## Key facts (docs-verified 2026-08-19)

1. **Version resolution precedence** (Claude Code, *Plugins reference →
   Version management*): `version` in the fetched source's `plugin.json` →
   `version` in the marketplace entry → git commit SHA (git sources) → sha256
   digest (`archive` sources) → `unknown`. A declared version or a changed pin
   is the update signal. Checks are **metadata-only when the signal lives in
   the marketplace entry** (pin or entry version — the marketplace refresh is
   a git pull); a signal that lives only inside the archive (its `plugin.json`
   version, or its digest with no pin) costs a download per check. v2 keeps
   the signal in the entry.
2. **`sha256` is optional but enforced**: every download is verified against
   it, install refused on mismatch. It is Claude Code's *only* install-time
   integrity mechanism — no sigstore, attestation, or signed-tag verification
   exists or is documented as planned. Keeping install-time integrity means
   keeping the pin.
3. **`gh release create` with assets is draft-based internally** (create
   draft → upload assets → publish), so asset upload composes with immutable
   releases (fact 4).
4. **Immutable releases** (GitHub, GA 2025-10): drafts stay fully editable;
   at publish, assets lock (no add/modify/delete) and the tag becomes
   protected (no delete/move). Publishing also **auto-generates a sigstore
   release attestation** (tag + commit SHA + asset digests, checkable with
   `gh attestation verify`) — provenance for free, verifiable by humans and CI
   even though Claude Code itself never checks it (proven live in W6.2).
   Existing releases stay mutable unless republished.
5. **Consumer floor unchanged**: `archive` sources need Claude Code v2.1.224+
   (recorded in `eng/set-archive-source.ps1`'s header, which survives in v2).

## Already done (2026-08-19, independent of the workflow rewrite)

- **`main-protect` strict up-to-date**: `strict_required_status_checks_policy:
  true`. No PR merges unless its required checks ran against the current tip
  of `main` — it keeps `ci-ok` honest against the base actually merged into,
  and makes drift *blocking* on a release PR. It does **not** guarantee the
  release's provenance (a drifted branch that re-ran its checks still merges);
  that guarantee is phase B's dispatch-diff check (below). Price accepted:
  while a release PR is open, any other PR landing on `main` invalidates it —
  close + re-dispatch. Fine for a single-maintainer repo.
- **Immutable releases enabled** on the repo (`PUT
  /repos/vdebellabre/claudinine/immutable-releases`). Applies to releases
  published from now on; v1.2.0 remains mutable (fine — never republish it).

---

## Decision

`marketplace.json` stays per-release, exactly today's shape: versioned asset
URL + `sha256` pin. What moves is *how the write lands*: authored by CD on a
release branch, reviewed and merged by a human, never pushed directly.

### Target release flow

**Phase A — open the release PR** (`cd.yml`, `workflow_dispatch` with the
existing `bump: [patch, minor, major]` input):

1. Compute the version (`eng/bump-version.ps1 -Component <bump> -WhatIf`) —
   unchanged.
2. Refuse to republish — the existing release-and-tag checks, unchanged.
3. Refuse to race: fail if a `release/v*` branch, an open release PR, **or a
   draft release `v<version>`** already exists.
4. Build via `./.github/workflows/build.yml` **with the `version` input** —
   the uncommitted-override mechanism survives v2: phase A builds from the
   dispatch SHA before the release branch exists, and the override is what
   stamps the version into that build. Outputs: zip, `.plugin`, sha256.
5. Create **draft release** `v<version>` (notes from `eng/release-notes.ps1`,
   digest line included) and upload both assets. A draft is invisible to
   users and to `releases/latest`, creates no tag, and stays editable —
   immutability starts at publish (fact 4).
6. Create branch `release/v<version>` from the dispatch SHA; run
   `eng/set-version.ps1`, `eng/release-notes.ps1 -Version <v> -Promote`
   (still fail-closed on an empty `Unreleased` section), and
   `eng/set-archive-source.ps1 -Sha256 <digest> -Version <v>`; commit
   `Release <v>`; push with the App token.
7. Open the PR with the App token, labeled `no-notes` (promotion reopens an
   empty `Unreleased` slot). PR body: the rendered release notes + digest,
   plus a machine-readable line recording the dispatch SHA the zip was built
   from (e.g. `<!-- dispatch-sha: <sha> -->`) — phase B's provenance check
   reads it back.

The human merge is the release gate. The reviewed diff is the changelog that
ships, two version stamps, **and the pin that will govern every install** — a
strictly better review surface than v1's, which had no pin to show.

**Phase B — publish on merge** (new `cd-publish.yml`,
`on: pull_request: types: [closed]`, guarded by `merged == true` and head
branch `release/v*`):

1. Coherence checks, all before anything irreversible:
   - manifest version at `merge_commit_sha` == the branch-name version (a
     human edit in flight must stay coherent);
   - **provenance**: `git diff --name-only <dispatch-sha> <merge_commit_sha>`
     (dispatch SHA read from the PR-body line) touches exactly the four
     release-managed files — `.claude-plugin/plugin.json`,
     `src/Claudinine/Claudinine.csproj`, `CHANGELOG.md`,
     `.claude-plugin/marketplace.json`. The published zip was built from the
     dispatch SHA, so this asserts the tag covers precisely that tree plus the
     release stamps. Any drift — an "Update branch" click, a stray push to the
     branch — surfaces as extra files, and a plain tree diff is
     merge-method-independent. (A merge-tree == head-tree check would NOT
     catch this: "Update branch" moves the PR head itself, so head identity
     passes while the zip's provenance silently diverges.);
   - the draft carries exactly the two expected assets (a missing `.plugin`
     must not publish silently);
   - download the draft's zip asset, hash it, compare against **both** the pin
     in the merged `marketplace.json` (catches a mis-uploaded or tampered
     asset — the class v1 gave up) **and** the digest phase A recorded in the
     PR body. The second comparison closes a gap the pin alone leaves open: an
     actor with `contents: write` but no PR-write could swap the draft asset
     post-review and push a matching pin edit to the release branch — the pin
     lives in one of the four allowed files, so every other check passes. The
     PR body is phase A's point-in-time record, editable only with PR-write
     (parsed from an env var, like `dispatch-sha`).
2. Re-run the republish check (guards re-runs after success).
3. Push tag `v<version>` at `merge_commit_sha` with `GITHUB_TOKEN`
   (`contents: write`; tags need no bypass and no PR).
4. Publish the draft (`gh release edit v<version> --draft=false`). It attaches
   to the just-pushed tag, becomes immutable, and GitHub emits the release
   attestation (fact 4).
5. Delete the `release/v<version>` branch if it still exists — a backstop
   only: `delete_branch_on_merge` is on repo-wide (enabled 2026-08-19), so
   GitHub removes the branch at merge time and this step normally reports
   "already gone". It exists so the next dispatch's race check stays clean if
   that setting is ever turned off. Best-effort — the release is already out;
   a failure is a warning.

**Consequences:** CD is read-only on `main`. The `main-protect` ruleset loses
its bypass actor; the App is demoted from bypass-pusher to ordinary PR author
(keeps its secrets, gains `pull_requests: write` and `issues: write` — the
`no-notes` label must ride the App's own PR-create call, because a
`GITHUB_TOKEN`-applied label fires no `labeled` event and the changelog gate
would stay red from its `opened` run). No marketplace format change, no
transition dance, existing installs unaffected; update checks stay
metadata-only.

### Why the App survives at all (and only for PR creation)

PRs created with the workflow's `GITHUB_TOKEN` do **not** trigger other
workflow runs (GitHub's recursive-run prevention), so a `GITHUB_TOKEN` release
PR would never get its two required checks and could never merge under the
ruleset. An App-token PR triggers CI normally. Branch push in phase A uses the
same App token for symmetry. Phase B needs no App: it reacts to a human merge
and only pushes a tag and flips a draft.

### What stays unchanged

- `changelog-gate.yml` and the per-PR `Unreleased` notes discipline (the
  release PR uses the existing `no-notes` escape hatch; nothing new).
- The six-RID matrix, pack/verify steps, `.plugin` hosted bundle.
- **All eng scripts survive**, including the two v1 wanted gone:
  `eng/set-archive-source.ps1` (now run by phase A on the release branch) and
  `build.yml`'s `version` input (now consumed by phase A).
- Fail-closed ordering: everything fallible (version computation, republish
  and race checks, empty-notes check, build, pack, draft upload, the three
  phase-B coherence checks) happens before the first irreversible step, which
  is now the draft publish. Phase A's writes (draft release, branch, PR) are
  fully recoverable: delete all three.

---

## Trade-offs accepted

1. **Two full six-RID builds per release** — phase A's, and the PR's `ci-ok`
   check. Same count as v1 (PR CI + phase B build), just both before merge.
   The published bytes are phase A's; `ci-ok` merely gates the merge.
2. **Between merge and phase B's publish, `main`'s manifest pins a URL that
   404s** (the draft isn't public yet). Window is minutes; a user refreshing
   the marketplace inside it gets a failed download, self-healing on retry.
   Recovery for a phase B failure: re-run it — nothing is published until the
   undraft succeeds. **Not a regression**: today's flow has the same window —
   the release commit (pin included) and tag push precede the asset upload.
3. **An abandoned release leaves debris**: closing the PR unmerged leaves the
   branch *and* the draft release. The race check refuses the next dispatch
   until both are deleted. Optional nicety: a cleanup job on `pull_request
   closed && !merged`. (A *successful* release cleans up after itself — phase
   B step 5 deletes the branch, and publishing consumes the draft.)
4. **Immutability is forever**: a bad published release can never be patched
   in place — the fix is the next version. That is the existing
   never-republish discipline, now machine-enforced.

---

## Failure modes and edge cases

- **Release PR closed without merging:** nothing shipped, tree untouched.
  Delete the `release/v*` branch and the draft release, re-dispatch.
- **`main` moves while the release PR is open:** strict up-to-date blocks the
  merge. The correct response is **close + re-dispatch — never GitHub's
  "Update branch" button**: updating the branch folds drift into the tag that
  the published zip was *not built from*, and the drifted PRs' changelog notes
  sit in the reopened `Unreleased` slot, shipping code in v*X* with notes
  deferred to v*X+1*. A habit-click is machine-caught: phase B's dispatch-diff
  check fails loudly on the extra files.
- **Phase B's coherence checks fail after a bad merge** (e.g. Update-branch
  drift merged anyway): nothing is published, but `main` now carries the
  release commit with no release behind it. Recovery: delete the draft and the
  merged `release/v*` branch, re-dispatch — `bump-version.ps1` reads
  `gh release list`, so it recomputes the *same* still-unreleased version, and
  the fresh phase A builds from the current tip, drift now included and
  re-promoted (`release-notes.ps1`'s already-promoted handling keeps the
  re-run safe).
- **Two dispatches racing:** the race check (branch / open PR / draft exists)
  plus the republish check; the second dispatch fails before writing.
- **Phase B fails after merge:** re-run it. If it failed between tag push and
  undraft, the tag exists with only a draft attached — the tag is not yet
  protected (protection lands at publish), so the existing debris procedure
  (delete tag, re-run) still works.
- **Human edits the release PR in flight:** allowed and reviewed like any PR.
  A version edit trips phase B's manifest-vs-branch-name check; a pin edit
  trips the digest comparison; changelog wording flows through untouched.
- **Merge method:** squash, merge, or rebase all work — phase B tags
  `merge_commit_sha`, and the dispatch-diff check is a plain tree diff between
  two commits, indifferent to how the second one was produced.

---

## Work items

### W1 — cd.yml becomes phase A

Rewrite the header comment: the branch-model paragraph, the ordering
paragraph, and the App justification all change (the App is a PR author, not
a bypass actor; the first irreversible step moves to phase B). Delete: the
app-token *push* plumbing, the `Commit bump and pin` step, the `Push release
commit and tag to main` step. Keep: version computation, republish checks,
the `build.yml` call with the `version` input, App-token minting. Add: the
race check (branch / PR / draft), draft-release creation + asset upload, the
release-branch commit (set-version, promote, set-archive-source), PR
creation with the dispatch-SHA line in the body.
`permissions: { contents: write, pull-requests: write }`.

### W2 — new cd-publish.yml (phase B)

As specified above. `permissions: { contents: write }`. Trigger guard:
`github.event.pull_request.merged == true &&
startsWith(github.event.pull_request.head.ref, 'release/v')`.

### W3 — demote the App, delete the bypass

- `main-protect` ruleset: remove the publish App from bypass actors (keep
  deletion/force-push block, PR requirement, the two required checks, the
  strict policy).
- App installation permissions: confirm `contents: write`, add
  `pull_requests: write` **and `issues: write`** (labels go through the
  Issues API; see *Consequences* for why the label must be App-applied).
- Keep `PUBLISH_APP_ID` / `PUBLISH_APP_PEM` secrets (phase A still mints).

### W4 — header comments on surviving machinery

- `eng/set-archive-source.ps1`: now called by phase A on the release branch;
  the direct-push wording is stale. The consumer-floor note (Claude Code
  v2.1.224+) stays where it is.
- `eng/set-version.ps1`: called once, by phase A; the "cd.yml calls it again
  to make the write permanent" paragraph is stale.
- `build.yml`: the `version` input's header comment — same mechanism, new
  caller (phase A instead of the old release job).

### W5 — documentation

- `CHANGELOG.md` preamble: "CD renames that heading to the computed version in
  the release commit" → "in the release PR"; "fails the dispatch before
  anything is built or pushed" → "before any PR is opened".
- The switchover PR's own `Unreleased` entry describes the new model (the
  1.2.0 entry's "Single-branch releases" paragraph is superseded).

### W6 — verification

1. Dispatch a release: draft appears with both assets, PR opens with
   `no-notes`, the pin in the PR diff equals the draft zip's digest.
2. Both required checks pass; merge; phase B runs: tag at the merge commit,
   release published and immutable, release attestation present
   (`gh attestation verify <zip> --owner vdebellabre` or the release page).
3. Scratch profile holding the previous version: `/plugin marketplace update`
   then `/plugin update` offers the new one; `claudinine version` matches.
4. `.plugin` bundle still uploads to a Cowork account unchanged.
5. Ruleset audit: no bypass actors remain on `main-protect`; a direct push to
   `main` with an admin token is refused.

### W7 — `/ship-release` skill (after the flow has shipped a release)

Repo skill wrapping the two human actions into one confirmed command:
`/ship-release minor` → `gh workflow run cd.yml -f bump=minor` → poll for the
release PR → `gh pr checks --watch` → present the shipping diff (changelog,
stamps, pin) in-session → on explicit confirmation, `gh pr merge
--delete-branch` → watch phase B → report the release URL and digests. The
in-session confirmation *is* the release gate; the skill removes tab-switching,
not review. Build it against the real PR lifecycle, not before.

---

## Transition

None. The marketplace format doesn't change, so there is no asset-name or
pointer dance (v1's four-step transition is void). Land W1–W5 in one PR;
merging it changes nothing observable — the new model first runs on the next
dispatch, which is also W6's verification.
