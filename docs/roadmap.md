# ECI-CAS — Roadmap

## Milestone 1 — Backend finished (done, 2026-08-29)

**1. Consolidation doodle backend** — done. Phase 0.9 removed
Consolidator's epoch/batch model entirely (it writes per-event now, no
buffer, no epoch id), so this landed keyed on `event_id` rather than an
epoch id:
- `agents/consolidator/base.py`'s `_execute_writes()` publishes
  `ConsolidationWritten` on `system.control` after a write pass that
  actually wrote something — event_id plus a human-readable summary
  line (reusing `agents/governance/knowledge_swarm.py`'s
  `format_for_intent`).
- `ui_click` is a valid Sensory source type
  (`agents/sensory/agent.py`), and `Sensory.ingest()` takes an optional
  `ref_event_id` that lands in the fan-out's `meta`, naming which
  consolidation write-pass the click is about.
- Event-level dedup in Consolidator (`ConsolidatorBase.on_event()`): the
  first click referencing a given `ref_event_id` runs normally, every
  later click on the same one is a no-op (in-memory set, consistent
  with Consolidator's fail-open posture).

Design doc: `docs/ideas/consolidation-doodle.md` (also updated to match
the per-event model). This is also the backend half of Morrow-ECI's "+"
doodle (Milestone 2) — the frontend side can now move past its stub.

**2. Cleanup pass** — done.
- `spoken.jsonl` rotation — `agents/action/sinks.py`'s `FileSink` now
  date-partitions like the queue log (`<stem>_<date>.jsonl`, recomputed
  per write).
- Analytics' `proceed`/`concern` — already resolved (removed 2026-08-25
  per `agents/governance/agent.py`'s BUNDLE route comment); nothing left
  to decide.

## Dispatch #4 — Reflection Agent & domain taxonomy (done, 2026-08-29)

Landed alongside Milestone 1, off the roadmap's own numbering (see
`docs/dispatch.md` item 4 for the original design capture):

- **Domain taxonomy.** `agents/archive/structured_store.py`'s schema
  gained `domain` above `category` — `"external"` (everything
  Consolidator writes, the default for every pre-existing caller) vs
  `"internal"` (Reflection's own derived insight). Part of `upsert()`'s
  dedup key, so the same path can independently hold one fact in each
  domain. `tools/migrate_domain.py` backfills a pre-domain Parquet file.
- **Reflection Agent** (`agents/reflection/`), a 12th role. Fed by
  Governance's `_conclude()` fork (`events.reflection`) — one hop after
  Consolidator's BUNDLE fork, since Reflection needs the FINISHED arc
  (what Action actually did), not just the proposal. Every `batch_size`
  concluded events (manifest `roles.reflection.batch_size`, default 5),
  it looks for a durable pattern across them and produces at most one of:
  a `domain="internal"` archive write, an Idea ping back through
  `Sensory.ingest(source_type: "idea")`, or silence (the common case).
  Gates nothing, never replies to Governance — same posture as
  Consolidator. Tiers exactly like every other cognitive role
  (`roles.reflection.mock`, budget-tier presets share Consolidator's
  substrate class).

## Dispatch #5 — fast-*/slow-* substrate classes & Mistral fast lane (done, 2026-08-29)

See `docs/dispatch.md` item 5 for the original design capture. Replaces
the old `local`/`low`/`medium`/`high` substrate classes (kept as
commented-out reference in the manifest) with two crossed axes:

- **fast-\*** — the live/gated path (Analytics, Personality, Knowledge,
  Intent), where time-to-first-token is the budget. `fast-low` and
  `fast-medium` both point at Mistral's `ministral-3b-2512` (EU-hosted,
  France — the trigger for this dispatch was the old US-hosted
  `low`'s TTFT); `fast-high` (Super tier's Intent slot) points at
  `mistral-small-2603`.
- **slow-\*** — the async path (Consolidator, Reflection), which can
  spend luna's 2sec+ TTFT for `reasoning_effort: "medium"` instead of
  `"none"` — a smarter answer, tolerable because nothing downstream
  waits on it. `slow-low`/`slow-medium`/`slow-high` all point at
  `gpt-5.6-luna`, same vendor as the old `low`/`medium`.
- `fast-local`/`slow-local` replace `local`, split only by `max_tokens`
  ceiling (Minimal tier stays fully free/keyless).

`budget/tiers.py` rewritten around the new classes (`FAST_LOCAL_CLASS`
… `SLOW_HIGH_CLASS`); the tier ladder is now: Minimal =
fast-local/slow-local everywhere, Budget = fast-low/slow-low everywhere,
Default = fast-low + Intent on fast-medium + Consolidator/Reflection on
slow-medium (the shipped manifest, unchanged — `apply_tier`'s `default`
no-op still holds), Super = fast-low + Intent on fast-high +
Consolidator/Reflection on slow-high. No provider code changed — Mistral
was already a registered alias to the OpenAI-compatible adapter
(`substrates/registry.py`'s `PROVIDER_ALIASES`); the new classes are
manifest-only, plus a `MISTRAL_API_KEY` env var and a
`base_url: "https://api.mistral.ai/v1"` per Mistral-backed entry.

## Milestone 2 — Morrow-ECI, first iteration (active)

Morrow-ECI is the frontend companion surface — a Jarvis/tamagotchi
hybrid, not a literal avatar. Concept spec:
`docs/archive/v0-35-parallel-fanout-draft.md` §6. The scaffold
(`morrow-eci/`, Next.js) already renders Impulse's expressions, the
three-agent thought bubbles (Analytics/Personality/Knowledge), the
security yellow/red icon, and the speech bubble — all against a mocked
event feed (`morrow-eci/lib/mockTurn.ts`). Two things turn that scaffold
into a first real iteration:

**Live bus wiring**
Replace the mock feed with a real subscription to `system.control` /
`events.*`, void-observer only — same discipline as `tools/console.py`.
Component contracts (`morrow-eci/types/events.ts`) already match the
real event shapes, so this should be a feed swap, not a component
rewrite.

**The "+" doodle**
`ConsolidationDoodle.tsx` is stubbed pending Milestone 1's backend
payload, which has now landed: `system.control` carries
`ConsolidationWritten` (event_id + human-readable summary) after any
write pass that actually wrote something. Wire the click to fire
`Sensory.ingest(source_type: "ui_click", ref_event_id: <that event_id>)`
and close the loop.

## Milestone 3 — C# rebuild, genuinely decoupled agents (scoped, 2026-08-29)

Not a port of this repo — a from-scratch rebuild in its own project/chat
context, replacing the thing dispatch #5 exposed: the Python bus
(`bus/pubsub.py`) dispatches synchronously and recursively, so
Consolidator/Reflection were never actually off the live path, just
called "asynchronous." Full design capture, including the target
architecture (one queue + one listener per agent, fire-and-forget
publish, console as a plain subscriber) and what ports as business logic
vs. what doesn't: [`docs/csharp-rebuild-spec.md`](csharp-rebuild-spec.md).
Standing rule for any bus work going forward, Python or C#: `AGENTS.md`'s
Architecture section. Supersedes the old "Python → C# port" long-term
goal below in scope and intent — that one assumed preserving this repo's
pub-sub topology as-is, which is exactly what this milestone rejects.

## Long-term goals (post-Milestone 2)

**Minimal-tier local LLM.** A free 1.8B–3B model (Phi, Qwen, or similar)
for the `minimal` budget tier, so ECI-CAS can run on-device where cloud
connectivity is unreliable. New substrate class, not a topology change
— budget tiers already drive substrate selection via the manifest.
Concrete candidate flagged in `docs/dispatch.md`: Qwen 1.5B via MLC LLM.
Scope TBD: fine-tuning, quantization, latency targets.

**Python → C# port** — superseded by Milestone 3 above.

**Android native client.** On-device minimal-tier agent running the full
12-role system, or a remote-client mode where only Sensory and Action
cross process boundaries and all reasoning stays server-side. Stretch:
iOS via shared business logic. Needs UI parity with Morrow-ECI.

## Companion & knowledge extensions (not started)

Four capabilities for device-sharing and persistent user identity, none
built yet:

**Multi-user profiles.** Multiple users per device; a new name in
conversation offers to create a profile. Each profile is a separate
Parquet knowledge graph. Surface: Morrow-ECI profile picker. Backend:
Knowledge tier multi-profile routing.

**Voice recognition for user detection.** Speaker ID as the primary
detector (continuous, harder to spoof than camera alone), camera as a
fallback for ambiguous cases. Integration point: Sensory, before Impulse
fires. Needs baseline voice samples from the original user.

**Biometric + camera authentication.** Device biometrics authenticate
the original user at unlock; a different person picking up the device
triggers camera-based profile-creation. Surface: lock screen / auth
flow. Backend: a user-context field on Sensory's meta.

**Diary knowledge category.** A Knowledge category for entries that
accumulate rather than overwrite — recurring appointments, dated
milestones — so a new doctor's visit doesn't clobber the last one.
Storage: Archive, timestamp + profile ID. Query: Knowledge surfaces
diary entries in temporal order, not as overwriting facts.

These layer on top of the current system and don't block Milestones 1
or 2. Voice + biometric + camera compose as one "who is this" pipeline
feeding profile context, which diary-aware archiving then reads:

```
biometric unlock → voice/camera check → profile context →
diary-aware knowledge archiving
```

Profiles and auth are Morrow-ECI surface features; diary is an
Archive-schema + Knowledge-agent feature that can be prototyped
independently.
