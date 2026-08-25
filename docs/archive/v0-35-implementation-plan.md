# v0.35 Implementation Plan — Phase 0.5

**Status:** PLAN — for Daniel's review before any code changes.
**Basis:** `docs/ECI-spec-revisions-v0-35.md` (design-complete) +
`docs/ideas/v0-35-parallel-fanout-draft.md`, against the Phase 0.4
codebase (baseline reproduced 2026-08-24: 288 passed, 13 skipped, 1
known pre-existing failure in `test_budget_mode.py` — Daniel's
live-testing spend-cap config, untouched throughout).

**Decisions already made by Daniel (2026-08-24, this session):**

1. Personality and Knowledge are **mocked first**, per §13.1 precedent.
2. The shared archive-lookup **base class is built now** (`agents/archive_lookup/`),
   not deferred until a third agent of the family shows up.
3. Consolidator's heavy reconcile call runs on a **background thread from
   day one** — the live response never waits on it.

Two of the spec's five "Still open" items remain genuinely open and are
flagged inline where they bite (marked **ASK**): the exact shape of
Intent's broader-conversation-context input, and the final confirmation
that Security's concern text rides in the Consolidator bundle (settled
in practice by v0.35g's bundle contents; wants one explicit yes).

---

## Overview: four steps, in the spec's suggested order

| Step | Spec | What ships | Risk |
|---|---|---|---|
| 1 | v0.35f/g | Consolidator split out of Intent; persona caching; multi-instruction Archive writes; background-thread reconcile | Medium — touches Intent, but no topology change |
| 2 | v0.35b | `agents/archive_lookup/` base + Personality + Knowledge (mock tier), new bus topics | Low — additive, nothing routes to them yet |
| 3 | v0.35a/c | Sensory 4-way fan-out, Governance buffering/bundling, Analytics output redirected into the bundle, Critical fast path | High — the topology break |
| 4 | v0.35e | Security red → Intent `Revise` (fail-closed), Analytics severed from Security, stale-claim correction pass | Safety-critical — own pass, own test file |

Each step: implement → `python -m pytest tests/ -q` green (baseline +
that step's new dedicated test file) → update `docs/` → only then the
next step. As-built docs (`docs/phase-0.5-*.md` + Project mirror) are
written once each piece is actually built and tested, not before.

Steps 1 and 2 are independent of each other and of the topology break;
they can land and soak individually. Step 3 depends on 2 (needs
Personality/Knowledge to fill two bundle slots). Step 4 depends on 3
only in the sense that Intent's Revise output re-enters Governance's
bundle-era routing table; its contract work is independent.

---

## Step 1 — Consolidator split (v0.35f/g)

### 1.1 New package: `agents/consolidator/`

Mirroring the established base/mock/live split:

- `agents/consolidator/base.py` — `ConsolidatorBase`: batch buffer,
  batch-size trigger (the moved `_events_since_consolidation` mechanism),
  epoch/write-instruction assembly, the recalibration hand-off to
  Impulse, and the threading harness (1.4). Subclasses supply one thing:
  `reconcile(batch, prior_epochs) -> ConsolidationResult`.
- `agents/consolidator/agent.py` — `ConsolidatorMock`: templated empty
  result, `decided_by="deterministic"`, proves the write path at zero
  cost. (Direct port of today's `IntentMock.reconcile`.)
- `agents/consolidator/live.py` — `ConsolidatorAgent`: the moved body of
  `IntentAgent.reconcile()` / `_consolidation_prompt()` /
  `_parse_consolidation()`, on the `consolidation_substrate` class,
  budget-aware, same fallback posture (empty templated epoch on any
  failure — consolidation never gates anything, so it stays fail-open).

`ConsolidationResult` moves from `agents/intent/base.py` to the new
package (with a re-export shim or import update at every use site —
grep shows only intent/* and tests import it today).

**Interim wiring (this step only):** no new bus topic yet. Per the
spec's handover note, Intent calls Consolidator **directly, in-process**:
`IntentBase.on_event` hands each event's record to
`consolidator.observe(record)` after publishing its advice. The
Governance-sends-one-bundle-after-Action flow (v0.35g's settled design)
arrives in Step 3, when Governance actually has the whole event arc to
bundle. This keeps Step 1 self-contained and the trace shape unchanged.

### 1.2 What Intent loses, what it keeps

**Loses** (moves to Consolidator): `reconcile()`, `_consolidate()`,
`_events_since_consolidation`, `_cycle`, epoch writing,
`_apply_recalibration` (the Impulse coupling now lives in
ConsolidatorBase — still exactly one place), `consolidation_substrate`,
`CONSOLIDATION_RESPONSE_CONTRACT`.

**Loses too** (v0.35f): the node/rotation surface — `node_id`,
`self.state = "Awake"`, the `nodes[0]`-selection logic in bootstrap.
Manifest `roles.intent.nodes` is replaced by a flat
`roles.intent.substrate` (same shape as Analytics); `rotation.batch_size_events`
moves to `roles.consolidator.batch_size_events`. §7's whole
fleet/rotation chapter is superseded, so the code stops carrying its
vocabulary. The `node_id` field in epoch records becomes a fixed
`"consolidator"` source tag; `meta.node_id` on Intent's hops is dropped
(tests that assert it get updated in this step's sweep).

**Keeps:** the temp log (it is Intent's cross-event conversational
context, needed for v0.35c/e — **ASK**: bounding/window is open item 2),
`voice()`, both registers, the budget gate, all contract surface.

**Gains — persona caching (v0.35g):** `hydrate()` runs once in
`__init__` and the result is held as `self._persona`; `voice()` uses
the cached copy and never touches Archive. Refresh mechanism: after
ConsolidatorBase writes an epoch it publishes a `system.control`
envelope (`type="EpochWritten"`, source `Consolidator`); Intent
subscribes to `system.control` and re-hydrates on it. Control-plane,
zero business-queue footprint, and no shared mutable state between the
two agents — the only coupling is Archive plus a ping that says "Archive
changed". (The alternative — a direct callback — was rejected as a
hidden object reference between two agents the spec says share nothing.)

### 1.3 Archive: multi-instruction writes (v0.35g Option B)

`ConsolidationResult` gains `writes: List[WriteInstruction]`, each fully
specifying destination — `store` (`knowledge` | `identity`), `tag`
(`general` | `security` | `epoch` | ...), and `content`. ArchiveStore
gains one thin method:

```python
def execute_writes(self, instructions) -> int   # N mechanical appends
```

— a loop over the existing `write(kind, record)`, nothing more. Archive
stays a dumb executor; validation of store names happens at the
Consolidator parse boundary (unknown store → that instruction dropped
and counted in diagnostics, same clamp-at-the-boundary discipline as
recalibration). The identity-epoch write remains what it is today;
knowledge writes land in `knowledge_store.json` with their tag — the
first real writer that store has ever had.

Source-determines-destination (Sensory→knowledge, Intent→identity,
security events→knowledge:security) is implemented as the **default in
the live tier's prompt**, not as code — the spec is explicit that
Consolidator may override for a misfit, so the rule belongs in the
reasoning pass, with the parse boundary only checking structural
validity.

### 1.4 Background-thread reconcile (Daniel's decision)

- The batch-threshold trip enqueues a reconcile job and returns
  immediately; a single worker thread (one, ever — reconciles are
  serialized by construction) runs `reconcile()` and then the epoch
  write + `EpochWritten` ping.
- The batch buffer is swapped out atomically under a small lock before
  the thread starts (same swap idiom as today's
  `temp_log, self._temp_log = self._temp_log, []`), so the live path
  never blocks on the worker.
- Archive single-writer discipline holds: identity/knowledge writes come
  only from the worker thread; the bus thread only appends to the queue
  JSONL (different files, append-only) and `drive_vectors.json` (Impulse).
  The one genuinely shared file is none — no lock needed in ArchiveStore
  itself yet; noted in the code where that assumption lives.
- **Determinism for tests:** `ConsolidatorBase(..., synchronous=True)`
  runs the job inline (test fixtures and the Phase 0 byte-identical-trace
  e2e use this); the threaded path gets its own tests via a
  `join(timeout=...)`-able handle (`consolidator.flush()` — waits for
  the in-flight job, used by tests and by a clean shutdown).
- Bootstrap provisions Consolidator with `synchronous=False` by default;
  manifest knob `roles.consolidator.synchronous: true` for debugging.

### 1.5 Manifest + bootstrap + tools

- `manifests/ecosystem-manifest.yaml`: new `roles.consolidator` block
  (`tier: cognitive`, `mock`, `substrate` — defaulting to the old
  `consolidation_substrate` value, `batch_size_events`, `synchronous`);
  `roles.intent` loses `nodes`/`rotation`/`consolidation_substrate`,
  gains flat `substrate`.
- `budget/tiers.py`: presets' `consolidation` slot now writes
  `roles.consolidator.substrate` (and `roles.consolidator.mock: false`);
  `intent_live` writes `roles.intent.substrate`. Every named tier sets
  `roles.consolidator.mock` explicitly, same stale-flag discipline as
  the existing roles.
- `recovery/bootstrap.py`: `_provision_intent` simplified (no node
  selection, no consolidation substrate); new `_provision_consolidator`
  (mock/live selection, credential check as WARNING-not-stop, same
  posture as today's consolidation-substrate handling — a dead
  consolidation model must not block the live pipeline). `Ecosystem`
  gains a `consolidator` field. Boot log line updated (9 roles).
- `tools/preflight.py`: `COGNITIVE_ROLES` gains `consolidator`.

### 1.6 Tests — `tests/test_phase05_consolidator.py`

Batch trigger fires at threshold; mock writes a well-formed epoch;
live-tier parse handles multi-instruction output (echo provider);
unknown-store instructions dropped with diagnostics; fail-open fallback
on substrate failure; recalibration still clamped to ±0.2 and reaches
Impulse's baseline; persona cache: Intent makes zero Archive queries
across N voiced events, then re-hydrates exactly once after
`EpochWritten`; threaded mode: live response returns before reconcile
completes, `flush()` converges, two threshold trips serialize; budget
mode gates Consolidator's substrate call. Plus the suite-wide sweep for
fixtures that pin `roles.intent.*`/nodes (grep precedent:
`roles.intent.mock`).

---

## Step 2 — Personality + Knowledge, mock-first, shared base (v0.35b)

### 2.1 New package: `agents/archive_lookup/`

- `base.py` — `ArchiveLookupBase`: subscribes to its own topic,
  queries exactly one Archive store (`self.store_kind`), **read-only by
  construction** (holds no reference to any write method — it takes the
  archive object but only ever calls `query`; enforced by a test, not
  just a docstring), no cross-event state, no persona. Emits its
  findings to Governance (Step 2 interim: emits nowhere/loggable no-op
  until Step 3 wires the bundle — see 2.3).
- `contract.py` — the shared **keyword-findings format**, explicitly
  matching Analytics' terse keyword style (load-bearing for the
  three-bubble UI): `{"findings": "<keywords/short phrase>", "relevant": true|false}`,
  parse + deterministic fallback (`relevant: false`, empty findings —
  a lookup agent with nothing to say fails toward silence, gating
  nothing). Instances differ only in `store_kind` + system instruction.
- `agent.py` — `ArchiveLookupMock`: templated finding, zero cost.
- `live.py` — deferred to a later phase (mock-first decision); the file
  is NOT written now. The base/contract shape is proven by the mock
  exactly the way Phase 0 proved every other role.

Concrete instances are configuration, not subclasses:
`ArchiveLookupMock(bus, archive, role="Personality", store_kind="identity", topic="events.personality")`
and the Knowledge twin on `store_kind="knowledge"`. A future third
family member is one more instantiation.

### 2.2 Bus + manifest + bootstrap

- `bus/pubsub.py`: `BUSINESS_TOPICS` gains `events.personality`,
  `events.knowledge` (and Step 3 will use them in the fan-out).
- Manifest: `roles.personality`, `roles.knowledge`
  (`tier: cognitive, mock: true`, future `substrate` slot documented).
- Bootstrap: `_provision_archive_lookup(role, ...)` — one method, called
  twice. Ecosystem gains both handles.

### 2.3 Interim behaviour (until Step 3)

Nothing publishes to their topics yet, so in Step 2 they are provisioned,
subscribed, and exercised only by their own test file — additive, zero
trace change. This mirrors how `local-fast` sat unused-but-proven in the
manifest before anything called it.

### 2.4 Tests — `tests/test_phase05_archive_lookup.py`

Read-only invariant (a lookup agent never calls any Archive write
surface across a full event); identity instance sees anchors/epochs,
knowledge instance sees the knowledge store (seeded via Consolidator
writes from Step 1 — the first end-to-end proof that store has a writer
and a reader); shared keyword format parses; fallback fails toward
silence; both instances are the same class configured twice.

---

## Step 3 — The fan-out and the universal router (v0.35a/c/d)

The topology break. Everything lands in one step because the routing
table is one source of truth and half-migrating it would leave the
worked example in `routing.py`'s docstring false either way.

### 3.1 Sensory (v0.35a)

`ingest()` publishes four envelopes per event — same `event_id`, same
verbatim content — to `events.impulse`, `events.analytics`,
`events.personality`, `events.knowledge`. No Governance hop on this
fan-out (the one deliberate exception, quoted in the module docstring).
Impulse stops being "sole trigger into Governance"; its docstring claim
and the v0.31 relay language get corrected in the same commit.

### 3.2 The four workers' outputs re-target Governance

- **Impulse**: unchanged mechanics; its reply (reflex + vectors +
  severity) already goes to Governance — now as bundle slot, not relay
  trigger.
- **Analytics**: `AnalyticsBase.emit()` re-targets
  `Governance`/`events.governance` (type `Recommend`) instead of Intent
  directly. Its reasoning, tasks, and `proceed`/`concern` semantics are
  untouched — only the recipient changes (v0.35c: "output redirected,
  not re-reasoned"). NOTE: Analytics' Evaluate prompt currently gets
  Impulse's reflex via `meta.reflex`; under the fan-out Analytics sees
  the raw Sensory copy, which has no reflex — the prompt already treats
  reflex as optional, and Intent (which gets the whole bundle) is where
  the reflex is synthesized now. Called out so nobody reads the missing
  reflex line as a regression.
- **Personality/Knowledge**: emit findings to Governance (their
  `contract` gains the emit target once, in the base).

### 3.3 Governance: buffering + bundling (v0.35c)

New trigger class `WORKER_REPORT` (source ∈ {Impulse, Analytics,
Personality, Knowledge}) and a `BundleBuffer` keyed by `event_id`:
collects the four slots, and on the fourth publishes ONE bundled
envelope to Intent —

```
content: the original Sensory content (verbatim)
meta.bundle: { impulse: {reflex, drive_vectors, severity},
               analytics: {recommendation, proceed, concern, ...},
               personality: {findings, ...},
               knowledge: {findings, ...} }
```

Intent reads `proceed`/`concern` from the analytics slot — that half of
the ADVISE/REFUSE contract is unchanged, just relocated into the bundle.

**Statelessness note, named honestly in the docstring:** this is
Governance's first per-event *held* state. It is still no *cross*-event
state — the buffer entry lives and dies inside one `event_id` — so
§5.1's statutory reset survives with a sharpened definition ("no
decision may depend on a previous event"), and the docstring says so
rather than quietly weakening the claim. Buffer hygiene: entries are
dropped on bundle emission; a stragglers-safety valve (an entry whose
event already bundled, or an unknown slot) is log-and-drop with a
metrics counter. With a fully synchronous in-process bus a partial
bundle cannot stall — all four handlers run before `ingest()` returns —
so no timeout mechanism is built yet; the counter is the tripwire that
would tell us a future async bus needs one.

**Critical fast path (v0.35d):** severity `Critical` on Impulse's slot
routes `Impulse → Governance → Security` immediately, skipping the
bundle/Intent voicing on the way in (the other three workers' answers
for that event are discarded by the buffer). Red on that path revises
through Intent like any other red (Step 4).

### 3.4 Routing table rewrite

`routing.py`'s `Trigger`/`LEGAL_ROUTES`/templates updated to the
v0.35 topology: worker reports bundle (or Critical-dispatch); Intent
advice → Security clear (unchanged); Security verdicts per Step 4;
Action failure fallback (unchanged). The module docstring's lane
diagram and worked example are rewritten in the same commit — the
spec's "no new code sitting next to a stale claim" rule applied to the
file that is most explicitly the one source of truth.

**Consolidator hand-off goes final (v0.35g settled design):** Governance
sends Consolidator its one-bundle-per-event (event_id, Sensory verbatim,
Security outcome incl. full revision arc **with concern text — ASK:
final confirmation, open item 5**, Intent's concluded output) once
Action completes. Since Action is silent on success (v0.33), "Action
completes" is observed by Governance at the moment it releases SPEAK —
it publishes the Consolidator hand-off immediately after the Action
publish on the same dispatch. Step 1's interim direct call from Intent
is removed; Consolidator now hears only from Governance, never
mid-event. Requires Governance to have accumulated the revision arc —
which it has, since every red/re-clear hop passed through it (per-event
buffer again, same lifetime rules).

### 3.5 Intent's broader conversation context (v0.35c) — **ASK first**

Open item 2. The temp log is already the only cross-event state; the
open questions are window size, bounding, and rendering. Proposal to
react to: last **5 turns**, each truncated to 160 chars (matching the
consolidation prompt's own truncation idiom), rendered as a
`RECENT CONVERSATION:` block in both the voicing and the Revise
prompts; manifest-tunable `roles.intent.context_turns`. Not implemented
until Daniel confirms or corrects.

### 3.6 Tests — `tests/test_phase05_fanout.py`

Four copies per ingest, same event_id, no Governance hop on the fan-out
(trace-shape assertions); bundle fires exactly once, on the fourth
report, with all four slots; Intent voices once per event; Critical
skips straight to Security and discards the bundle; Consolidator
hand-off arrives only after Action, containing exactly the settled
contents and none of the excluded ones; e2e byte-identical two-boot
trace updated for the new shape.

---

## Step 4 — Security red → Intent revises; Intent gains the veto (v0.35e)

Last, alone, and with its own test file, per the spec.

### 4.1 Intent's third task: `Revise`

`agents/intent/contract.py`:

- `Task` gains `REVISE`. New `REVISE_RESPONSE_CONTRACT` +
  `build_revise_prompt(...)` — inputs: the original bundle, Security's
  concern text, the blocked proposal, the revision-pass count, and the
  recent-conversation block (3.5).
- Response shape: `{"speech": "<revised proposal>", "proceed": true|false,
  "concern": "<why not, when proceed is false>"}` — the first time
  Intent's contract carries `proceed`, because this is the first time
  Intent decides it.
- **`parse_revise()` and `fallback_revise()` fail CLOSED**: unusable
  substrate answer, unparsable JSON, missing/unreadable `proceed` (via
  `coerce_bool(default=False)`), or a revised proposal that parrots the
  blocked one → `proceed: false` with a concern, mirroring Analytics'
  Revise posture verbatim. This asymmetry is the entire point of the
  step and gets the densest tests.
- Revised output re-enters `Governance → Security` (loop until cleared
  or declined); a `proceed: false` revision exits the loop as a refusal
  voiced by Intent (it already owns refusal voicing).
  Loop bound: reuse the existing loop-detection value (3 passes) as a
  hard cap — a third failed revision converts to fail-closed refusal —
  so a red that can't be fixed can't ping-pong forever. (New constant in
  intent/contract.py, tested.)

### 4.2 Wiring

`routing.py`: `VERDICT_ROUTES[red]` → Intent (`events.intent`,
type `Revise`), carrying the concern + blocked proposal + pass count.
Analytics loses `Review`?  **No** — yellow stays Analytics' (v0.35
severs only the red lane; the spec text says "sever the connection
between analytics and security" in the context of red/revision, while
v0.35's superseded-sections table names only the red path. **ASK**: one
explicit confirmation that yellow → Analytics Review survives, since
"severed entirely" could be read either way.)

### 4.3 The correction pass (stale-claim sweep)

Same commit as the code: `agents/intent/contract.py` module docstring
("no fail-closed asymmetry... Intent never decides proceed" — now
false), `DEFAULT_CORE_ANCHORS["boundaries"]` ("advisory only" — the
persona's own self-description must not lie about its veto),
`DEFAULT_SYSTEM_INSTRUCTION` in `live.py`, manifest
`roles.intent.system_instruction`, `README.md`, `docs/phase-0.4-intent.md`
(marked superseded-in-part), and the Project's
`claude/phase-0.4-intent-as-built.md` via `project_write`.

### 4.4 Tests — `tests/test_phase05_intent_veto.py`

Red routes to Intent, never Analytics; full revise loop green-exit;
fail-closed on every degraded shape (no JSON, bad proceed, parroted
proposal, substrate down, budget mode — budget-mode Revise must also
decline, not advise); loop cap converts to refusal; Critical-origin red
revises identically; yellow lane per the ASK outcome.

---

## Cross-cutting

- **Test-fixture sweep per step**, following the `roles.analytics.mock`/
  `roles.intent.mock` precedent — every fixture that boots the shipped
  manifest pins the new roles' mock flags explicitly
  (`roles.consolidator.mock`, `roles.personality.mock`,
  `roles.knowledge.mock`).
- **Docs stay living**: each step updates the affected `docs/` files in
  the same commit as its code; as-built docs + Project mirror per phase
  discipline, after tests pass.
- **The known failing test** (`test_it_reads_the_shipped_manifest`) is
  left untouched at every step; "no regressions" means the baseline
  288/13/1 plus each step's new file.
- **CI** (`.github/workflows/phase0-test.yml`) needs no change — offline
  suite only, new files are picked up by `tests/`.

## Rough size

| Step | New files | Modified files | New tests (est.) |
|---|---|---|---|
| 1 | 4 (consolidator pkg) | ~9 (intent/*, bootstrap, manifest, tiers, preflight, archive) | ~25 |
| 2 | 4 (archive_lookup pkg) | ~4 (bus, manifest, bootstrap) | ~15 |
| 3 | 1 (test file) | ~8 (sensory, analytics base, governance agent+routing, bus, bootstrap, e2e tests) | ~25 |
| 4 | 1 (test file) | ~8 (intent contract/live/base, routing, manifest, README, docs, project doc) | ~20 |

## The ASK list (blocking items, smallest first)

1. **Yellow lane** (4.2): does Security yellow still go to Analytics
   `Review`, with only red moving to Intent? (Plan assumes yes.)
2. **Concern text in the Consolidator bundle** (3.4): v0.35g's settled
   bundle includes it — one explicit confirmation, per open item 5.
3. **Intent's conversation window** (3.5): 5 turns × 160 chars,
   manifest-tunable — confirm or correct before Step 3.
4. **Revision loop cap** (4.1): 3 passes then fail-closed refusal —
   number and posture to confirm; the spec never bounded the loop.
