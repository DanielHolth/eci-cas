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
shared call stack wearing a pub-sub API. This is a standing rule born
from the Python prototype's mistake (synchronous/recursive dispatch) —
see `docs/csharp-rebuild-spec.md` for the full diagnosis and the target
design that corrects it. Don't reintroduce that coupling here.

## Docs

`docs/` holds:

- `docs/csharp-rebuild-spec.md` — design spec for the C# rebuild (target architecture, what does and doesn't carry over from the Python prototype)
- `docs/roadmap.md` — planned milestones, current state
- `docs/handover.md` — key notes for the next agent to pick up the work; replace its contents each session, it's not a log

The Python prototype this project replaced lives in the sibling folder
`eci-cas-python-prototype` (also pushed to its own remote as a
fallback) — not part of this repo, reference only.
