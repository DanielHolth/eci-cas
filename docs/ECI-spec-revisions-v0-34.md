# ECI-spec-v0-34.md

**Version:** 0.34
**Status:** Stable (Phase 0.1 complete)
**Supersedes:** v0.33
**Last Updated:** 2026-08-22

---

## Executive summary

v0.34 records what Phase 0.1 found by building the thing v0.33 asked for.
The instruction was "replace GovernanceMock with a real LLM-backed
Governance." That got built, measured, and then largely removed — because
the measurement said the model in that seat was contributing routing
decisions that were already determined, plus wording nobody downstream
could use.

Three changes follow from it:

1. **Governance moves from the Cognitive tier to the Deterministic tier.**
   It holds no substrate at all.
2. **Security's verdict becomes a closed enum** — `green | yellow | red`,
   carried as data. The yellow lane is what lets Governance stay
   deterministic without ever guessing about safety.
3. **The cognitive tier is exactly Analytics and Intent.** Two reasoners:
   one that thinks, one that has values. Everything else is plumbing.

Plus one deferred decision now settled: **`Critical` severity is handled
as a reflex in Impulse**, not as a Governance fast-path.

---

## The three revisions

### v0.34a: Governance is a dispatcher, not a cognitive agent

**Problem.** v0.33 §"The Eight Agents" lists Governance as "Mock Phase 0,
Real Phase 0.1+" on the cognitive tier, at temperature 0.0, with a system
instruction. Phase 0.1 implemented exactly that, then asked what the model
was actually doing on each hop. The answer:

| Hop | What the model contributed |
|---|---|
| Impulse relay → Analytics | wording of an instruction whose template already quoted the human verbatim |
| Intent advice → Security | nothing; the payload passes through |
| Security verdict → Action / Analytics | a route already determined by reading a verdict |
| Action failure → Action | wording of an explanation the template already carried |

Only one case was genuinely open: a safety verdict that could not be read
mechanically. And on reflection that case has a better answer than a model
in the router seat — **doubt routes to the agent that reasons.**

**Solution.** Governance is deterministic. One implementation, no
mock/real split, no substrate, no system instruction. It joins Sensory as
always-real; §13.1's reasoning ("there is nothing meaningful to mock")
applies just as well to a lookup table.

**Result.** An ordinary event, a block, and an action failure all route
for free and cannot be affected by a model being slow, wrong, or absent.
`roles.governance` in the manifest loses `substrate`, `temperature`,
`max_tokens`, and `system_instruction`.

**What this is not.** Governance is not demoted or hollowed out. §2.1
already said it "decides nothing itself"; v0.34 simply stops paying for a
faculty the role was never supposed to exercise. Tier is an implementation
detail (§2.1), not a change to any role's responsibility — the routing
table, the topology, and every guarantee Governance made are unchanged.

---

### v0.34b: Security's verdict is a closed enum

**Problem.** v0.33's Security answers `Green` or `Red` as prose, and §5.6
describes a *graded* response — ~90% silent, ~9% advisory warning, ~1%
hard No. The middle tier had no representation on the wire. Governance
had to parse English and guess, and its deterministic fallback treated
anything it couldn't read as non-blocking. That is **fail-open on the
safety path**, in exactly the degraded case where it matters most.

**Solution.** The verdict is data, carried as `meta.verdict`, drawn from
a closed enum:

```
green    cleared by rule             -> Action        release
yellow   the rules do not cover it   -> Analytics     you decide  (Review)
red      blocked by rule             -> Analytics     revise      (Revise)
```

**Anything that is not exactly `green` routes to Analytics.** An
unrecognised value, an absent field, a malformed one, a near-miss spelling
— all yellow. The pipeline's one irreversible step is reachable by exactly
one value, spelled correctly.

`yellow` and `red` get **distinct message types**. Yellow means the rules
didn't cover it, not that it was blocked; telling Analytics otherwise
would be Governance putting words in Security's mouth.

Governance records `verdict_inferred` when it had to fall back to reading
prose, so a Security emitting something the enum doesn't cover shows up in
the queue log rather than being silently absorbed.

---

### v0.34c: Security keeps no LLM; the ethical line lives in Intent

**Problem.** The yellow lane invites an obvious next step: give Security a
model so it can catch novel harms the rule file misses.

**Solution.** No. §5.6's value is that every decision is justifiable from
`security_rules.json` and that single event alone. A model in that seat
trades the audit trail for judgment the ecosystem already has, one hop
away, in a role built for it.

The division of labour, stated plainly:

- **Security** — *is this against the rules?* Deterministic, auditable,
  the hard stop. Where the rules don't reach, it says `yellow` and passes
  the question on. It does not need to be clever; it needs to be
  trustworthy.
- **Intent** — *is this against who we are?* Advisory, values-bearing, and
  deliberately given room to push at the line (§5.5's "sticks to its
  acquired integrity over the socially easier answer"). The persona is
  *meant* to have urges and act some of them out; a slightly blurry
  ethical boundary is the design, not a defect.

That blur is exactly why Security must stay mechanical. A persona with
latitude needs a backstop that cannot be talked round — and one that can
be audited after the fact when it does stop something.

**If a model is ever added to Security**, it may only *escalate*:
green→yellow→red, never toward clearance. Same guardrail shape as
Impulse's severity ceiling (§5.3) — internal judgment amplifies concern
but can never manufacture it away. Not implemented; recorded so the
constraint exists before the temptation does.

---

## Settled: `Critical` is an Impulse reflex

v0.31 deferred a Governance fast-path on `Critical` — skip Analytics and
Intent, go straight to Security. v0.34 drops that and puts the fast path
in Impulse instead.

**Why the spec'd version could not work as written.** Security clears a
`proposed_action`. On a path that skips Analytics and Intent, nobody has
produced one. The fast-path had no payload to carry.

**Why Impulse is the right home.** A reflex arc doesn't route through
cortex — that is the actual biology the architecture is modelled on, and
Impulse is already the reflexive gut, already deterministic, already the
sole trigger into Governance (§5.3, v0.31). A `Critical` reflex from
Impulse is instant and free. Routing `Critical` to Analytics instead would
add an inference call and a round trip to the events where latency matters
most.

**The guardrail is now load-bearing.** §3 already caps Impulse's own
severity assessment at `Elevated` — only an external signal via Sensory
can set `Critical`. Until now that cap was tidiness. Once `Critical`
*bypasses cognition entirely*, it becomes the thing standing between the
system and a drive-vector spike triggering unreviewed action. It must not
be relaxed, and `Critical` must stay expensive to reach: an event that is
merely urgent is `Elevated`.

Deferred to Phase 2 (Impulse is third in §13.4's sequence). Recorded here
so the reflex is designed before it is needed, and so nobody re-adds the
Governance fast-path.

---

## What the ecosystem looks like now

| Role | Tier | Model calls per event |
|---|---|---|
| Sensory | Deterministic | 0 |
| Impulse | Deterministic | 0 |
| **Governance** | **Deterministic** (was Cognitive) | **0** |
| Analytics | Cognitive | 1 |
| Intent | Cognitive | 1 |
| Security | Deterministic | 0 |
| Action | Deterministic | 0 |
| Archive | Deterministic | 0 |

Two cognitive roles, six deterministic. Every model call in the ecosystem
buys either reasoning or values, and the flat-cost claim (§1) rests on a
much shorter list of things to keep cheap.

The substrate layer (§10.2) is unchanged and unused: declared in the
manifest, credential-checked at boot, provider-agnostic, and waiting for
Phase 0.2 to put Analytics on it.

---

## Sections superseded

| Section | Change |
|---|---|
| §2.1 role table | Governance: Cognitive → Deterministic; "Persona? None" unchanged |
| §5.1 Governance | No substrate, no system instruction; three-lane verdict dispatch; the deferred `Critical` fast-path is removed, not deferred |
| §5.3 Impulse | Gains the `Critical` reflex (Phase 2); the `Elevated` ceiling is reclassified from guardrail to safety-critical invariant |
| §5.6 Security | Verdict is a closed enum in `meta.verdict`; the graded middle tier gets the `yellow` value; explicitly no LLM |
| §3 envelope | `meta.verdict` added to the protocol; `VERDICT_LEVELS` alongside `SEVERITY_LEVELS` |
| §10.1 manifest | `roles.governance` loses `substrate`, `temperature`, `max_tokens`, `system_instruction`; `mock` is ignored |
| §13.1 / §13.4 | Governance joins Sensory as always-real; Phase 0.1 roster is 6 mocks + 2 real |

## What didn't change

- Bus architecture, topics, and the 8-agent roster
- Pipeline topology: Sensory → Impulse → Governance → Analytics → Intent → Security → Governance → Action
- v0.31's strict relay and severity scale
- v0.33's failure protocol: one Prompt, no retries, no escalation
- Archive interface, Recovery bootstrap, Watchdog levels
- The substrate layer and the substrate-class indirection (§10.2)

---

## Next: Phase 0.2 — Analytics

Analytics is next in §13.4's sequence, and it inherits more from Phase 0.1
than Governance kept:

- the provider-agnostic substrate layer, tested and credential-checked;
- the mock/real selection pattern in Recovery, which Analytics *will*
  actually need;
- attribution in the queue log (`source_substrate` / `source_model`,
  §7.4), so its calls are traceable;
- the discipline of a validated output contract with a deterministic
  fallback.

What does **not** transfer is the routing whitelist. Analytics is a
reasoner; there is no equivalent closed set of legal answers, and
pretending otherwise would be applying this design to a role it doesn't
fit. Two things do apply: Analytics now receives a `Review` message type
it must handle (the yellow lane — decide whether something proceeds), and
that is a genuine safety-adjacent judgment, so its own degradation path
needs to fail toward *not* acting.

---

*End of revision note v0.34.*
