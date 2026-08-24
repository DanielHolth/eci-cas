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

* **`EpochWritten` on `system.control`** is already published by
  `ConsolidatorBase._run()` at the end of every pass. It exists today as
  the Intent persona-cache refresh ping. A UI layer subscribing to that
  topic is a void observer — reads, never publishes — which is the same
  pattern `tools/console.py` already uses and the same discipline the
  spec asks of anything watching the bus.
* **`ArchiveWritten` on `system.control`** (new in Phase 0.6, see
  `agents/archive/agent.py`) makes individual writes observable too, at
  a finer grain than the epoch.
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

So: the **first** click on a given epoch is a real signal and should
reconcile as one. Every subsequent click on the same epoch is a
duplicate and Consolidator drops it. Not "the same content again" —
literally the same epoch being revisited.

This also happens to close the feedback loop that would otherwise be a
problem: consolidation → doodle → click → event → concludes → back into
Consolidator's batch → consolidation. Harmless at `batch_size: 25`, but
the dedup rule makes it terminate by construction rather than by
arithmetic.

## What would need building

1. **`EpochWritten` needs a payload.** Today it is a ping — the content
   is the string `"epoch consolidator_cycle-N written"`. The doodle needs
   at minimum an epoch id and one human-readable line to render. That
   summary has to be authored by the live Consolidator tier, which means
   this idea is coupled to Consolidator's live reasoning and can't fully
   land before it.

2. **A `source_type` for UI interaction.** Sensory already tags source
   types (`prompt`, and the diagnostic pings). A click wants its own tag
   — something like `ui_click` — so that downstream can tell "the human
   typed something" from "the human looked at something".

3. **Epoch-level dedup in Consolidator.** The record for a click event
   carries the epoch id it refers to; `observe()` (or the reconcile pass)
   drops a click record whose epoch id has already been acknowledged.
   Cheap: one set of seen epoch ids, persisted with the epoch itself.

4. **A UI surface.** Out of scope for this repo. The seam is
   `system.control` for the outbound half and `Sensory.ingest` for the
   inbound half; nothing about the doodle needs to reach into the
   pipeline.

## Open question

Async ordering. The reconcile runs off the live dispatch path on a
background worker (`synchronous: false`), so the doodle appears out of
band with the conversation. Fine for a GUI, awkward in a console. Worth
knowing which surface this is for before designing the timing.
