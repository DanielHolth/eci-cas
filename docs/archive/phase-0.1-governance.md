# Phase 0.1 — Governance made real

> **Superseded in part by v0.35** (see
> [`docs/phase-0.5-v0-35.md`](phase-0.5-v0-35.md)). Governance is still the
> deterministic dispatcher this document describes, and the fail-safe
> verdict property is intact — but the routing table has changed:
> Sensory now fans out to four agents with no Governance hop, Governance
> buffers those four answers and bundles them for Intent, and BOTH
> non-green verdict lanes route to Intent rather than Analytics. The
> "Impulse relay is the sole trigger" row in the route table below is
> historical.

**Status:** implemented
**Spec:** ECI-spec-v0-34 (§5.1 Governance, §5.6 Security, §10.2 substrates, §13.4)
**Roster:** 6 mocks + 2 real (Sensory, Governance)

The first cycle of §13.4's replacement sequence. The brief was "replace
GovernanceMock with a real LLM-backed Governance." That got built,
measured, and then mostly removed — because the measurement said the
model wasn't contributing anything.

Governance ships **real and deterministic**. The reasoning is recorded in
[`ECI-spec-revisions-v0-34.md`](ECI-spec-revisions-v0-34.md); this is how
it works.

```
Governance → Analytics → Impulse → Intent → Security → Action
   ✓done        next
```

---

## What the phase actually found

Governance is the hardest of the eight to make "real," because its
character is the absence of character. It "decides nothing itself"
(§2.1), holds no memory across events (§5.1), and its system instruction
opened with *NEVER generate opinions, personal answers, or explanations*.

The LLM-backed version was built with a validated routing whitelist, a
verbatim-quote rule, and a deterministic fallback — enough guardrails
that the model couldn't damage anything. Which raised the obvious
question: with that many constraints, what is it contributing?

Hop by hop, the answer was nothing. Routes that were already determined,
and wording for instructions whose templates already quoted the human
verbatim. The one genuinely open case — a safety verdict that couldn't be
read mechanically — turned out to have a better answer than a model in
the router seat: **send doubt to the agent that reasons.**

So the guardrails became the implementation, and the model went away.

---

## Shape of the implementation

```
substrates/                  provider-agnostic LLM access (§10.2)
  base.py                      LLMProvider ABC, Substrate, request/response
  providers.py                 Anthropic, OpenAI-compatible, Echo
  registry.py                  manifest substrate class -> Substrate

agents/governance/
  routing.py                   the routing contract, as data
  agent.py                     the dispatcher

bus/envelope.py                + VERDICT_LEVELS (green | yellow | red)
agents/security/agent.py       states its verdict as data, not prose
```

The substrate layer ships fully tested and **entirely unused** — declared
in the manifest, credential-checked at boot, waiting for Phase 0.2 to put
Analytics on it. That is the phase's durable deliverable; the Governance
work is what proved it wasn't needed *here*.

---

## The routing table

`agents/governance/routing.py` states the topology once, as data. It is
total, evaluated without a substrate, and there is no second
implementation to keep in step with it.

| Inbound trigger | Route | Payload |
|---|---|---|
| Impulse relay (v0.31 sole trigger) | → Analytics `Evaluate` | template, quotes prompt + reflex verbatim |
| Intent advice | → Security `Clear` | passes through untouched |
| Security **green** | → Action `Speech` | the persona's cleared words, untouchable |
| Security **yellow** | → Analytics `Review` | template, quotes the verdict and the proposal |
| Security **red** | → Analytics `Revise` | template, quotes the verdict |
| *anything else* | → Analytics `Review` | — |
| Action failure (v0.33) | → Action `Prompt` | template, quotes the failed request |
| unroutable source | log and drop | — |

---

## The three lanes, and the safety property

Security states its verdict as data (`meta.verdict`, enum in
`bus/envelope.py`) rather than prose. Governance dispatches on it without
interpreting anything:

- **green** — cleared by rule. Release.
- **yellow** — the rules do not cover this. Analytics decides.
- **red** — blocked by rule. Analytics revises.

**Everything that is not exactly `green` is yellow.** An unrecognised
value, an absent field, a null, a wrong type, a near-miss spelling: all
route to Analytics. There is a parametrised test for each.

This is the change that matters. Before it, an unreadable verdict left
Governance guessing, and its fallback *released* — fail-open, on the
safety path, in the degraded case. Now the pipeline's one irreversible
step is reachable by exactly one value, spelled correctly, and doubt is
something Security can state rather than something Governance has to
infer.

Yellow and red get distinct message types on purpose. Yellow means the
rules didn't cover it, not that it was blocked; telling Analytics
otherwise would be Governance putting words in Security's mouth.

Governance records `verdict_inferred` when it had to fall back to reading
prose, and counts it in `metrics`. A rising count means Security is
emitting something the enum doesn't cover — visible in the queue log
rather than silently absorbed.

---

## What Governance still guarantees

These were the guardrails on the LLM version. They are now simply how the
code works, which is a stronger form of the same claim.

**It never authors the persona's speech.** The `Speech` payload is
`meta.proposed_action` — Intent's proposal, cleared by Security. §5.1's
"no persona, no opinions" would be a dead letter if the router could
rewrite the line on the way to the door.

**It never paraphrases.** Every template quotes verbatim: the human's
words to Analytics, Security's verdict to a revision request, the failed
request to a fallback prompt. Impulse's relay discipline (§5.3) applied
one hop later, now unconditional.

**It never touches severity.** Outbound envelopes are built with
`Envelope.reply()` and no severity argument, so the upstream tag
propagates untouched (§3's OR-upscale-only rule). No code path can set
it.

**It holds no state across events.** §5.1's per-event statutory context
reset is structural — `decide()` is a pure function of one envelope. The
`metrics` dict is observability only and is never read by a decision.

**The control plane is the same native code.** BootCheck and SystemCheck
are answered identically, which is what lets Recovery bootstrap and
health-check with every model endpoint offline (§9). Previously arranged
carefully; now true by default.

There is a regression guard with teeth: a test asserts the string
`substrates` appears nowhere in `agent.py` or `routing.py`. If someone
reintroduces a model call here, that fails before any behaviour test
does.

---

## Cost

Zero, per event, forever — for this role.

Not "cheap," not "one small call": Governance has no substrate attribute,
no credentials, and no network path. A model being slow, wrong, rate
limited, or entirely absent cannot affect an ordinary event, a block, or
an action failure, because none of them ask.

The steady-state ecosystem is two model calls per event — Analytics and
Intent — and six of eight roles deterministic.

---

## Tests

```bash
pytest tests/ -v          # 69 tests, offline, free, no key required
```

`tests/test_phase0_e2e.py` (13) is the §13.3 exit-criteria suite: the
worked example reproducible from a cold bootstrap twice in a row with
byte-identical traces. As of v0.34 it needs no tier pinning — the shipped
configuration is deterministic end to end, so that claim is now a
property of the system rather than of a test setting.

`tests/test_phase01_governance.py` (56) covers the dispatcher: every
trigger's classification, all three verdict lanes, eight flavours of
malformed verdict, template content, severity propagation, the failure
protocol, queue-log attribution, the control plane, bootstrap behaviour,
and the substrate registry that Phase 0.2 inherits.

The `live` pytest marker and its `ECI_LIVE_TESTS` gate stay in
`conftest.py`, unused. Analytics will need them.

---

## Notes for Phase 0.2 (Analytics)

**Analytics gains a new message type.** `Review` is the yellow lane —
"Security could not clear or block this by rule; decide whether it should
proceed." That is a safety-adjacent judgment, so Analytics' own
degradation path must fail toward *not* acting. The mock currently treats
it like any other event.

**The `Evaluate` hop drops Impulse's `meta`** — `drive_vectors` in
particular — because `EVALUATE.carry_meta` is `False`. The reflex text
survives only because the template interpolates it. Inherited Phase 0
behaviour, not new, but a real Analytics may want the vectors. One-line
change, stays deterministic.

**What transfers from this phase:** the substrate layer, the mock/real
selection pattern in Recovery (which Analytics will actually use),
queue-log attribution via `source_substrate` / `source_model` (§7.4), and
the discipline of a validated output contract with a deterministic
fallback.

**What doesn't:** the routing whitelist. Analytics is a reasoner; there
is no closed set of legal answers, and applying this design there would
be cargo-culting it onto a role it doesn't fit.
