# Morrow-ECI

A Next.js/React/TypeScript scaffold for the companion app described in
`docs/roadmap.md` (companion app section) and originally specced in
`docs/archive/v0-35-parallel-fanout-draft.md` §6/§6a-2. This is the
frontend companion surface for the ECI-CAS Python backend, which lives
one level up in this repo.

This pass builds M5's shell (read-only observer + companion) and M6/M7's
visual states (expressions, thought bubbles, security icon, speech
bubble) against a **mock event feed** (`lib/mockTurn.ts`) so the UI can
be reviewed before any live bus wiring exists. M8's "+" doodle is
stubbed the same way — it depends on backend work (M3: `EpochWritten`
payload, `ui_click` source type, epoch dedup) not yet built.

## Running it

```bash
npm install
npm run dev
```

Open the page and click "Next stage" / "Next turn" to step through two
canned conversational turns: one clean pass, one that trips a security
yellow/red and revises.

## What this app is allowed to do (void-observer discipline)

Per the spec, this UI is a **void observer** on the bus — it subscribes
to `system.control` / `events.*` and never publishes, with exactly one
sanctioned exception: clicking the consolidation doodle fires a
`Sensory.ingest(source_type: "ui_click")` event, and nothing else. Right
now neither side is wired to the real bus — `lib/mockTurn.ts` stands in
for the subscription, and `ConsolidationDoodle`'s click handler only
logs what it would send (see the comment in
`components/ConsolidationDoodle.tsx`). When M5's live wiring lands, the
mock feed is the thing that gets replaced; the component contracts
(`types/events.ts`) are written to match the real event shapes already,
so the components themselves shouldn't need to change.

Any future interaction idea for this app should stay inside that same
constraint: read from the bus freely, but the only path back in is the
one sanctioned click.

## Event → UI map

| Backend source | UI element |
|---|---|
| Impulse's reflex / drive-vector expression (`agents/impulse/agent.py`, `EXPRESSIONS`) | `Avatar` — bucketed expression, not continuous animation |
| Analytics / Personality / Knowledge bundle (shared keyword format) | `ThoughtBubbles` — three colored bubbles, persist faded after speaking |
| Security's verdict (`agents/security/agent.py`) | `SecurityIcon` — only renders on yellow/red; click reveals Intent's revision detail |
| Intent's ADVISE/REFUSE output | `SpeechBubble` |
| `EpochWritten` (post-M3) | `ConsolidationDoodle` — clickable "+", first click reconciles, repeats are view-only |

**Phase 0.8 update (backend):** Knowledge is no longer a live fan-out
subscriber — `agents/governance/buffer.py`'s `DEFAULT_WORKERS` is now
just `{Impulse, Analytics, Personality}`. Analytics instead proposes
`(category, topic)` paths, and Governance runs a deterministic swarm
query (`agents/governance/knowledge_swarm.py`) against a new
Parquet-backed structured store, then synthesizes the result back into
a `Knowledge`-labeled recommendation — so the bundle shape Intent (and
this UI) sees is unchanged, but the Knowledge bubble is now backed by
zero or more swarm nodes rather than one LLM narrative. `ThoughtBubbles`
reflects this: the Knowledge bubble is clickable when `swarmNodes` is
present, expanding to show each queried path with its match count and
a sample — mirroring how `SecurityIcon` reveals detail on click. Colors
for Analytics (blue) and Knowledge (orange) are pulled directly from
`tools/console.py`'s `COLORS` table rather than invented, so the companion
app and the console read as the same system.

## Open questions for Daniel

These are placeholder decisions made to have something concrete to
react to — none of them are settled:

- **Expression rendering** — right now `Avatar` is a colored circle +
  label per expression. Is that the right register, or should this be
  illustrated/literal facial features?
- **Personality's bubble color** — Analytics (blue) and Knowledge
  (orange) now match `tools/console.py`'s `COLORS` table; Personality
  has no console color to anchor to yet, so fuchsia here is still a
  guess. Worth picking one when Personality gets a console color, so
  the two surfaces stay in sync.
- **Doodle staleness** — `docs/ideas/consolidation-doodle.md` flags
  that `EpochWritten` fires off-path on a background worker, so a
  doodle can appear out of band with the current turn. This mock
  always shows the doodle attached to the turn that produced it; the
  real UI needs a way to signal "this doodle is from N turns ago"
  without it reading as part of the current exchange.
- **Web vs. desktop shell** — M5 leaves this open; this scaffold is a
  plain Next.js web app, which assumes "web" is fine for now.

## Not in this pass

- No live bus connection — mock data only (`lib/mockTurn.ts`).
- No backend M3 work (that's a separate, parallel track).
- No visual polish beyond "clearly reviewable" — placeholders are
  called out inline and above, not final design.
