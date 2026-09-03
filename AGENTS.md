# Agent instructions

Be terse. No preamble, no restating the request, no trailing summary
unless asked. Prefer diffs/snippets over full file dumps.

Write the least code/tests/docs that fully satisfies the requirement — no
speculative abstractions, no redundant test variants for the same claim,
no comments or docs restating what the code already says; when in doubt,
cut. Less is more.

## Where to work

**`main` in `C:\Users\holdan\source\eci-cas`, always.** One developer, no
branches, no worktrees — commit to `main` and push. If a worktree exists,
it is leftover plumbing, not somewhere to work.

This is not only a preference. Each worktree carries its own `bin/`, and
the archive lives in the build output, so a host started from the wrong
folder gets a *different persona with an empty memory* — it seeds
`assistant/identity` and knows nothing else. That reads exactly like a
retrieval bug and has already been debugged as one. If Librarian reports
an index of one pair, check the folder before the code.

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
