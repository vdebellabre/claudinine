# Claudinine

**Claudinine keeps your Claude Code sessions from getting heavy.**

Every session, Claude Code writes down everything that happened — every file it
read, every command it ran, every search result. That file grows fast, and it is
mostly bulk you will never look at again: the full text of a file Claude read
once, the output of a build that succeeded twenty turns ago.

The next time that session is loaded — you resume it, or Claude compacts it —
all of that bulk gets read back in. It costs tokens, time, and it crowds out the
part of the conversation that actually matters.

Claudinine trims the bulk as you go. It keeps a short summary of what happened
and moves the full details to a side file, so nothing is lost — it is just not
in the way anymore. Your next session starts lean.

## Why use Claudinine

- **Your long sessions stay usable.** Less filler in the transcript means more
  room for the actual conversation before Claude has to compact.
- **Resuming is faster and cheaper.** Reloading a session no longer means
  reloading megabytes of old tool output.
- **You do not have to think about it.** There is no dashboard, no report, no
  prompt asking you to approve anything. Install it and forget it.
- **Nothing is thrown away.** Full outputs are kept in a side file, and you can
  pull any of them back or undo the whole thing (see [Getting your details back](#getting-your-details-back)).

Across 174 real sessions, transcripts shrank from **189 MB to 43 MB** on disk.
The typical session gives up **about three quarters of the tokens** Claude has
to read back when it loads that session again; subagent transcripts, which are
almost pure tool traffic, give up **82%**.

## One thing to expect

Your scrollback and the transcript are the same file, so after you resume a
session, older tool outputs show up as short stubs instead of their full text.
That is the compaction in effect — the originals are still on disk.

## Install

Add the marketplace, then install the plugin:

```bash
/plugin marketplace add vdebellabre/claudinine
```

```bash
/plugin install claudinine
```

That is the whole setup. Claudinine runs silently from then on.

Requires Claude Code 2.1.224 or later. Compatible with Claude Desktop and Claude CLI.
Published for x64/arm64 on Windows, macOS and Linux.

### Staying up to date

Third-party marketplaces do not auto-update by default, so new versions will not
arrive on their own. In the CLI, turn it on once with `/plugin` → **Marketplaces**
→ `claudinine` → **Enable auto-update**. The desktop app has no equivalent toggle
— its plugin pane only offers a manual **Update** button — so set it by hand in
`~/.claude/settings.json`:

```json
"extraKnownMarketplaces": {
  "claudinine": {
    "source": { "source": "github", "repo": "vdebellabre/claudinine" },
    "autoUpdate": true
  }
}
```

Both apps read the same file, so doing it either way covers both. Updates are
fetched in the background shortly after a session starts and load on the next
launch, never mid-session.

### Getting your details back

You can undo compaction for a session entirely, while it is closed. The transcript is
rebuilt verbatim from the mirror, and Claudinine can leave that session alone from
then on:

```bash
claudinine restore-compaction-off <session-id>
```

Use `restore-compaction-on` instead to restore then let compaction resume.

---

## Comparison with Cozempic

[Cozempic](https://github.com/Ruya-AI/cozempic) solves a closely related problem,
and Claudinine started as an attempt to get the same benefit with far less
machinery. If you are choosing between them, the differences that matter are:

- **No dependencies, no runtime to install.** Claudinine is a single native
  binary. Cozempic needs Python, plus `uv` or `pip`, plus the `fastmcp` and
  `cozempic` packages — on a machine where the wrong `python3` comes first on
  `PATH`, that is a real source of breakage.
- **No persistent processes.** Claudinine runs on hook invocations and exits;
  nothing stays resident. Cozempic spawns a background guard daemon per session
  and keeps an MCP server running alongside it.
- **No MCP server, so no context cost of its own.** MCP tool definitions occupy
  context in every session. A tool whose purpose is to save context should not
  spend any first; Claudinine registers none.
- **Nothing is installed or upgraded behind your back.** Cozempic's session-start
  hook runs `pip install --upgrade cozempic` on every session unless you set an
  opt-out variable, which means the code that edits your transcripts can change
  without you asking. Claudinine only changes when you update the plugin.
- **No slash commands to remember, no nudges.** Claudinine has no user-facing
  loop at all: it never posts status lines, never suggests you run a treatment,
  and adds no turns to your conversation. Cozempic is driven through
  `/cozempic` skills (treat, reload, guard, doctor) and prompts you when it
  thinks you should act.
- **Cross-platform without a shell.** Claudinine's hooks invoke a binary
  directly. Cozempic's hooks are long POSIX shell one-liners using `flock`,
  `stat`, and `/tmp` paths — fragile on native Windows.

The compaction itself also works differently, and this is where most of the
practical difference shows up:

- **Chain-collapse has no equivalent.** Claudinine's biggest single win is
  turning a turn that ran many tool calls into one digest record — each call
  listed with a short preview, full outputs moved aside. Cozempic prunes
  record by record (thinking blocks, stale reads, mega-block trim, envelope
  strip); it has no notion of collapsing a whole tool chain, which is exactly
  where large sessions put their weight.
- **Removed content is kept, not deleted.** Claudinine writes every full output
  to a per-session mirror, so a stub is a pointer rather than a loss, and
  `restore-compaction-off` rebuilds the transcript verbatim. Cozempic's safety
  net is a timestamped `.bak` copy of the whole file — fine for undoing the
  last treatment, but it does not let you pull back one specific output while
  keeping the savings.
- **It runs continuously, not as a treatment.** Claudinine compacts each turn
  as it completes, so the file is already lean at rest. Cozempic's pruning is
  an operation you invoke — diagnose, dry-run, confirm, apply, then resume the
  session — with savings quoted per prescription (`gentle` through
  `aggressive`) at the moment you run it.

### Measured side by side

Both tools ran over the same corpus of **174 real sessions** (189 MB, 97 main
transcripts and 77 subagent transcripts), each on its own copy so neither saw
the other's output, every one starting from a baseline neither tool had touched.
Cozempic ran its strongest prescription (`treat -rx aggressive`). The corpus and
harness are in the repo (`eng/bench/`), so the numbers below are reproducible.

**What "tokens" means here matters**, because it is where a naive measurement
goes wrong. The count is BPE over only what Claude actually reads back:
`message.content` blocks, and only from the last compaction boundary onward. Two
large parts of a transcript are *not* counted, because the model never sees them:

- **`toolUseResult`** — a top-level field duplicating each tool's output
  alongside the copy in `message.content`. It feeds the transcript UI. It was
  **half the payload** on tool-heavy sessions.
- **Everything before a compaction boundary** — once Claude compacts, the loader
  reads only from that boundary on. On the one corpus session that had compacted,
  that was **70% of the file**.

Deleting either shrinks the file on disk without saving Claude a single token.
Counting them credits a tool for work that has no effect, so both were excluded
for both tools. Byte columns still cover the whole file, which is the honest
measure for disk.

| | baseline | Claudinine | Cozempic |
|---|---|---|---|
| **All sessions** (n=174) | 189.2 MB / 13.44 M tok | 42.7 MB (77.4%) / **4.18 M tok (68.9%)** | 83.8 MB (55.7%) / 9.89 M tok (26.4%) |
| **Main transcripts** (n=97) | 167.9 MB / 10.34 M tok | 38.7 MB (77.0%) / **3.63 M tok (64.9%)** | 71.1 MB (57.7%) / 7.88 M tok (23.8%) |
| **Subagent transcripts** (n=77) | 21.2 MB / 3.10 M tok | 4.0 MB (81.2%) / **0.55 M tok (82.1%)** | 12.7 MB (40.1%) / 2.01 M tok (35.1%) |

Those totals are dominated by whichever sessions happen to be largest — the ten
biggest are about 28% of all corpus tokens. For what a single session should
expect, the per-session view is the useful one, so here it is by size, with every
file kept:

| session size | n | Claudinine | Cozempic |
|---|---|---|---|
| Under 30k tokens | 52 | **67.2%** | 20.5% |
| 30k – 100k | 86 | **77.8%** | 32.6% |
| 100k – 400k | 33 | **62.8%** | 22.9% |
| Over 400k | 3 | **67.0%** | 24.2% |

The median session gives up **74.7%** of its tokens to Claudinine and 23.8% to
Cozempic. Claudinine saves more on **167 of 174** sessions, with 5 ties and 2
sessions where Cozempic saves more. It is also about 10× faster over the corpus
(30s against 300s), which is what a native binary buys over a Python process
spawned per session.

The two sessions Cozempic wins are worth naming. One is 68% a single 900 KB
block — a bundled skill the session loaded — which Cozempic truncates and
Claudinine leaves alone; across the corpus, oversized injected blocks like that
are under 2% of all payload. The other is a 0.8-point difference. Neither
reflects a category Claudinine handles badly.

Subagent transcripts compact especially well, since a subagent run is one long
uninterrupted chain of tool calls — exactly the shape chain-collapse is built
for. Claudinine finds those files itself from the session directory; Cozempic has
no session-directory concept, so it was pointed at each one explicitly.

Cozempic does things Claudinine deliberately does not: live token monitoring,
agent-team protection across compaction, and interactive diagnosis. If you want
a tool you drive, look there. Claudinine is for people who want the transcript
to stay small and never think about it again.

---

# Technical details

## Where the savings come from

Most of the yield is **chain-collapse**: a turn that ran many tool calls becomes
one digest record listing each call with a short preview, and the full outputs
move to a sidecar mirror. The rest comes from the aging and trim family
(age-tiered tool-result stubs, mega-block trim, image strip) and from
record-level housekeeping (superseded file edits, stale reminder blocks, queue
history).

Byte and token percentages are not interchangeable, and neither is a proxy for
the other. A transcript carries a lot of weight Claude never reads — the JSON
envelope, the duplicated `toolUseResult` field, history behind a compaction
boundary — so a rule can move bytes without saving tokens, or the reverse.
Compare like with like: bytes for disk, tokens for what a session costs to load.

## How it works

- **UserPromptSubmit** — after each of your prompts, the turn that just
  finished is mirrored to a sidecar file, then compacted in the live transcript.
- **SessionEnd** — the final turn gets the same treatment, leaving the file
  clean at rest. Subagent transcripts (`<session>/subagents/agent-*.jsonl`) are
  swept here too, each with its own mirror.
- **SessionStart / PreCompact** — full-scan repair for crash leftovers
  (including the subagent sweep), plus garbage collection of mirrors and
  orphaned session directories.

Compaction never touches the live in-memory context of a running session — the
payout arrives at the next transcript load. Every rewrite is validated before an
atomic swap, and any failed check leaves the original untouched. See
[docs/session-file-changes.md](docs/session-file-changes.md) for exactly what is
modified, why, and what the safety guarantees are.

## Engineering

C# / .NET 10, Native AOT, zero NuGet dependencies. One small native binary per
platform (6 targets), built by CI and shipped in the release archive rather than
committed. A dual shim routes to the right one at `bin/` inside the archive
(`claudinine` for POSIX shells, `claudinine.cmd` for cmd.exe); both are
hand-written source and live in `eng/shims/`.

## Distribution

CI builds all six targets (Native AOT cannot cross-compile, so the matrix *is*
the release build) and packs them into one zip, `claudinine-<version>.zip`, built
by `eng/pack-plugin.ps1`. The archive carries only the runtime payload — manifest,
hooks, shims, binaries — which is ~8.7 MB against a ~250 MB working tree.

That asset is what the marketplace serves, via an `archive` source pinned to the
release URL and its SHA-256 (`eng/set-archive-source.ps1` writes both). Installing
needs no git clone, which matters because this repo's `.git` carries every binary
ever shipped and grows ~18 MB per release.

### CI / CD

Three workflows. `build.yml` is the shared build — the six-RID matrix, the pack
and the archive verification — reusable via `workflow_call` and publishing
nothing. **Commit validation** (`ci.yml`) calls it on every push and PR.
**Publish release** (`cd.yml`) is the only path that publishes, and it calls the
same build, so a release ships exactly what CI validated rather than a second
build of it.

Releasing is a manual **Run workflow** on *Publish release*, choosing which
component to bump: `patch`, `minor` or `major`. Because the version is *computed*
from the dropdown rather than supplied, no input can name a version that already
shipped; a collision therefore means an invariant broke, and the run refuses.

The order matters. The version is computed without being written
(`eng/bump-version.ps1 -WhatIf`), then the build writes it into its own checkout
just before packing (`build.yml`'s `version` input → `eng/set-version.ps1`) — so
the archive carries its version without anything being committed. Only once the
archive exists does the release job commit the bump *and* the refreshed digest
pin as one `Release <version>` commit, push it with an annotated tag, and create
the release from that pushed tag.

So everything fallible — the six-RID build, the pack, the republish check —
happens before the first write, and a failure there leaves `main` exactly as it
was. Only the asset upload sits after the push; if that fails, the commit and tag
are already correct, so re-running the release job attaches the asset to the
existing tag without a new version or a new build.

Both the version and the pin live in the tagged commit, so the tag and `main`
never disagree about which digest is pinned. Pull after a release.

Because the version is *computed* from the dropdown rather than supplied, a
release run cannot target a version that already shipped; if the tag somehow
exists, the run fails rather than overwriting the published asset. The zip is not
byte-reproducible across builds, so re-uploading would silently break the pinned
digest for anyone mid-install.

## License

MIT — see [LICENSE](LICENSE).
