# Agent instructions

Keep responses terse and to the point. Try to spend less tokens whenever
possible. Less is more. No preamble, no restating the request, no trailing
summary unless asked. Prefer diffs/snippets over full file dumps.

Write the least code/tests/docs that fully satisfies the requirement — no
speculative abstractions, no redundant test variants for the same claim,
no comments or docs restating what the code already says; when in doubt,
cut.

## Where to work

**`main` in `D:\Dev\Claude\eci-cas`, always.** One developer, no
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

## Instructions are prose, not code

Anything that colours how an agent behaves or answers belongs in
`src/EciCas.Host/instructions/*.txt`, never in a C# string constant — a
prompt, a persona, a canned reply, a notice the user reads. The test is not
"does a model see it" but "is this a writing decision". If it is, a rebuild
should not be the way to revise it.

Surface interiority only where something actually happened to cause it.
Governance's *"(Thinking without Recall just now, so this is less grounded
than usual.)"* is the template: true, caused, and silent otherwise. A
persona narrating a mood it has no grounds for is the failure this avoids,
which is also why the drive window reaches Reflection as words and never as
numbers.

## Docs

`docs/` holds:

- `docs/architecture.md` — system design: agent roster, bus mechanics, storage, verification
- `docs/roadmap.md` — what's ahead, open design questions
- `docs/appendix.md` — operational notes: running the host, reading its
  output, and traps that have already cost someone an afternoon. Add to
  this whenever a debugging session turns up something worth not
  rediscovering.

A review that lands as its own document is a worklist, not a changelog:
delete an entry when it is fixed rather than marking it done, move
anything that survives into `docs/roadmap.md`, and delete the document
once nothing is left in it.

The Python prototype this project replaced lives in the sibling folder
`eci-cas-python-prototype` (also pushed to its own remote as a
fallback) — not part of this repo, reference only.
