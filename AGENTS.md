# Agent instructions

Write the least code/tests/docs that fully satisfies the requirement — no
speculative abstractions, no redundant test variants for the same claim,
no comments or docs restating what the code already says; when in doubt,
cut. Less is more.

## Architecture

Always use loose coupling and async processes for transactions between
agents on the service bus. A `publish()` must never block on a
subscriber finishing its own handling, and no agent's correctness may
depend on another agent's message arriving before or after it —
concurrent, message-passing, independently-listening agents, not a
shared call stack wearing a pub-sub API. This is the standing rule the
Python bus (`bus/pubsub.py`, synchronous/recursive dispatch) violates —
see `docs/csharp-rebuild-spec.md` for the full diagnosis and the C#
rebuild that corrects it. Don't reintroduce the same coupling there or
anywhere else.

## Docs

`docs/` holds four living documents plus one active rebuild spec; keep
them current as you work instead of leaving it for someone to notice:

- `docs/dispatch.md` — ongoing brainstorming, raw intake
- `docs/roadmap.md` — planned milestones, current state
- `docs/current-spec.md` — detailed description of what's built, as-is (no phase history) — describes the Python system only
- `docs/handover.md` — key notes for the next agent to pick up the work; replace its contents each session, it's not a log
- `docs/csharp-rebuild-spec.md` — design spec for the planned from-scratch C# rebuild (loosely-coupled async agents), landing in this same repo in place of the Python source (`morrow-eci/` keeps its name/location); lives outside the four above because it specs a separate build, not the current one — fold it into `docs/archive/` once the rebuild has its own roadmap entry here

Anything else that accumulates (old phase write-ups, superseded specs,
one-off design drafts) belongs in `docs/archive/` or `docs/ideas/`, not
loose in `docs/`.
