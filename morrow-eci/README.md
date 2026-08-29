# Morrow-ECI

A Next.js/React/TypeScript scaffold for the companion app described in
the C# rebuild plan (`docs/csharp-rebuild-spec.md` — see the "Milestones"
and "Project layout" sections). This is the frontend companion surface
for the ECI-CAS backend, which lives one level up in this repo
(`src/EciCas.*`).

This pass builds the surface milestone's shell (read-only observer +
companion) and its visual states (expressions, thought bubbles, security
icon, speech bubble) against a **mock event feed** (`lib/mockTurn.ts`) so
the UI can be reviewed before any live bus wiring exists. The "+" doodle
is stubbed the same way — it depends on backend work (Memory milestone:
`EpochWritten` payload, `ui_click` source type, epoch dedup) not yet built.

## Running it

```bash
npm install
npm run dev
```

Open the page and click "Next stage" / "Next turn" to step through two
canned conversational turns: one clean pass, one that trips a security
yellow (one revision pass), then red on the revision itself.

## What this app is allowed to do (void-observer discipline)

Per the plan, this UI is a **void observer** on the bus — it subscribes
to `events.*` and never publishes, with exactly one sanctioned exception:
clicking the consolidation doodle fires a `Perception.ingest(source_type:
"ui_click")` event, and nothing else. Right now neither side is wired to
the real bus — `lib/mockTurn.ts` stands in for the subscription, and
`ConsolidationDoodle`'s click handler only logs what it would send (see
the comment in `components/ConsolidationDoodle.tsx`). When the surface
milestone's live wiring lands (an ASP.NET SSE endpoint as one more bus
subscriber), the mock feed is the thing that gets replaced; the component
contracts (`types/events.ts`) are written to match the real event shapes
already, so the components themselves shouldn't need to change.

Any future interaction idea for this app should stay inside that same
constraint: read from the bus freely, but the only path back in is the
one sanctioned click.

## Event → UI map

| Backend source | UI element |
|---|---|
| Impulse's reflex / drive-vector expression (`src/EciCas.Agents/Impulse`) | `Avatar` — bucketed expression, not continuous animation |
| Reasoning / Recall / Self advisories (shared keyword format) | `ThoughtBubbles` — three colored bubbles, persist faded after speaking |
| Security's verdict (`src/EciCas.Agents/Security`) | `SecurityIcon` — only renders on yellow/red; click reveals the matched rule's concern |
| Intent's advise/refuse output | `SpeechBubble` |
| `EpochWritten` (post-Memory milestone) | `ConsolidationDoodle` — clickable "+", first click reconciles, repeats are view-only |

## Open questions for Daniel

These are placeholder decisions made to have something concrete to
react to — none of them are settled:

- **Expression rendering** — right now `Avatar` is a colored circle +
  label per expression. Is that the right register, or should this be
  illustrated/literal facial features?
- **Bundle bubble colors** — Reasoning (blue) and Recall (orange) are
  carried over placeholders from the old console color table (now
  archived with the Python prototype); Self's fuchsia is likewise
  unanchored. There's no console subscriber yet in the C# rebuild to
  pin a real palette to — worth revisiting once one exists.
- **Doodle staleness** — a doodle can appear out of band with the
  current turn if consolidation runs off-path on a background worker.
  This mock always shows the doodle attached to the turn that produced
  it; the real UI needs a way to signal "this doodle is from N turns
  ago" without it reading as part of the current exchange.
- **Web vs. desktop shell** — left open by the plan; this scaffold is a
  plain Next.js web app, which assumes "web" is fine for now.

## Not in this pass

- No live bus connection — mock data only (`lib/mockTurn.ts`).
- No backend Memory/Surface milestone work (that's a separate, parallel
  track — see the plan's milestone list).
- No visual polish beyond "clearly reviewable" — placeholders are
  called out inline and above, not final design.

## Assumptions made while unblocking this pass (2026-08-29/30)

This scaffold predated the C# rebuild and referenced the old Python
agent names (Sensory/Analytics/Personality/Knowledge) and a Phase-0.8
Parquet "knowledge swarm" concept that never made it into the C# plan.
It also imported a `lib/mockTurn.ts` that didn't exist on disk, so the
app didn't build. Judgment calls made to unblock it, without Daniel
available to confirm:

- Renamed the three bundle agents to the current roster: Reasoning,
  Recall, Self (was Analytics/Knowledge/Personality).
- Dropped the `swarmNodes` / clickable-detail concept on the Recall
  bubble entirely, rather than porting it — the C# plan's Recall
  (§3.4) is a thin adapter over `IArchiveStore`, not a Parquet swarm,
  and nothing in the current plan calls for per-path drill-down in the
  UI. If Recall later needs that, it can come back.
- Wrote two brand-new mock turns from scratch (the original
  `mockTurn.ts` this scaffold's README described was never committed).
  Second turn exercises yellow-then-red on the *same* correlation to
  show the Blocked-notice path, since that's the one gating-matrix
  branch the first turn doesn't cover.
- Renamed the doodle's stub log line from `Sensory.ingest` to
  `Perception.ingest` to match the plan's agent rename (§1).
