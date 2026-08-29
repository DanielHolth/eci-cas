# Handover

Notes for whoever picks up the next session. Read this, then
[`current-spec.md`](current-spec.md) for how the system actually works
and [`roadmap.md`](roadmap.md) for what's planned. Replace this file's
contents each session — it's a pickup point, not a log.

## Current focus

**New milestone: the C# rebuild, replacing the Python source in this
same repo.** This Python repo's core weakness — a bus that dispatches
synchronously and recursively instead of decoupling agents — was
surfaced 2026-08-29 (Consolidator's now-slower `slow-medium` model
exposed that it was blocking the live reply path despite every doc
calling it "asynchronous"). Rather than patch that in Python, Daniel
scoped a from-scratch C# rebuild with genuinely decoupled,
independently-listening agents — his own architectural picture, not a
port of this one. Full design capture:
[`docs/csharp-rebuild-spec.md`](csharp-rebuild-spec.md). It starts in
its own chat context, but lands **in this repo**, not a new one —
Daniel is renaming the current Python source folders aside (manually)
to make room; `morrow-eci/` (the Next.js frontend) keeps its name and
location unchanged.

Milestone 2 (Morrow-ECI) is still the open work item on the Python
side until the C# rebuild replaces it — see below.

## Where things stand (this Python repo)

All 12 roles run live by default; no mocks left unaddressed in the
roster. Dispatch #5 (fast-\*/slow-\* substrate classes, Mistral EU fast
lane) landed 2026-08-29 — see `docs/roadmap.md`. One follow-on fix
landed with it: OpenAI's `gpt-5.6-luna` at `reasoning_effort: "medium"`
rejects any non-default `temperature`, fixed via `temperature_locked:
true` on the `slow-*` manifest entries plus a reactive per-model
fallback in `substrates/providers.py`. Console noise was also trimmed:
`tools/console.py` no longer double-prints Action's spoken output (the
Governance→Action trace hop was a redundant copy of what
`StreamSink` already writes).

Milestone 1 (Consolidation doodle backend, `ConsolidationWritten` on
`system.control`) is done. **Milestone 2 is open**: replace
`morrow-eci/lib/mockTurn.ts`'s mocked feed with a real
`system.control` / `events.*` subscription (void-observer only), and
wire `ConsolidationDoodle.tsx`'s click to
`Sensory.ingest(source_type: "ui_click", ref_event_id: ...)`. See
`docs/roadmap.md`'s Milestone 2 section.

## Genuinely open, not yet designed

Deliberately not on the Milestone 2 roadmap — these need a real design
pass before they're buildable, not just implementation time:

**Swappable personas.** The user may want to switch which persona is
active ("which tamagotchi am I playing with today?"). Knowledge would
stay shared across personas (it's "what happened," not character);
Personality would not — each persona needs its own trait bank that only
develops while active. This touches Archive's `kind` namespacing
(`agents/archive/store.py`), Intent's persona cache (currently one
`PersonaState` hydrated once, `agents/intent/base.py`), and
Consolidator's batch/threshold gating (would need to gate on "was this
persona active when the batch's events happened"). Open question: does a
swap create a new Intent instance or re-hydrate the same one from a
different store? Probably wants its own design doc before any code —
this is the largest single piece of unscoped work in the project.

**Match input to output, not just retrieve.** Personality and Knowledge
currently answer "what does the archive say that's relevant to this
event" — a retrieval question. The sharper version is "given this
event, what do I already know that changes how I should read it" — an
inference question. Tension: `archive_lookup/contract.py`'s own design
principle is "report what the records say, not what you happen to
know... never invent a record." Pushing toward inference risks turning
Knowledge/Personality into a second Analytics, which is exactly what the
original archive-lookup design was written to prevent. Needs a real
design conversation, and is hard to evaluate against a thin archive.
