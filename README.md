# Claudinine

[![Works with Claude Code and Cowork](https://img.shields.io/badge/works%20with-Claude%20Code%20%C2%B7%20Cowork-d97757)](#install)
[![Release](https://img.shields.io/github/v/release/vdebellabre/claudinine?label=release)](https://github.com/vdebellabre/claudinine/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platforms](https://img.shields.io/badge/platforms-win%20%C2%B7%20macOS%20%C2%B7%20linux-lightgrey)](#install)

**Claudinine silently slims your Claude sessions, moving the bulky parts to a side file. Your sessions stay lightweight, yet nothing is lost.**

Every session, Claude writes down everything that happened — every file it read, every command it ran, every search result. That file grows fast, and it is mostly bulk you will never look at again: the full text of a file Claude read once, the output of a build that succeeded twenty turns ago. The next time that session is loaded, all of that bulk gets read back in. It costs tokens, time, and it crowds out the part of the conversation that actually matters.

Claudinine trims the bulk as you go. It keeps a short summary of what happened and moves the full details to a side file, so your next session starts lean. Across **174 real sessions**, transcripts shrank from **189 MB to 43 MB** on disk. The typical session is reduced by about **75% of the tokens** Claude has to read back when it loads that session again.

- **Your long sessions stay usable.** Less filler in the transcript means more room for the actual conversation before Claude has to compact.
- **Resuming is faster and cheaper.** Reloading a session no longer means reloading megabytes of old tool output.
- **You do not have to think about it.** There is no dashboard, no report, no prompt asking you to approve anything. Install it and forget it.
- **Nothing is thrown away.** Full outputs are kept in a side file, and you can restore them (see [Getting your details back](docs/how-it-works.md#getting-your-details-back)).

## Install

Claudinine runs in the Claude Code CLI and in Cowork (claude.ai), in both of its modes — "In the cloud" and "On your computer". Pick the route that matches where you work.

**Claude Code (CLI, or the Desktop app's Code tab).** Claudinine is not in Anthropic's plugin directory; it ships from its own marketplace, which is this repository. Add the marketplace once, then install from it:

```bash
claude plugin marketplace add vdebellabre/claudinine
```

```bash
claude plugin install claudinine@claudinine
```

Inside a session, `/plugin marketplace add vdebellabre/claudinine` and `/plugin install claudinine@claudinine` do the same. That is the whole setup: the hooks register themselves and compaction starts with your next prompt. There is nothing to configure. The Desktop app's Code tab uses the same `~/.claude` plugin configuration, so an install made from a terminal shows up there too. You can also add the marketplace from the Desktop's own plugin settings, using the same `vdebellabre/claudinine` address.

Third-party marketplaces are not refreshed for you. To pick up a new release, update the marketplace, then the plugin (in the Desktop, the marketplace's "Check for updates" followed by the plugin's update button does the same):

```bash
claude plugin marketplace update claudinine
```

```bash
claude plugin update claudinine@claudinine
```

Each marketplace entry pins the release archive by sha256, so an install only ever unpacks the exact build that was published.

**Cowork (claude.ai).** Plugin marketplaces are disabled there, so `/plugin install` is not the route. Download `claudinine-<version>.plugin` from the [latest release](https://github.com/vdebellabre/claudinine/releases/latest) and import it in claude.ai's plugin settings. It installs account-wide and registers in your Cowork sessions automatically — including sessions already running. The same artifact covers both Cowork modes: it carries binaries for all six platforms, because cloud sessions run hooks inside a Linux container while local sessions run them on your own desktop — Windows or macOS included.

The two artifacts differ only in packaging: the `.plugin` file is what claude.ai's uploader accepts, while the CLI zip additionally puts `claudinine` on your PATH for the [retrieval commands](docs/how-it-works.md#getting-your-details-back).

## Learn more

- [How it works](docs/how-it-works.md): what a compacted turn looks like, when each hook runs, how to get full outputs back, and how to turn on diagnostics.
- [Comparison with Cozempic](docs/cozempic-comparison.md): the design differences, and a side-by-side benchmark on 174 real sessions.
- [What Claudinine changes in a session file](docs/session-file-changes.md): every modification, why it is made, and the safety guarantees.
