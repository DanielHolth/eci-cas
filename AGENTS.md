# Agent instructions

Terse. No preamble, no restating the request, no trailing summary unless
asked. Diffs and snippets over full file dumps.

Write the least code/tests/docs that fully satisfies the requirement — no
speculative abstractions, no redundant tests for the same claim, no comments
restating the code. When in doubt, cut.

Spawn every subagent on **sonnet, low reasoning effort** — pass the model
explicitly on each `Agent` call rather than inheriting this session's.

## Where to work

**`main` in the one checkout, always.** One developer, no branches, no
worktrees — commit and push. A worktree or second clone is leftover
plumbing, not somewhere to work.

Each checkout carries its own `bin/`, and the archive lives in the build
output (`src/EciCas.Host/bin/<config>/net10.0/archive`), so a host started
from the wrong folder gets a *different persona with an empty memory*. That
reads exactly like a retrieval bug and has been debugged as one. If
Librarian reports an index of one pair, check the folder first.

## Architecture

Loose coupling, async, message-passing. A `Publish()` must never block on a
subscriber, and no agent's correctness may depend on another's message
arriving before or after it — independently-listening agents, not a shared
call stack wearing a pub-sub API. Full design in `docs/architecture.md`.

## Instructions are prose, not code

Anything that colours how an agent behaves or answers goes in
`src/EciCas.Host/instructions/*.txt`, never a C# string constant — prompt,
persona, canned reply, a notice the user reads. The test is not "does a
model see it" but "is this a writing decision". If it is, a rebuild should
not be the way to revise it.

Surface interiority only where something actually caused it. Governance's
*"(Thinking without Recall just now, so this is less grounded than
usual.)"* is the template: true, caused, silent otherwise. A persona
narrating an ungrounded mood is the failure this avoids — also why the
drive window reaches Reflection as words, never numbers.

## Commands

"Reset parquet" — `docs/appendix.md` § Resetting the archive.

## Docs

- `docs/architecture.md` — agent roster, bus mechanics, storage, verification
- `docs/roadmap.md` — what's ahead, open design questions
- `docs/appendix.md` — running the host, reading its output, traps that cost
  someone an afternoon. Add to it whenever debugging turns up something
  worth not rediscovering.

A review that lands as its own document is a worklist, not a changelog:
delete an entry when fixed, move survivors into `docs/roadmap.md`, delete
the document once empty.

The Python prototype this replaced lives in `eci-cas-python-prototype`
(sibling folder) — reference only, not part of this repo.
