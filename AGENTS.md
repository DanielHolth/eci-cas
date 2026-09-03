# Agent instructions

Be terse. No preamble, no restating the request, no trailing summary
unless asked. Prefer diffs/snippets over full file dumps.

Write the least code/tests/docs that fully satisfies the requirement — no
speculative abstractions, no redundant test variants for the same claim,
no comments or docs restating what the code already says; when in doubt,
cut. Less is more.

## Architecture

Always use loose coupling and async processes for transactions between
agents on the service bus. A `Publish()` must never block on a
subscriber finishing its own handling, and no agent's correctness may
depend on another agent's message arriving before or after it —
concurrent, message-passing, independently-listening agents, not a
shared call stack wearing a pub-sub API. See `docs/architecture.md` for
the full design. Don't reintroduce that coupling here.

## Docs

`docs/` holds:

- `docs/architecture.md` — system design: agent roster, bus mechanics, storage, verification
- `docs/roadmap.md` — what's ahead, open design questions
- `docs/appendix.md` — operational notes: running the host, reading its
  output, and traps that have already cost someone an afternoon. Add to
  this whenever a debugging session turns up something worth not
  rediscovering.

The Python prototype this project replaced lives in the sibling folder
`eci-cas-python-prototype` (also pushed to its own remote as a
fallback) — not part of this repo, reference only.
