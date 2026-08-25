# Phase 0.4 — Intent (real), as built

> **Superseded in part by v0.35** (see
> [`docs/phase-0.5-v0-35.md`](phase-0.5-v0-35.md)). Read this for how the
> persona and the ADVISE/REFUSE registers work — that part is intact.
> Three claims below are now FALSE, deliberately:
>
> 1. **"Intent holds no veto."** v0.35e reversed it. Both of Security's
>    non-green lanes route to Intent, which decides `proceed` on them,
>    and its gating registers fail CLOSED.
> 2. **Consolidation belongs to Intent.** It is its own role now
>    (`agents/consolidator/`, v0.35f) with its own substrate, its own
>    batch buffer and a background worker thread.
> 3. **The persona is hydrated per call.** It is cached in memory and
>    refreshed only when Consolidator writes an epoch (v0.35g).
>
> The node/rotation model (`Awake → Consolidating → ReadyToSwap`) is also
> gone, along with `roles.intent.nodes`.

**Status:** implemented, offline-tested (36 dedicated tests + full suite green)
**Spec:** ECI-spec-v0-34 (§5.5 Intent, §6 Memory Model, §7 Intent Lifecycle/
Rotation/Consolidation, §10.2 substrates, §13.4)
**Roster:** 6 real (Sensory, Impulse, Governance, Analytics, Intent) + 2 mocks
(Security, Action)

```
Governance → Analytics → Impulse → Intent → Security → Action
   ✓done       ✓done      ✓done      next
```

The third cycle of §13.4's replacement sequence, and the first role with a
persona. Analytics reasons about a situation with no voice of its own —
Phase 0.4 is the opposite half: a role that owns no gating judgment at all,
but has to sound like somebody every single time it speaks.

---

## What Intent does

One job — turn Analytics' verdict into something spoken — in two registers
(`agents/intent/contract.py`'s `Task` enum), not three tasks the way
Analytics has:

| Task | Arrives when | What the model is asked |
|---|---|---|
| `Advise` | Analytics said `proceed: true` | react to the recommendation, in persona, addressed to the human |
| `Refuse` | Analytics said `proceed: false` | voice the decline, in persona, without softening or dropping the `concern` |

Intent holds no veto (§5.5). `proceed` was already decided by Analytics
before Intent is ever invoked, and nothing Intent says changes whether
Security ultimately clears the action. That asymmetry — real for
Analytics' Review/Revise, absent here — is why both registers degrade the
same way on failure: to a deterministic, templated line. The only thing at
stake in a bad answer is *how* something is said, never *whether* it
happens.

REFUSE still gets one extra safeguard ADVISE doesn't. Governance's CLEAR
route forwards whatever Intent writes straight to Security with no semantic
check, and Security may still be a mock that clears everything while
Intent is going live — a refusal that reads as assent would sail straight
through with nothing downstream positioned to catch it. So
`parse_refuse()` never lets the model write the whole sentence: it asks
for a short in-voice **lead-in** only (capped at 120 chars, rejected
outright if it opens with an assent word — "sure", "yes", "of course", and
so on), and the `concern` — the actual reason, the only load-bearing
content — is always appended verbatim in code, never phrased by the model.
The model colors delivery; it cannot touch substance.

## The Analytics/Intent boundary (unchanged, now load-bearing)

The one guarantee Phase 0.4 is not allowed to break: **Analytics writes
ANALYSIS, Intent writes SPEECH.** IntentMock enforced this by construction
(it had no model to blur the line with). A live node has a new failure
mode the mock never could: parroting Analytics' recommendation back with a
different label on it. `contract.is_parroting()` catches this —
exact match after normalization, or the recommendation wrapped in a
sentence with fewer than five words of real content around it — and a
parroting response is a `ContractViolation`, degrading to the deterministic
fallback exactly like any other unusable answer.

## Persona (§5.5, §6, §7.1) — lives in Archive, not code or the manifest

Per the working decision made before this phase started: the persona is
**data in Archive**, inspectable and editable there
(`data/archive/identity/intent_epochs.json`), not a manifest string and not
a Python-only constant read at runtime. A `DEFAULT_CORE_ANCHORS` constant
still exists in `agents/intent/contract.py` — it's the seed value, written
once at first bootstrap (`ensure_anchors_seeded()`, idempotent, safe to
call from every node/tier) as a record with `"kind": "anchors"` sharing the
same `identity` archive file as ordinary consolidation epochs, distinguished
by that marker rather than living in a second file.

Phase 0.4's starter persona, drafted per Daniel's steer ("draft an active
listener starter persona"): **stance** (reflect back before offering a
take, ask rather than assume, the human's account of their own experience
is authoritative), **values** (warmth without flattery, genuine curiosity,
honesty over comfort, restraint before rushing to fix or advise), and
**boundaries** (advisory only — Governance and Security hold the real
veto; when Analytics has declined, Intent's job is to say so in its own
voice, not relitigate it). This is a draft to react to and edit directly
in Archive, not a final answer.

Every live call hydrates a `PersonaState`: Core Anchors (fixed, ~1k
tokens) plus an Evolving Trait Delta — a bounded, recency-weighted digest
of the most recent consolidation epochs' rationale strings (§7.1's
"hydrates by recency-weighted summarization... Core Anchors are never
summarized away"). The digest is naive concatenation, capped at 800 chars
before rendering, not a second LLM summarization pass — unjustified
against zero evidence the naive version is too noisy yet. `render()` caps
the whole persona block at 1400 chars; it rides on every live call, so
it's charged against the same flat-cost claim as everything else in the
prompt.

## The Intent lifecycle, as it stands at N=1 (§7)

Phase 0-0.4 runs exactly one registered node (`node-a`), always `Awake`
(§7.3: "N=1 degrades rotation to a pause"). `IntentBase` carries node
bookkeeping, a batch counter, and the consolidation trigger
(`rotation.batch_size_events`, manifest-tunable, default 25) — all real
scaffolding for a fleet that doesn't exist yet, exercised today by exactly
one node.

The temp log is in-memory, not Archive, per the spec's explicit
permission: "the temp log stays ephemeral in memory during POC phases...
a mid-consolidation crash is recoverable state loss, not data corruption"
(§7.2). Every voiced event appends `{recommendation, proposed_action,
proceed, concern}`; consolidation drains it.

## Consolidation (§7.4) — real, not templated

`reconcile()` reasons over three inputs into one `ConsolidationResult`:

1. **The temp log** — this node's own reactions since the last cycle.
2. **A recent queue window** (bounded to the last 100 records) — the
   Phase 0.4 stand-in for "Analytics' delta report," which is a §5.4
   capability that doesn't exist yet. Reading the raw Archive queue log
   directly is a documented scope simplification, not the final shape.
3. **Prior epochs** — the last 3 consolidation records, same window as
   hydration's Evolving Trait Delta.

The model is asked (via `CONSOLIDATION_RESPONSE_CONTRACT`) for `deltas`
(trait + one-sentence rationale, may be empty — "most cycles warrant zero
or one small delta, not a rewrite"), an optional `recalibration` (small
named nudges to Impulse's baseline vectors), and a one-or-two-sentence
`evolving_delta` summary. The result becomes an immutable, append-only
epoch record written to Archive (§7.4's format), carrying
`source_substrate`/`source_model` attribution the same way Analytics'
epochs do.

Consolidation runs on its own substrate class
(`roles.intent.consolidation_substrate`, named by `budget/tiers.py`'s
presets — falls back to the live-duty substrate if unset), resolved
independently of the per-node live substrate. A consolidation substrate
that fails its credential check is a bootstrap **warning**, not a stop —
consolidation just degrades to `reconcile()`'s own deterministic fallback
(empty deltas, no recalibration), the same "outage changes quality, not
behavior" posture as every other degraded path in this codebase. It would
be wrong to block the whole live voicing pipeline over a substrate only a
rare, non-blocking background pass depends on.

## The "slow coloring" coupling (§5.3) — wired for real

Daniel's explicit call ("wire it for real"): §5.3's line — "only Intent
adjusts the temperature, during consolidation — a values judgment, not a
security one" — is implemented, not deferred.

`ConsolidationResult.recalibration` is a dict of `{vector: delta}`,
clamped hard to `[-0.2, 0.2]` in `_parse_consolidation()` regardless of
what the model asked for (the same discipline as Impulse's own
`IMPULSE_SEVERITY_CEILING` — the ceiling has to be enforced where the
number is parsed, not merely requested in the prompt, or "slow" stops
being true). `IntentBase._apply_recalibration()` is the one place the
coupling to Impulse lives: it calls the new
`Impulse.recalibrate_baseline(vector, delta, rationale)` for each entry.

The critical distinction, and the reason it's called *slow* coloring:
`recalibrate_baseline()` shifts Impulse's **`_baseline`** dict — the target
`_drift()` relaxes toward — never the live `vectors` value directly. A
consolidation cycle nudges where "at rest" means for that vector; it takes
many cycles of drift to actually move the live value there. This is
distinct from `apply_feedback()`'s reward path, which shifts a live vector
immediately. Baseline recalibration is currently in-memory only, matching
Impulse's existing not-yet-persisted-baseline posture for Phase 0 — nothing
calls `archive.set_drive_vectors()` over a baseline-only change.

A node with no live Impulse reference (e.g. a unit test constructing
`IntentAgent` directly) degrades this coupling to a silent no-op — it's
deliberately not a required wiring, just an available one.

## Manifest surface

```yaml
roles:
  intent:
    tier: cognitive
    mock: false
    temperature: 0.7
    nodes:
      - { id: "node-a", substrate: "fast-reflex" }
    consolidation_substrate: "fast-reflex"
    rotation:
      batch_size_events: 25
    system_instruction: |
      You are INTENT: the persona of a multi-agent system, speaking
      directly to the human. Advisory only — Governance and Security
      hold the real veto over whether anything you say actually happens.
```

- `mock` — `true` selects `IntentMock` (templated voicing, zero LLM cost,
  same lines it has always produced); `false` selects `IntentAgent`. Every
  named budget tier (`budget/tiers.py`) now sets this explicitly to
  `false` — an operator's stale `intent.mock: true` must not silently
  survive a tier switch, the same discipline already applied to
  `roles.analytics.mock` in Phase 0.2.
- `nodes` — N-generic from day one; Phase 0-0.4 only ever provisions
  `nodes[0]`. A manifest declaring more than one gets a `NOTE`, not an
  error — real rotation across a fleet arrives in Phase 2+ (§7.3).
- `consolidation_substrate` — resolved independently of the node
  substrate; falls back to it if unset.
- `system_instruction` — short and structural on purpose, same discipline
  as Analytics' condensed instruction (`docs/phase-0.2-analytics.md`): the
  persona-boundary rule lives in `contract.py`'s code-fixed response
  contracts as a backstop that survives an operator blanking this field;
  the actual character (Core Anchors) lives in Archive as data, not here.

## Budget tiers — every tier now runs Intent live

`budget/tiers.py`'s `apply_tier()` sets `roles.intent.mock = False` for
every named tier (Minimal, Budget, Default, Super) — Intent has no
zero-cost row in the appendix the way Analytics does under Minimal. This
surfaced a latent gap while wiring Phase 0.4: Minimal and Budget's
"$0, zero-credential" promise was only true by accident while Intent was
mocked and nothing ever called `local-fast`. Making Intent live for real
exercised that substrate class for the first time and exposed that it had
been left pointed at a hosted OpenAI stand-in from an earlier preprod
stress test — reverted back to a genuine keyless local Ollama config
(`manifests/ecosystem-manifest.yaml`'s `local-fast` entry) so Minimal and
Budget's promise is actually true again, not just untested.

## Test coverage

`tests/test_phase04_intent.py` (36 tests, all offline, no API keys
needed, via a `ScriptedIntentProvider` test double registered into the
real substrate registry):

- **Contract** — `parse_advise`/`parse_refuse` accept well-formed
  answers and reject malformed ones; `is_parroting` catches exact and
  wrapped duplication case- and whitespace-insensitively, and does not
  trip on a genuine reaction; assent-opener rejection on refusal
  lead-ins; the refusal lead-in is appended to the concern verbatim.
- **Voicing** (end-to-end through a booted `Recovery` ecosystem) —
  parroting is rejected and degrades to fallback; a refusal carries its
  concern through; an assent-shaped refusal degrades rather than reading
  as clearance; a substrate outage degrades cleanly; strict mode raises
  instead of degrading; `meta.intent` attribution
  (`source_substrate`/`source_model`/`provider`/`latency_ms`/`usage`/
  `est_cost_usd`) is written the same way Analytics writes
  `meta.analytics`; a full pipeline run reaches Action.
- **Persona** — Core Anchors seed once and are idempotent across
  repeated bootstraps; hydration reads the seeded anchors and a bounded
  evolving-delta digest.
- **Consolidation** — an empty batch still writes a templated epoch;
  `test_recalibration_reaches_the_live_impulse_baseline` asserts
  `impulse._baseline["temperature"]` shifts by exactly the requested
  delta while `impulse.vectors["temperature"]` stays untouched;
  `test_recalibration_is_clamped_regardless_of_what_the_model_asked_for`
  asserts the `[-0.2, 0.2]` clamp holds even when the scripted model
  requests `delta=5.0`.
- **Bootstrap** — the shipped manifest resolves Intent as real;
  `mock: true` selects the templated tier; a consolidation substrate that
  can't validate its credentials is a warning, not a bootstrap stop, and
  the live pipeline still comes up.
- **Vendor independence** — swapping Intent's substrate is a manifest
  edit, mirroring Analytics' equivalent regression test.

Every pre-existing fixture that boots the shipped manifest without
pinning `roles.intent.mock` needed that pin added (`test_phase0_e2e.py`,
`test_phase01_governance.py`, `test_budget_mode.py`,
`test_phase02_analytics.py`) — the exact precedent Phase 0.2 set when
Analytics went live, applied here for the same reason: the shipped
manifest's Intent substrate now needs real credentials the sandbox
doesn't have.

Full suite after this phase: **288 passed / 13 skipped / 1 known
pre-existing failure** (`test_budget_mode.py`'s shipped-manifest spend-cap
assertion, unrelated to Intent — up from 252/13/1 before this phase; this
file's 36 tests are the whole delta).

## `tools/preflight.py` — extended for consolidation

`_iter_required_substrates()` previously only reported on Intent's
per-node substrates. It now also reports
`roles.intent.consolidation_substrate` as its own row
(`intent[consolidation]`) — a manifest can leave a node's substrate
healthy while consolidation points at something unusable, and that
wouldn't have shown up in the offline check otherwise. Same
required-ness rule as the nodes: only load-bearing while Intent is live.

## Deferred (named in scope, not yet built)

- **Real rotation across a fleet** — N>1, `Consolidating`/`ReadyToSwap`
  transitions actually exercised. Phase 0.4 keeps the state machine's
  scaffolding real but genuinely exercises only the N=1 "pause" case
  (§7.3). Phase 2+.
- **Analytics' delta report** — §5.4 describes a real digested-delta
  capability Analytics doesn't have yet; consolidation currently reads
  the raw Archive queue log directly as a Phase 0.4 stand-in.
- **Security and Action going live** — the last two mocks in §13.4's
  sequence.
