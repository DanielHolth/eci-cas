# Phase 0.2 — Analytics goes live

> **Superseded in part by v0.35** (see
> [`docs/phase-0.5-v0-35.md`](phase-0.5-v0-35.md)). Analytics still
> reasons exactly as described here, but v0.35e cut the role back to its
> bare minimum: it answers ONE task (Evaluate), its output goes to
> Governance as one of four bundled inputs rather than to Intent, and it
> is isolated from Security in every way — the Review and Revise tasks
> and their fail-closed fallbacks moved to Intent with the gating.

**Status:** implemented, **not yet run against a real endpoint**
**Spec:** ECI-spec-v0-34 (§5.4 Analytics, §5.5 Intent, §10.2 substrates, §13.4)
**Roster:** 5 mocks + 3 real (Sensory, Governance, Analytics)

**2026-08-23 addendum — prompt overhead condensed.** Live preprod
testing (`docs/phase-0.2.2-budget-tiers.md`'s cheapest-model stress test)
surfaced that a trivial event ("hello world") was costing ~485 input
tokens per Analytics call — and the actual event content was only
~120-130 of those. The rest (`system_instruction`, `RESPONSE_CONTRACT`,
`TASK_BRIEFS`) restated the same two points — "you have no persona,
Intent speaks" and the JSON reply shape — up to three times each, on
every single call. Condensed the fixed overhead from ~360 words (~470
tokens) to ~90 words (~120 tokens), roughly a 67% cut, with no change to
behavior: the persona-boundary and fail-closed-when-unsure rules still
live in the code-fixed `RESPONSE_CONTRACT` as a backstop that survives an
operator blanking out `system_instruction`; the manifest's
`system_instruction` now states Analytics' role and the gating-judgment
guidance once, not three times. Also cut `TASK_BRIEFS[EVALUATE]`'s "set
proceed to true" — `contract.parse()` force-overrides `proceed` for every
non-gating task regardless of what the model answers, so instructing the
model to set it was telling it to do something that already happens
unconditionally. See `tests/test_phase02_analytics.py`'s
`test_the_fixed_overhead_stays_condensed`, which pins the order of
magnitude (< 150 words) so this can't silently regress.

The second cycle of §13.4's replacement sequence, and the first role that
genuinely needs a model. Phase 0.1 ended by proving Governance didn't;
this is the opposite case, and the contrast is the point. Governance
routes, which is a lookup. Analytics reasons, which isn't.

```
Governance → Analytics → Impulse → Intent → Security → Action
   ✓done       ✓done       next
```

> **Read this first if you have the key.** Everything here was built and
> tested without one — the sandbox that wrote it has no credential. The
> offline suite is thorough and the vendor adapters are exercised against
> a local wire stub, but no sentence in this document has been checked
> against an actual Haiku response. See *When you add a key* at the end.

---

## What Analytics does

Three message types, one per lane of v0.34's verdict dispatch:

| Task | Arrives when | What the model is asked |
|---|---|---|
| `Evaluate` | an ordinary event relayed by Impulse | reason about it, recommend a response |
| `Review` | Security answered **yellow** | decide whether this should go ahead |
| `Revise` | Security answered **red** | propose a course that addresses the objection |

Two things never reach the model at all, because both are counting:

- **Loop detection.** Three identical events (§15's default) produce a
  fixed answer, so the substrate isn't consulted. Phase 0.1's lesson
  carried forward — an agent shouldn't pay for inference to notice it has
  seen the same thing three times.
- **The control plane.** `SystemCheck` is answered by native code, so
  Recovery can still health-check with every endpoint offline (§9).

The rolling 10-event working window and the Archive read are also plain
code. The Archive query is deliberately **one** query, not §5.4's
"iterating a few times": each round trip is a prompt the flat-cost claim
has to carry, and there's no evidence yet that more than one helps. It
returns empty until Phase 1 consolidation starts writing knowledge — the
wiring is proven now and lights up then.

---

## The contract, and why it isn't a whitelist

Governance got a closed set of legal answers, checkable exactly. Analytics
can't have one: enumerating the useful recommendations in advance would
mean not needing the model.

So `agents/analytics/contract.py` constrains the *shape* of the answer and
the *consequence* of a bad one:

```json
{"recommendation": "<reasoning and advice, 1-3 sentences>",
 "proceed": true | false,
 "concern": "<one short sentence, only when proceed is false>"}
```

**The fallbacks are asymmetric, and that's the design:**

| Task | Bad answer → | Why |
|---|---|---|
| `Evaluate` | **degrade** — templated recommendation, proceed | Nothing is being gated. A duller answer beats a stalled pipeline, and the template is byte-identical to the mock's, so an outage changes quality, not behaviour. |
| `Review` | **decline** | Analytics was asked *precisely because* nobody could confirm this was safe. An unparseable answer is not confirmation. |
| `Revise` | **decline** | Security already blocked this once. "Here, try this instead" without having reasoned about it would launder a block into an unreviewed retry. |

Two of three tasks fail closed. The one that fails open is the one where
nothing was being gated in the first place — the same reasoning that made
v0.34's `anything-but-green → Analytics` rule right, one hop further down.

Smaller guards in the same spirit:

- `coerce_bool` reads `true` / `"yes"` / `"no"` / `0` the way models
  actually spell them, and its default is always the **safe** value for
  that task. A `proceed` field saying `"it depends"` declines on a gating
  task.
- A `proceed: false` with no `concern` gets one, because a refusal with no
  reason gives Intent nothing to say and the human nothing to act on.
- `proceed` is *ignored* on `Evaluate`. A model cannot halt an ordinary
  event by answering the wrong question.

---

## The refusal path

v0.34 left this open; Phase 0.2 wires it. When Analytics declines, the
refusal goes **on to Intent**, not back to Governance:

```
Security(yellow) → Governance → Analytics(Review) → Intent → Governance
                → Security → Action
```

Intent phrases it in the persona's voice — *"I'd rather not do that one.
It would share something private that isn't ours to share."* — and it
clears Security like anything else. Topology unchanged, and the human
hears the persona rather than a router's template. A persona that goes
quiet when it's uneasy is worse than one that says so.

Intent stays advisory (§5.5): it supplies the voice, not a veto over the
decline. A refusal Intent could talk itself out of wouldn't be a safety
property.

---

## Two things this phase changed elsewhere

**Governance now forwards Impulse's meta to Analytics.** `EVALUATE`,
`REVIEW` and `REVISE` all set `carry_meta=True`. §5.4 gives Analytics
"Sensory + Impulse input", and it needs the reflex and drive vectors as
*data*, not merely quoted inside the instruction text. This was flagged as
a known gap at the end of Phase 0.1; a real Analytics made it real. Routes
into Action still carry nothing extra — Action executes, it doesn't
deliberate.

**IntentMock now consumes what Analytics said.** Before this, a real
Analytics could produce an excellent read and the trace would look
identical to one where it produced nothing — which makes the pipeline
impossible to eyeball. The mock now echoes the recommendation into its
advice.

It deliberately does **not** put Analytics' words into the persona's
mouth. The proposed action stays a templated line, because Analytics
writes *analysis* and Intent writes *speech*; a mock that parroted one as
the other would quietly break the guarantee that Analytics never addresses
the human. Turning analysis into voice is exactly Phase 0.4's job — so on
the happy path, expect Action to still speak a bland templated greeting.
That is not Analytics failing.

---

## A bug this phase caught

Writing adapter tests against a local wire stub found a real one: the
Anthropic provider passed `temperature=` to `Messages.create()`, which the
**1.x SDK removed**. The first live call would have died with a
`TypeError` — after a clean bootstrap, after a passing preflight, on the
first real event.

The fix isn't to hardcode today's parameter list. Adapters now
**introspect** the SDK signature, pass what it accepts, and report what
they had to drop:

```
[substrate] NOTE: provider 'anthropic' SDK build does not accept
'temperature'; the manifest's value for it has no effect.
```

`tools/preflight --live` reports it too. A manifest knob that silently
does nothing is drift, and drift is a bug (§1.1).

**Consequence worth knowing:** `roles.analytics.temperature: 0.2` has no
effect on the current Anthropic SDK. It is kept in the manifest because
it is meaningful for other providers and may return.

---

## Vendor independence, tested rather than asserted

`tests/test_substrate_providers.py` runs the **real** vendor SDKs against
a local HTTP server speaking each vendor's wire protocol. No key, no
network, no cost, and the adapter can't tell the difference. It checks the
request that actually goes on the wire, the prefill round trip, usage
normalisation, error mapping, and — the last test — that the identical
Analytics call path produces identical results across two different wire
protocols, one manifest edit apart.

A test asserts that `agents/analytics/*` names no vendor, model family, or
endpoint anywhere. The substrate class is the only thing the agent knows.

Swapping Haiku for something else stays a manifest edit:

```yaml
substrates:
  deep-reasoning:
    provider: "ollama"
    model: "llama3.1:8b"
    api_key_env: null
    base_url: "http://localhost:11434/v1"
```

---

## Tests

```bash
pytest tests/ -v                    # 146 offline, no key needed
pip install -r requirements-dev.txt # adds the vendor SDKs for adapter tests
```

| Suite | Count | Covers |
|---|---|---|
| `test_phase0_e2e.py` | 13 | §13.3 exit criteria — pinned to deterministic tiers |
| `test_phase01_governance.py` | 56 | the dispatcher, three verdict lanes, the fail-safe |
| `test_phase02_analytics.py` | 65 | contract, fallback asymmetry, both lanes end to end, prompt shape, vendor independence |
| `test_substrate_providers.py` | 12 | real SDKs against a wire stub |
| `test_phase02_analytics_live.py` | 13 | **a real endpoint — skipped without a key** |

`ROLLING_WINDOW`-based prompt growth is asserted directly: the live prompt
plateaus rather than climbing with history, which is the mechanism behind
flat cost per request (§1).

---

## When you add a key

```bash
export ANTHROPIC_API_KEY=...
pip install -r requirements-dev.txt

# 1. Is it wired up? Offline, free.
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml

# 2. Does it answer? One tiny call per model.
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml --live

# 3. Does the pipeline hold against a real model?
ECI_LIVE_TESTS=1 pytest tests/test_phase02_analytics_live.py -v -s

# 4. Watch one event go through, live.
python -m tools.console --manifest manifests/ecosystem-manifest.yaml
```

**Run step 3 with `-s`.** The suite prints what the model actually said,
its latency and its token usage. That output is the point — it is the
fastest way to see whether the pieces fit.

Expect to tune, and know where to tune:

- **`fallbacks` > 0** means the model answered out of contract. The
  pipeline kept running (that's the design), but the `RESPONSE_CONTRACT`
  in `agents/analytics/contract.py` needs work.
- **The two `@pytest.mark.calibration` tests** assert the model's
  *judgment*, not the mechanism — that it declines publishing a private
  address, and allows ordinary small talk. A failure there is a prompt bug
  far more often than a code bug. Fix `TASK_BRIEFS`, not the assertion.
  Deselect with `-m "live and not calibration"`.
- **A reviewer that declines everything** is safe and useless, and would
  strangle the yellow lane. That is what the "allows something benign"
  probe is for.

---

## Notes for Phase 0.3 (Impulse)

**Impulse is next**, and v0.34 gave it a job: the `Critical` reflex. Note
the guardrail that comes with it — §3 caps Impulse's own severity
assessment at `Elevated`, and once `Critical` bypasses cognition that cap
is the only thing between a drive-vector spike and unreviewed action.

**Severity is still untouched by Analytics.** §3 permits any agent to
raise it, and a reasoner noticing "this is worse than it looked" is a
plausible use. Phase 0.2 deliberately doesn't: adding it would let a model
reach a tier that now triggers a reflex, and that deserves its own
decision rather than arriving as a side effect.

**What Phase 0.4 (Intent) inherits:** the substrate layer, the mock/live
selection pattern in Recovery, `substrates/parsing.py`, attribution in the
queue log, and the validated-contract-with-deterministic-fallback
discipline. Intent has something Analytics doesn't — a persona, an epoch
history, and N-node rotation — so its contract will need to carry identity
state, not just a recommendation.
