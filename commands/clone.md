---
description: Clone this session's compacted transcript into a fresh, resumable session
---

## Why

Claudinine's hooks compact the transcript **on disk** while a session runs, but a
running process assembled its context at startup and never re-reads that file. So
the savings are real, banked, and unusable in the session that earned them — a long
conversation stays as heavy in memory as it ever was.

Cloning produces a *new* session whose transcript is the already-compacted one.
Resume it and you continue the same conversation with the compacted context.

## Do this

Run the clone for the current session:

!`"${CLAUDE_PLUGIN_ROOT}/bin/claudinine" clone "${CLAUDE_SESSION_ID}"`

Then report to the user, based on the output above:

- If it succeeded, tell them the clone is ready and that they resume it by opening
  the session picker (`/resume`) and choosing the entry marked **(compacted)** —
  it carries this session's title plus that suffix. Give them the new session id.
- Mention that **this session is untouched**: nothing was archived or deleted, and
  a clone they never resume costs only disk. Cleaning up the old session is their
  separate, explicit call.
- If the `Mirror:` line said no mirror was found, warn that archived tool outputs
  from `claudinine get` refs will not resolve in the clone.
- If it failed, show the error and stop — do not attempt a manual copy as a
  fallback. A half-made session that appears in the picker and fails on load is
  worse than no clone.

Keep the report to a few lines. Do not re-explain the mechanism unless asked.
