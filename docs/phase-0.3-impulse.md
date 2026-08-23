# Phase 0.3 — Impulse (real), as built

**Status:** implemented, offline-tested (21 dedicated tests + full suite green)
**Scope:** drive-vector drift + weighted appraisal reaction engine only.
**Explicitly deferred to later Phase 0.3 work:** the Critical reflex (bypass path
through Governance → Security → Action) and idle musing. Both were named in the
Phase 0.3 kickoff but intentionally sequenced after vectors, per the plan agreed
before implementation started.

---

## What this is

The Phase 0 mock proved the topology: real drive-vector bookkeeping, a
templated 2–3-branch reaction, always the sole trigger into Governance. Phase
0.3 makes the reaction itself real while staying on the deterministic tier
(§2.1) — no substrate, by design. Two things changed:

1. **Vectors drift.** They used to sit wherever they were last poked, forever.
   Now every vector relaxes back toward its baseline (the manifest's
   `initial_vectors`) over wall-clock time, exponentially, at its own
   per-vector rate (`drift_tau_sec`). Urgency snaps back fast (five minutes by
   default) — it shouldn't linger after whatever triggered it has passed.
   Temperament-like traits (curiosity, temperature) move slowly (one to two
   hours).

2. **The reaction is a weighted appraisal, not a lookup on one raw vector.**
   Five drive vectors (`curiosity`, `fatigue`, `urgency`, `social_drive`,
   `temperature`) collapse into three legible axes — `alertness`, `warmth`,
   `engagement` — via fixed, documented linear combinations. Still a formula,
   not a model, and still fully explainable from the vector state alone. The
   dominant axis and its bucket (low/mid/high) select one of nine reaction
   strings.

What did **not** change from the mock:

- Impulse is still the sole trigger into Governance (v0.31), and still relays
  the original, verbatim event content — never a paraphrase of it. Analytics
  and Intent need what was actually said, not Impulse's gloss on it.
- Impulse still combines its own severity read with whatever Sensory tagged,
  via OR-upscale-only (`severity_max`) — it can raise, never lower, a tag set
  upstream.
- The severity ceiling: Impulse's own assessment is hard-capped at
  `"Elevated"`. Drive-vector state alone, however extreme, can never produce
  `"Critical"` — only an external Sensory signal can. This was tidiness before
  this phase; it is now the actual safety invariant the future Critical reflex
  depends on (Impulse becomes the one path that can bypass cognition for a
  genuine emergency, and that path has to originate from the outside world,
  not from the ecosystem's own mood).

## Why this stays deterministic, not an LLM

This is the one hop every event must cross before Governance — it has to run
inline, synchronously, with zero added latency and zero added cost on the
pipeline's busiest hop, for the same reason Governance and Security stayed
deterministic.

There's a real case for a future fast/cheap LLM-backed variant here, colored
by Intent's persona over time (Phase 0.4's "temperature recalibration" —
slow coloring across Intent's periodic consolidation, not per-event, since the
v0.31 relay is one-way). The decision made going into this phase (**"measure
first"**) was: build the deterministic version properly, observe its actual
behavior against real traffic, and only then decide whether an LLM variant
earns its cost against a *real* baseline instead of a straw one. No LLM
variant exists yet; this doc will be updated when one is spiked behind the
same interface for comparison.

## Drift (§4.1)

```python
DEFAULT_DRIFT_TAU_SEC = {
    "curiosity": 3600.0,
    "fatigue": 1800.0,
    "urgency": 300.0,
    "social_drive": 3600.0,
    "temperature": 7200.0,
}
```

`tau` is the time (seconds) for a displaced vector to fall to ~37% (1/e) of
its distance from baseline. On every incoming event, before appraisal,
`_drift()` walks each vector: if it's sitting exactly on baseline, skip it
(bit-for-bit no-op); otherwise apply `baseline + (value - baseline) *
exp(-elapsed/tau)`, clamped to [0, 1].

**Baseline is per-instance, not a code constant.** Whatever `initial_vectors`
a manifest seeds becomes what "at rest" means for that instance — a manifest
that seeds an anxious or unusually curious persona means that's where drift
relaxes back to, not the code's own `DEFAULT_VECTORS`.

**Why the no-op-at-rest property matters:** the Phase 0 exit criterion
(`test_reproducible_twice_in_a_row`, `tests/test_phase0_e2e.py`) requires
byte-identical envelope traces across two independent cold bootstraps. Every
offline test fixture fires its event within milliseconds of construction,
with vectors sitting exactly at baseline — so `(value - baseline) == 0.0`
regardless of elapsed wall-clock time or `tau`, and the trace stays
reproducible. Drift only ever becomes *observable* once something has
displaced a vector away from baseline: `apply_feedback()`, a test override, or
(later) an idle-musing/recalibration path.

`drift_tau_sec` is manifest-tunable per vector via `roles.impulse.drift_tau_sec`
(see `manifests/ecosystem-manifest.yaml`); anything not overridden keeps the
code default.

## Appraisal (the reaction engine)

```python
alertness  = clamp(urgency - 0.3 * fatigue)
warmth     = clamp(0.6 * social_drive + 0.4 * temperature)
engagement = clamp(curiosity - 0.4 * fatigue)
```

These weights are a first cut, not tuned against real data — revisit once
there's a live LLM-backed variant to compare against (see "measure first"
above).

The dominant axis (highest score) and its bucket — `low` (< 0.35), `mid`
(0.35–0.65), `high` (≥ 0.65) — select one of nine fixed strings from
`REACTION_VOCABULARY`. Three buckets keeps the vocabulary small and every
choice traceable to "which third of the range is this axis in," rather than a
continuous, unexplainable slide. The reflex text is carried in
`meta.reflex` on the outgoing envelope; it is never substituted for the
event's actual content, which is still relayed verbatim.

Severity: if `alertness > urgency_elevated_threshold` (manifest-tunable,
default `0.6`), Impulse's own read is `"Elevated"` — never higher, regardless
of how far past the threshold the score sits. Otherwise `"Neutral"`.

## Feedback (§4.1 reward path)

`apply_feedback(valence, driver)` shifts one named vector immediately —
no Intent pre-approval, this is the immediate-shift half of §4.1. `_drift()`
is the gradual-relaxation half. An unrecognized `driver` name is a silent
no-op (there's no vector to shift).

## Manifest surface

```yaml
roles:
  impulse:   { tier: deterministic, mock: false,
               initial_vectors: { curiosity: 0.8, fatigue: 0.1, urgency: 0.0, social_drive: 0.5, temperature: 0.4 },
               severity:        { urgency_elevated_threshold: 0.6, ceiling: "Elevated" },
               # drift_tau_sec: { curiosity: 3600, fatigue: 1800, urgency: 300, social_drive: 3600, temperature: 7200 }
             }
```

- `mock` — Impulse is deterministic and always real as of Phase 0.3, the same
  posture as Sensory and Governance. `mock: true` in the manifest is
  warned-and-ignored (`recovery/bootstrap.py::_provision_impulse`), not
  branched on — there's nothing left for it to select between.
- `severity.ceiling` is **read but never obeyed** if it disagrees with the
  hard-coded `IMPULSE_SEVERITY_CEILING = "Elevated"`. This is deliberate: it's
  a v0.31/§3 safety invariant, not a tuning knob a manifest can loosen. A
  manifest attempting to raise it gets a loud stderr warning at bootstrap and
  is overridden anyway — the same discipline Governance's verdict dispatch
  applies to an unreadable Security verdict (fail-safe, not fail-open).
- `drift_tau_sec` — optional, per-vector override of the relaxation rate;
  anything omitted keeps the code default.

## Test coverage

`tests/test_phase03_impulse.py` (21 tests, all offline, no API keys needed):

- **Drift no-op at baseline** (2 tests) — vectors untouched by an event when
  nothing has displaced them, including across real elapsed wall-clock time.
- **Drift when displaced** (4 tests) — relaxation toward baseline over time,
  no overshoot past baseline, Archive write only on actual change, manifest
  `drift_tau_sec` override applied correctly.
- **Appraisal engine** (4 tests) — axis clamping, engagement-dominant and
  alertness-dominant reflex selection, reflex text present in outgoing meta.
- **Severity ceiling** (4 tests) — high alertness raises to Elevated and no
  higher; an incoming Critical tag is never downscaled; low alertness doesn't
  raise severity; the elevation threshold is manifest-tunable.
- **Verbatim relay** (2 tests) — content is relayed unchanged (not the reflex
  text); destination is always Governance.
- **Feedback path** (3 tests) — immediate vector shift, clamping, silent no-op
  on an unknown driver name.
- **Bootstrap provisioning** (2 tests) — `mock: true` is warned-and-ignored;
  a manifest attempting to raise the severity ceiling is warned and not
  obeyed.

Plus the existing suite, unmodified and still green (246 passed / 13 skipped
after this phase, up from 225/13 before — this file's 21 tests are the whole
delta):

- `test_impulse_upscales_neutral_to_elevated_on_high_urgency`
- `test_impulse_cannot_reach_critical_from_vectors_alone`
- `test_impulse_vectors_present_in_working_tier`
- `test_reproducible_twice_in_a_row` (the exit-criteria constraint that shaped
  the drift design in the first place)

## Deferred (named in scope, not yet built)

- **Critical reflex** — the bypass path that lets Impulse route straight
  through Governance → Security → Action without Analytics/Intent, for a
  genuine emergency signaled externally via Sensory. Deferred deliberately:
  it's safety-critical and wants its own focused implementation + test pass,
  not folded into the vectors change. The `IMPULSE_SEVERITY_CEILING`
  invariant hardened in this phase is exactly the guardrail that reflex will
  depend on.
- **Idle musing** — Impulse originating an event on its own (`triggered_by:
  "self"`) during a quiet period (`timers.impulse.idle_musing_interval_sec`,
  manifest default 7200s), rather than only reacting to Sensory. Two open
  scope questions from the prior session, still unanswered: should musing
  reach Action during the current preprod cheap-model stress test, and what
  should "pulls from Archive" read given the `knowledge` store is still
  empty (nothing writes to it yet — see `docs/phase-0.2.2-budget-tiers.md`'s
  live-queue notes).
