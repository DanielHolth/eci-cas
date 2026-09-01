# Morrow-ECI

A Next.js/React/TypeScript scaffold for the companion app described in
the C# rebuild plan (`docs/architecture.md`). This is the frontend companion surface
for the ECI-CAS backend, which lives one level up in this repo
(`src/EciCas.*`).

The surface milestone (M5) is now wired to the real backend: `lib/api.ts`
posts to `POST /api/perceive`, and `lib/useEciStream.ts` opens
`GET /api/stream` (an SSE feed off `EciCas.Host`, see
`src/EciCas.Host/Program.cs`) and reduces the raw envelope feed into the
`TurnEvent` shape the components already expected from the mock-era pass.
The mock feed (`lib/mockTurn.ts`) that stood in for it during the earlier
scaffold-only pass has been removed now that a real feed exists. The "+"
doodle stays client-side-only
(see `components/ConsolidationDoodle.tsx`) — no backend endpoint was built
for `source_type: "ui_click"` ingestion.

## Running it

```bash
npm install
npm run dev
```

Requires `EciCas.Host` running (`dotnet run --project ../src/EciCas.Host`)
on `http://localhost:5179` — override via `NEXT_PUBLIC_ECI_API_BASE` if it's
running elsewhere. Type into the input box and press Send; the avatar,
thought bubbles, security icon, and speech bubble update live as envelopes
arrive over SSE.

## What this app is allowed to do (void-observer discipline)

Per the plan, this UI is a **void observer** on the bus — it subscribes
to `events.*` and never publishes directly to it. It does now have one
sanctioned way in: `POST /api/perceive`, which is exactly what typing at
the console REPL does on the backend side (`PerceptionAgent.Perceive`) —
not a second privileged path. The consolidation doodle's click was the
plan's other sanctioned exception (`Perception.ingest(source_type:
"ui_click")`), but that stays unbuilt on the backend (see
`components/ConsolidationDoodle.tsx`) — a scope decision, not an oversight.

Any future interaction idea for this app should stay inside that same
constraint: read from the bus freely via SSE, write only through
`/api/perceive`.

## Event → UI map

| Backend source | UI element |
|---|---|
| Perception's `perception.text` | `Utterance` — the person's own line, echoed opposite the persona so a turn on screen reads as an exchange |
| Impulse's reflex / drive-vector expression (`src/EciCas.Agents/Impulse`) | `Avatar` — a drawn face, one bucketed pose per expression; the animation is decoration on top, not extra state |
| Reasoning's selected pairs, Recall's picked rows, Self's advice | `ThoughtBubbles` — three colored bubbles, persist faded after speaking. Each faculty thinks in its own shape (pairs / rows / a line of text), so each has its own reader in `useEciStream` that collapses it to one terse string |
| Security's verdict (`src/EciCas.Agents/Security`) | `SecurityIcon` — only renders on yellow/red; click reveals the matched rule's concern |
| Intent's advise/refuse output | `SpeechBubble` — dashed amber when Governance marks the turn `governance.degraded`, i.e. thought with a substrate missing |
| `EpochWritten` (post-Memory milestone) | `ConsolidationDoodle` — clickable "+", first click reconciles, repeats are view-only |

## Open questions for Daniel

These are placeholder decisions made to have something concrete to
react to — none of them are settled:

- **Expression rendering** — `Avatar` is now a drawn face: hand-written
  SVG geometry (brow angle, eye openness, mouth path) plus CSS keyframes,
  no sprite sheet and no animation library. Two motion layers, kept
  separate on purpose — an idle layer (breathing, blinking, pupil drift)
  that never stops so the persona is alive between turns, and an
  expression layer that belongs to the current mood (a tremble for
  scared, a slow droop for sad, a seething shudder for angry). Poses
  transition rather than cut, and all motion is suppressed under
  `prefers-reduced-motion` — the mood stays legible from the static pose,
  which is why it's encoded in geometry rather than in movement. Adding a
  seventh mood is a row in `FACE`, not a redraw. Still a guess at the
  register: is a cartoon face right, or should this be more abstract?
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

- No `ui_click` write path — the doodle acknowledges locally only.
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

## Assumptions made while wiring M5 (2026-08-30)

Made autonomously while Daniel was asleep, per his standing "keep going,
document assumptions" instruction:

- **Kestrel port fixed at `http://localhost:5179`** via
  `Surface:Url` config, rather than leaving it to `launchSettings.json`
  defaults — `EciCas.Host` has none (it started as a console app), and a
  stable documented port is needed for this app's default `API_BASE`.
- **JSON wire casing is camelCase** for both property names (`eventId`,
  not `EventId`) and enum values (`"green"`, not `"Green"`) — idiomatic
  for a TS consumer, configured in `EciCas.Host/Program.cs`'s
  `jsonOptions`.
- **Expression derivation is entirely client-side and heuristic**
  (`lib/useEciStream.ts: deriveExpression`) — the backend's Impulse agent
  has no concept of `Expression` at all, only a severity + free-text
  advice string. This mapping is a placeholder, not a spec.
- **Bundle findings are upserted, not appended**, keyed by agent name —
  `ThoughtBubbles` renders one row per `BundleAgent` via `f.agent` as a
  React key, so a second advisory from the same agent (e.g. a revision
  pass) replaces its row rather than duplicating it.
- **A consolidation epoch attaches to whichever turn is open on this
  client when `system.control` "Written" arrives** — there's no
  correlation between Consolidator's batch-write announcement and any
  single triggering turn (it's a batch over N turns), so "the current
  turn" is a simplification pending the "doodle staleness" open question
  above.
- **`ConsolidationDoodle` stays client-side-only** — no backend endpoint
  exists for `source_type: "ui_click"`, and building one wasn't asked for.
- **Known cosmetic quirk:** the "Live"/"Disconnected" label in `app/page.tsx`
  reflects `EventSource.onopen`, which in some network setups doesn't fire
  until the first SSE message actually arrives rather than as soon as
  headers land — so it can read "Disconnected" while genuinely connected
  and simply idle. Confirmed functionally working end-to-end (browser
  verification: typing a message correctly drove Avatar/ThoughtBubbles/
  ConsolidationDoodle from live backend envelopes); the label itself is
  just not a reliable idle-state indicator yet.
