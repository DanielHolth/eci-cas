# Phase 0.5 — v0.35 as built

**Status:** BUILT and tested, 2026-08-24.
**Implements:** `docs/ECI-spec-revisions-v0-35.md` (v0.35a–h), plus four
decisions Daniel made during implementation that the spec left open.
**Suite:** 438 passed, 13 skipped, 1 pre-existing unrelated failure
(`test_budget_mode.py::TestManifestConfig::test_it_reads_the_shipped_manifest`
— a spend-cap value that reflects Daniel's live-testing config, left
alone deliberately). Baseline before this phase was 288/13/1.

---

## What changed, in one picture

```
BEFORE (v0.34 / Phase 0.4)          AFTER (v0.35 / Phase 0.5)

Sensory                              Sensory
  └→ Impulse                           ├→ Impulse       ─┐
       └→ Governance                   ├→ Analytics     ─┤ parallel,
            └→ Analytics               ├→ Personality   ─┤ NO Governance
                 └→ Intent             └→ Knowledge     ─┘ hop
                      └→ Governance                        │
                           └→ Security                     ↓
                                └→ Action          Governance buffers all 4
                                                   and bundles them
   Security yellow → Analytics                            ↓
   Security red    → Analytics                          Intent
                                                          ↓
                                                   Governance → Security
                                                     green  → Action
                                                     yellow → Intent (Review)
                                                     red    → Intent (Revise,
                                                              one chance)
                                                     red #2 → Action (Blocked)
                                                          ↓
                                                   Governance → Consolidator
                                                   (once Action has run)
```

Eleven roles (was eight): Sensory, Impulse, Analytics, **Personality**,
**Knowledge**, Governance, Intent, **Consolidator**, Security, Action,
Archive. Three of them are new; one of the three was carved out of Intent.

---

## The four decisions Daniel made during implementation

The spec listed five "Still open" items. Four were answered on
2026-08-24; the fifth (whether the shared base class was worth building
now) was answered as part of the second.

**1. Analytics is isolated from Security in every way.** The spec draft
moved only the RED lane to Intent. Daniel widened it: *"analytics is
isolated from security in every way. thus both yellow and red goes to
intent now... analytics is only there to serve unbiased analytical
keywords to intent."*

What that meant in code: Analytics' `Task` enum went from three members
to one (`Evaluate`). Its `GATING_TASKS` / `FAIL_CLOSED_TASKS` sets are
gone, and so is its fail-closed fallback — a fail-closed path with
nothing to gate is dead code that reads like a safety property. The
discipline itself moved to Intent rather than being lost.

Analytics still sets `proceed` and `concern`. That is an *analytical*
judgment ("I don't think this is a good idea, and here's the one-line
reason"), not a security one, and Intent reads it out of the bundle to
choose between ADVISE and REFUSE exactly as before — v0.35c is explicit
that this half of the contract is unchanged. Loop detection is the other
place it comes from. **Worth confirming:** if you meant Analytics should
stop expressing `proceed` at all, say so — it is a small change, but it
would remove the refusal-voicing path and loop detection's only way to
decline, so it wasn't assumed.

**2. Security's outcome in the Consolidator bundle.** Daniel: *"I didn't
even know security had a concern text. if we have that sure, include it.
if not... both the status and the triggering prompt."* The rule engine
has no concern text of its own yet (SecurityMock only sets `verdict`), so
the bundle carries the verdict, the concern when one exists, and the full
revision arc — what Intent tried at each pass. `_verdict_detail()` in
`agents/intent/live.py` says `verdict: red` plainly when there is no
prose rather than inventing a reason Security never gave.

**3. Intent's conversation window is tier-scaled.** Daniel: *"we need to
make sure it is not cut off mid event... minimal is only 1 temp log,
budget is 5, default is 10, super is 15."* Implemented as
`roles.intent.context_events`, set by `budget/tiers.py`'s
`CONTEXT_EVENTS`. Cutting mid-event is structurally impossible: one
temp-log entry *is* one concluded event, so the window is bounded in
whole events. Individual entries are truncated per side (160 chars) so
the window always spans exactly N events regardless of how long any one
of them was.

**4. One chance to revise, then a blocked incident.** Daniel: *"only 1
additional pass... 'you can't do that, revise or get blocked'... if it
still fails on the 2nd try we'll just tweak some frustration into
impulse, send a security alert to the user and do an action sad/angry
face... make sure intent knows it has only 1 chance."*

Implemented as `contract.MAX_REVISION_PASSES = 1`, and the model is told
so twice — in `REVISE_RESPONSE_CONTRACT` ("This is your ONE chance...
there is no third attempt") and in the prompt body ("you get one attempt
to put this right"). A second red produces:

- a **deterministic notice** to Action (`type: "Blocked"`). Nothing
  model-authored may be spoken here, because nothing cleared Security —
  so the words are Governance's own template.
- `meta.expression` — a word from **Impulse's live appraisal state**
  (`angry` / `scared` / `sad` / `warm` / `alert` / `neutral`), read at
  that moment, never set by Governance. Daniel asked whether Impulse's
  existing wording could carry a blocked incident: it can't (its reaction
  vocabulary describes a reaction, it isn't speech), but its *state* can,
  and that is the better half of the idea — the face matches how the
  ecosystem actually feels rather than a canned sad emoji.
- `meta.security_alert: true`.
- a **frustration nudge** back into Impulse over the control plane
  (`urgency +0.15`, `fatigue +0.05`, `temperature −0.05`). Governance
  publishes the *fact*; Impulse owns what it does to its own vectors.
  It drifts back to baseline afterwards like any other displacement, so
  frustration fades rather than accumulating — and it still cannot
  manufacture a Critical severity (the Elevated ceiling holds).

---

## What was built, per spec section

### v0.35f/g — Consolidator (`agents/consolidator/`)

The former "Consolidating" mode of Intent, now a role with base/mock/live
tiers like every other. Owns the batch buffer, the threshold trigger,
epoch assembly, the Archive writes, and the Impulse recalibration
coupling.

- **Runs off the live dispatch path.** Daniel chose threading from day
  one, so the one event per batch that trips the threshold doesn't make
  the human wait on a slow reconciliation call. A single worker thread
  serializes reconciles by construction; `synchronous: true` runs inline
  instead, which is what every offline test fixture uses and what keeps
  the byte-identical-trace exit criterion checkable. `flush()` waits for
  in-flight work.
- **Multiple write instructions per pass** (v0.35g). One reasoning call
  over the whole batch may emit N writes, each naming its own store and
  tag; `ArchiveStore.execute_writes()` appends them and counts what it
  could not. An unknown store is dropped and counted, never rerouted —
  a misfiled memory is worse than a dropped one. The `knowledge` store,
  declared since v0.32 and never written to by anything, finally has a
  writer.
- **Source determines destination as a prompt-level default**, not a code
  rule — the spec is explicit that Consolidator may override for a
  misfit, so the judgment stays with the reasoner and the parse boundary
  only checks structural validity.
- **Fails open**, deliberately: it gates nothing, so a bad cycle writes
  an empty epoch and loses that batch to reconciliation, which is
  recoverable state loss rather than a safety event.

### v0.35g — Intent's persona cache

Hydrated once at construction, held in memory, refreshed on exactly one
signal: Consolidator's `EpochWritten` ping on `system.control`. Phase
0.4 called `hydrate()` — and therefore `archive.query("identity")` — on
*every* voicing call; that read is gone entirely. The ping is the only
coupling between the two agents: no shared mutable state, no direct
references, and the only shared durable thing is Archive, written only by
Consolidator.

Intent also lost the fleet/rotation model (`Awake → Consolidating →
ReadyToSwap`, `node_id`, the N=1 special case). `roles.intent.nodes` is
replaced by a flat `roles.intent.substrate`; a manifest still carrying
`nodes:` is told so at boot rather than having it silently ignored.

### v0.35b — Personality and Knowledge (`agents/archive_lookup/`)

One reusable class, two configurations, per Daniel's call to build the
shared base now. They differ only in which Archive store they read and
the wording of their brief; a third family member is one more
instantiation, not one more file — and a test asserts exactly that.

**Read-only by construction, not by convention:** they are handed a
`_ReadOnlyArchive` view that exposes `query` and nothing else, so there
is no write surface on the object to reach for by accident. Only
Consolidator gets the real `ArchiveStore`.

Mock-first per §13.1. The mock reports whether its store holds anything,
in the shared keyword format, and never claims relevance it cannot
assess — overstating that would make the fan-out tests pass for the wrong
reason.

### v0.35a/c — the fan-out and bundling

`Sensory.ingest()` publishes four envelopes — same `event_id`, same
verbatim content, one per worker — with no Governance hop. Impulse is
published to first, deliberately: on a synchronous bus its reflex is
already on the wire before the other three are dispatched, which is what
makes the Critical fast path fast.

Governance gained `agents/governance/buffer.py`: one `EventState` per
in-flight event, created on first sight, **destroyed when the event
concludes**. That is per-event state, not cross-event state — §5.1's
statutory reset survives with a sharpened definition, and the file says
so rather than quietly weakening the claim. No timeout is built, because
a partial bundle cannot stall a synchronous bus; the note explaining
which file would need one if the bus ever goes async is in that file.

**One bug worth recording, because it nearly shipped:** each worker
replies to its *own* copy of the event, so the bundle envelope is built
from whichever answer arrived last. If that one inherited `Neutral`
while Impulse raised `Elevated`, the escalation vanished. §3's
OR-upscale-only rule says a tag may be raised by anyone and lowered by
no one, so `EventState` now tracks the running maximum and the bundle
carries it. There is a test named after the failure.

### v0.35d — the Critical reflex

Now actually built, not just named. Impulse reading `Critical` routes
straight to Security through Governance, skipping the bundle and Intent's
voicing on the way in; the other three answers are discarded. A red on
that path revises through Intent like any other red. Impulse still cannot
open this path on its own — drive-vector state is capped at `Elevated`,
so only an external Sensory tag can.

### v0.35e — the veto and the correction pass

`agents/intent/contract.py` went from two registers to four. ADVISE and
REFUSE are unchanged in behaviour and degrade to the same deterministic
lines. REVIEW and REVISE **gate**, and every degraded path on them fails
closed: unparseable JSON, a missing `proceed`, an unreadable `proceed`
(`coerce_bool(default=False)`), a substrate outage, budget mode, or a
"revision" that merely restates what Security blocked.

The correction pass ran in the same commit as the code, per the spec's
"no new code sitting next to a stale claim":

| Where | Was | Now |
|---|---|---|
| `contract.py` docstring | "Intent holds no veto... no fail-closed asymmetry" | the four registers and where the asymmetry lives |
| `DEFAULT_CORE_ANCHORS["boundaries"]` | "You are advisory only" | Security is a hard stop; where it can't decide, the judgment is yours; one attempt to revise |
| `live.py` `DEFAULT_SYSTEM_INSTRUCTION` | "advisory only" | same correction |
| manifest `roles.intent.system_instruction` | "advisory only" | same correction |
| `routing.py` | v0.34 lane table | the v0.35 topology, with the reversal reasoned out |
| `README.md` | Phase 0.4 status | Phase 0.5 |

The persona's own boundaries mattered most: a persona whose
self-description misstates its authority would be reasoning from a false
premise on every single call. There is a test asserting each of those
strings is gone.

---

## Test files

| File | Covers | Tests |
|---|---|---|
| `test_phase05_consolidator.py` | batching, epochs, multi-writes, the worker thread, the persona cache | 32 |
| `test_phase05_archive_lookup.py` | the family shape, read-only posture, shared contract, bundle slot | 41 |
| `test_phase05_fanout.py` | fan-out, bundling, severity survival, Critical reflex, Consolidator hand-off | 26 |
| `test_phase05_intent_veto.py` | the routing reversal, fail-closed on every degraded path, one-chance revision, blocked incident | 40 |

Existing suites were updated in place rather than forked: the Phase 0.4
consolidation tests moved to the Consolidator file with the role, and
every hop-list assertion across `test_phase0_e2e.py`,
`test_phase01_governance.py`, `test_phase02_analytics.py` and
`test_phase04_intent.py` now describes the v0.35 topology.

---

## Hardening pass — six defects found and fixed after the build

The v0.35e change reverses a documented safety property, so the finished
work went through an adversarial review against the six invariants it is
supposed to hold. Six real defects came back. All are fixed, each with a
regression test named after the failure.

**1. The revision budget bounded RED only — yellow could live-lock.**
The worst of the six. Governance forwards whatever Intent writes to
Security for clearance, *including a fail-closed decline* — so a rule
engine that yellows a decline yellows it again, every time. On a
synchronous bus that isn't a slow loop, it is stack exhaustion inside a
single `ingest()` call: a scripted always-yellow Security produced a
`RecursionError` after 545 hops with nothing ever reaching Action. Now
**every non-green verdict spends one clearance attempt**, and the budget
is per event rather than per colour, so a yellow→red mix can't buy an
extra pass either.

**2. The bundle buffer leaked an entry per unroutable envelope.**
`on_event` called `buffer.get()` — which creates on miss — *before*
classifying, and released only on the path to Action. So every dropped
envelope minted a permanent `EventState` holding the user's verbatim
words. 1000 junk envelopes left 1000 entries. Now unroutable envelopes
are classified and dropped before any state is created, and a bounded
eviction (`MAX_IN_FLIGHT_EVENTS = 256`) turns the one remaining case — a
worker that isn't subscribed at all, so its events can never bundle —
into a counted diagnostic (`metrics["incomplete"]`, which was previously
initialised and never incremented) instead of unbounded growth.

**3. `CredentialsError` escaped Intent's fail-closed fallback.**
It is a *sibling* of `CompletionError` under `SubstrateError`, not a
subclass, and both providers build their client outside their own
try/except. So a key rotated away after boot — or an SDK that fails to
import lazily — raised something the `except (CompletionError, ...)`
clause let through, skipping `fallback_gated` on the exact registers
v0.35e added the veto to, and unwinding the whole synchronous pipeline.
Bootstrap's credential check is offline and one-shot; it cannot cover
this. All three live tiers now catch `SubstrateError`.

**4. An Action failure consolidated the same event twice.**
`emit()` publishes synchronously, so a failing Action re-enters
Governance and concludes the event from inside the frame that was about
to conclude it. Long-term memory double-counted the event and the batch
threshold tripped early. `_conclude` is now idempotent.

**5. Consolidator's worker could orphan a whole batch, silently.**
The worker exited on a 0.5 s idle timeout and was restarted lazily on
`is_alive()`. Between `get()` timing out and the thread actually dying,
`is_alive()` still reads True — so a job enqueued in that window got no
consumer, and 25 concluded events vanished from long-term memory with no
metric recording it. The window recurred every 0.5 s for the life of the
process. The worker now lives for the process and blocks on `get()`, and
a reconcile that raises no longer takes it down with it.

**6. Intent was shown the router's instruction instead of the request.**
The REVIEW and REVISE routes carried Governance's own instruction as
their payload — and Intent's prompt renders the payload as "THE HUMAN
SAID". So the agent deciding "unsure means no" was never shown what it
was deciding about; worse, REVISE quoted the *verdict envelope's* text,
sending Intent off to revise the phrase "Red — profanity". Both routes
now carry the original request (a new `sensory` content policy), with the
instruction, the blocked proposal and Security's concern in meta where
they can be attributed correctly.

Two things the review could **not** break, worth recording: the
green-only path to Action (invariant 1) and Impulse's Elevated ceiling
(invariant 4). Both held under every probe.

Noted but not changed: `_ReadOnlyArchive` is a barrier against accident,
not against a determined caller (`agent.archive._archive` reaches the
real store). That is the right level for an in-process ecosystem —
worth revisiting if these ever become separate processes.

## Still open

1. **Whether Analytics should stop expressing `proceed` entirely** — see
   decision 1 above. Currently kept, as an analytical judgment.
2. **A live tier for Personality/Knowledge.** Mock-first was the call;
   the shape is proven and the substrate slot is declared in the
   manifest. Real retrieval logic is the next phase's work.
3. **Security's own concern text.** The rule engine is still a mock that
   always clears. When the real one lands, `meta.security_concern` is
   already plumbed end to end — Governance carries it, Intent's prompt
   attributes it separately from Analytics' `concern`, and Consolidator
   files it under `knowledge:security`.
4. **The blocked notice's wording** is a Governance template. If the
   product layer wants something warmer, that is a one-line change in
   `routing.template_content` — but it must stay deterministic, because
   nothing on that path cleared Security.
