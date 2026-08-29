# The clickable consolidation doodle

Status: idea, not built. Captured 2026-08-24 from Daniel, during the
Phase 0.6 session, so it survives into whoever picks this up next.

## The idea

When Consolidator finishes a pass, the result should be able to surface
to the human as a small clickable thing — a doodle, a card, a thought
bubble in the avatar UI. Clicking it does two things: it shows the human
what was just learned, and it feeds a new event back into Sensory:

> "the user checked out what we just learned"

That second half is the interesting one. It closes a loop the system
currently doesn't have — right now consolidation is entirely one-way.
Memory gets written and nothing ever comes back to say whether anyone
cared.

## Why it fits the architecture rather than bolting onto it

Three pieces already exist and line up:

* **`ConsolidationWritten` on `system.control`**, published by
  `ConsolidatorBase._execute_writes()` after any write pass that actually
  wrote something (Phase 0.9 removed the old epoch/batch model —
  Consolidator writes per-event now, so this is keyed on the writing
  event's `event_id`, not an epoch id). A UI layer subscribing to that
  topic is a void observer — reads, never publishes — which is the same
  pattern `tools/console.py` already uses and the same discipline the
  spec asks of anything watching the bus.
* **`ArchiveWritten` on `system.control`** (Phase 0.6, see
  `agents/archive/agent.py`) makes individual writes observable too.
* **The click going back in through `Sensory.ingest(...)`** is correct by
  §5.2 rather than a convenient hack. Sensory is "an input field plus
  source-tagging", and a click genuinely IS a perception — unlike a
  budget-mode command, which is control-plane state and therefore
  deliberately lives in the console instead. Impulse then colours the
  event as it colours any other; curiosity satisfied, or whatever the
  drive vectors make of "the human looked".

## Daniel's dedup rule (2026-08-24)

Stated directly, because it's the part most likely to be lost:

> If ever it's reconciled, it should be as an "oh, the user found it
> interesting that I share what I've learned". After that it's a
> duplicate and Consolidator should ignore it.

So: the **first** click referencing a given consolidation write-pass
(its `event_id`) is a real signal and should reconcile as one. Every
subsequent click on the same one is a duplicate and Consolidator drops
it. Not "the same content again" — literally the same write-pass being
revisited.

This also happens to close the feedback loop that would otherwise be a
problem: consolidation → doodle → click → event → back into
Consolidator. The dedup rule makes it terminate by construction — a
click's own write-pass (if it has one) is a new event_id, and only a
*repeat* click on the same referenced event_id is suppressed.

## What was built (2026-08-29)

1. **`ConsolidationWritten` payload.** `agents/consolidator/base.py`'s
   `_execute_writes()` publishes it on `system.control` after any pass
   that actually wrote something — event_id plus one human-readable
   summary line, built with `agents/governance/knowledge_swarm.py`'s
   `format_for_intent` rather than re-deriving the format.

2. **A `source_type` for UI interaction.** `ui_click` joins Sensory's
   `VALID_SOURCE_TYPES` (`agents/sensory/agent.py`); `Sensory.ingest()`
   takes an optional `ref_event_id` that names which consolidation
   write-pass the click is about (lands in the fan-out's `meta`).

3. **Event-level dedup in Consolidator.** `ConsolidatorBase.on_event()`
   checks `meta["source_type"] == "ui_click"` and `meta["ref_event_id"]`
   against an in-memory set of already-acknowledged event_ids; a repeat
   is a no-op (no substrate call, no writes). In-memory only — losing it
   on restart is recoverable state loss, consistent with Consolidator's
   existing fail-open posture.

4. **A UI surface.** Still out of scope for this repo. The seam is
   `system.control` for the outbound half and `Sensory.ingest` for the
   inbound half; Morrow-ECI's `ConsolidationDoodle.tsx` wires the click
   (Milestone 2).

## Open question

Async ordering. The reconcile runs off the live dispatch path on a
background worker (`synchronous: false`), so the doodle appears out of
band with the conversation. Fine for a GUI, awkward in a console. Worth
knowing which surface this is for before designing the timing.
