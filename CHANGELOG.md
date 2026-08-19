# Changelog

Condensed notes per release, newest first. Releases before `1.0.0` predate this
file and are not backfilled.

The CD workflow (`.github/workflows/cd.yml`) reads the section matching the
version it is publishing and puts it in the GitHub release body. **Write the
next release's section under `## Unreleased` as you go** — CD renames that
heading to the computed version in the release commit, and an empty
`Unreleased` section fails the dispatch before anything is built or pushed.

## Unreleased

- **Single-branch releases.** The `develop` branch is gone: development happens directly on `main`, and each release adds exactly one CD-written commit there (version stamps, marketplace pin, changelog promotion) plus a tag. Between releases the marketplace manifest keeps pointing at the previous release, so it is always a valid install target. Tags are the released snapshots; `main` no longer is one. No user-facing behaviour change.

## 1.1.0

Cowork local mode becomes fully usable: retrieval now works there, and every digest header tells the truth about how to retrieve on the host it was written for.

- **Retrieval in Cowork local mode.** Local sessions run hooks on the desktop host but shell commands inside a Linux microVM, so no command in a header could be trusted. Claudinine now dumps per-ref files to `outputs/.claudinine/refs/`, which the model's own Read/Grep tools can open — a retrieval path that needs no shell at all.
- **Headers adapt to their host.** Each digest block is written for the transcript's *current* location: launcher commands normally, file-tool instructions in local mode. Move a session and the next pass regenerates the right form.
- **Retrieval goes through a routing shim.** Headers now invoke `libexec/claudinine`, which picks the platform at *run* time, instead of baking in the write-time binary — the two differ whenever the hook's OS is not the shell's.
- **Old transcripts are upgraded in place.** Stubs and short headers from `0.1.x`–`0.4.x` carried a bare `claudinine get` command that resolves nowhere on hosted installs; they are rewritten to a working form as they are encountered. Idempotent — a current-form header is never touched.
- **Retrieval instructions are kept per compact boundary, not per file.** The app rebuilds live context from the last boundary onward, so a "first carrier" sitting before it left later pointers aiming at instructions the model could no longer see.
- **Cheaper anchor stubs.** Collapse anchors carry a path-free pointer to the carrier's retrieval block instead of a full command — paths tokenize badly, and this recovers ~0.5 pt of corpus tokens.
- **Release safety: a GitHub API error can no longer publish a bogus version.** During the 2026-08-17 outage a `503` was read as "no releases exist" and published a stray `v0.1.0` off a minor bump. Transient failures now retry, and a persistent one fails the run before anything is built or pushed.
- **WSL documented** as an ordinary marketplace install inside the distro.

## 1.0.1

Documentation-only release. No functional change from `1.0.0` — the binaries are identical in behaviour.

- **README documents Cowork.** Install routes are now split per surface: `/plugin install claudinine` for the Claude Code CLI, `.plugin` account import for Cowork on claude.ai.
- **Explains why the two artifacts differ.** The `.plugin` bundle is what claude.ai's uploader accepts; the CLI zip additionally puts `claudinine` on your PATH for the retrieval commands.
- **Documents the Cowork-specific wins** measured on real sessions: one autonomous cloud turn went 285 KB → 36 KB, and five subagent transcripts went 802 KB → 142 KB.

## 1.0.0

First stable release. Same engine as `0.1.22`, promoted after both Cowork modes were validated end to end on a real host.

- **Cowork support is complete in both modes.** Cloud sessions ("In the cloud") install, compact and retrieve; local sessions ("On your computer") install, register all six hooks and compact — validated the same day against a live desktop session.
- **Hosted bundle ships all six platform binaries again.** Local-mode hooks execute on the *desktop host*, not in the Linux sandbox, so the Linux-only slimming shipped in `0.1.20`–`0.1.21` made every hook die with exit 127 on Windows and macOS desktops. CI now asserts all six RIDs are present so it cannot regress.
- **API stability.** Retrieval headers, stub formats and mirror layout are settled; later versions upgrade older transcripts in place rather than breaking them.

