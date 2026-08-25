# ECI-CAS — Roadmap: current build → v1 product

Old §14 roadmap (v0.32) is retired, not just superseded. It was built
around an N-node Intent fleet doing `Awake → Consolidating →
ReadyToSwap` rotation for zero-downtime and multi-substrate diversity.
v0.35 replaced that model entirely: Consolidator split off, Intent is
always-active, no state machine, no fleet. There is no N to scale.
What survives from the old "Phase 3" idea is just "different roles can
point at different substrate classes" — already possible today, one
manifest edit.

Each milestone below is scoped to fit one Cowork session, the same
grain as Phases 0.5/0.6.

---

## Backend — finishing the 8(11)-role system

**M1 — Consolidator live tier**
Give Consolidator real substrate-backed reasoning over a batch instead
of templated epochs. Highest-value item still open — nobody has watched
its real behavior end to end. Unblocks the doodle (needs `EpochWritten`
to carry a real payload).

**M2 — Security rule tuning pass**
Feed real/scripted traffic through the 12 rules, check the green/
yellow/red distribution against §5.6's ~90/9/1 target, adjust
`config/security_rules.json`. No code change expected — a calibration
session, not a build session.

**M3 — Consolidation doodle backend half**
`EpochWritten` payload (epoch id + human-readable line), `ui_click`
source type on Sensory, epoch-level dedup in Consolidator. Doc already
exists (`docs/ideas/consolidation-doodle.md`) — this milestone just
builds it. This is also most of the backend the avatar app's "+" icon
needs (see M6).

**M4 — Cleanup pass**
`spoken.jsonl` rotation, decide whether Analytics keeps expressing
`proceed`, any regressions from M1–M3. Small, deliberately a buffer
milestone.

---

## Avatar app — new surface, backend-informed by the design already on file

The concept is already fully specified in
`docs/ideas/v0-35-parallel-fanout-draft.md` §6 — this isn't new design,
it's build-out. Mapping your ask to what's already spec'd:

| Your ask | Spec'd as | Backend source |
|---|---|---|
| Impulse → facial expression | avatar driven by Impulse's live reflex | `meta.reflex`, drive vectors |
| Analytics/Personality/Knowledge → thought bubbles | 3-colored typing thought bubble, one color per agent | Governance's bundled fan-out input |
| Security yellow/red → clickable icon | security-fail icon with its own bubble | `meta.verdict`, Intent's `Revise`/refusal text |
| Speech bubble | Intent's output specifically | Intent's ADVISE/REFUSE output |
| "+" icon on useful knowledge | not literally spec'd as "+", but *is* the consolidation doodle | `EpochWritten` (needs M3) |

**M5 — Read-only bus observer + static avatar shell**
A small app (web or desktop) that subscribes to `system.control` /
`events.*` as a void observer — same discipline as `tools/console.py`.
No expressions yet, just prove the wiring: one event flows in, the
observer sees Impulse's reflex, Analytics/Personality/Knowledge's
bundle, Security's verdict, and Intent's final line, and prints them.

**M6 — Facial expressions + thought bubbles**
Map Impulse's reflex/drive-vector state to a small fixed set of
expressions (same discipline as the 9-string `REACTION_VOCABULARY` —
don't invent continuous animation, bucket it). Render Analytics/
Personality/Knowledge's three inputs as three colored typing bubbles
before the avatar speaks.

**M7 — Security icon + speech bubble**
Clickable icon appears on yellow/red verdicts; click reveals what
Intent tried and why it was stopped (Intent's `Revise`/refusal
content). Final speech bubble renders Intent's actual output. This is
the milestone that makes the avatar *feel* alive rather than just
decorative.

**M8 — The "+" doodle, wired to the avatar**
Depends on M3 (backend payload) being done first. `EpochWritten`
surfaces as a clickable "+"; click shows the epoch's human-readable
line and fires the click back through `Sensory.ingest(source_type:
ui_click)`, closing the loop per Daniel's dedup rule (first click
reconciles, repeats are dropped).

---

## Suggested order

```
M1 (Consolidator live) ──┬─→ M3 (doodle backend) ──→ M8 (doodle UI)
                          │
M2 (Security tuning) ─────┤
                          │
M4 (cleanup) ─────────────┘

M5 (observer shell) → M6 (expressions/bubbles) → M7 (security/speech)
```

M1–M4 (backend) and M5–M7 (avatar shell → basic UI) can run in
parallel tracks if you want two threads going — neither blocks the
other until M8, which needs M3 finished first.

Nothing here is a spec revision by itself; each milestone gets its own
`as-built.md` the same way 0.1–0.7 did, and v0.35's numbering can just
continue (0.8, 0.9, ...) rather than reviving "Phase 1/2."
