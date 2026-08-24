# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as an
11-agent ecosystem on a pub-sub message bus. Personality emerges from the
interplay of narrowly specialized roles, not from any single agent, and
every identity change is written as an immutable, source-attributed
epoch you can audit.

Full architecture: [`docs/ECI-spec-revisions-v0-35.md`](docs/ECI-spec-revisions-v0-35.md)
— the living specification and technical source of truth, with
[`docs/phase-0.5-v0-35.md`](docs/phase-0.5-v0-35.md) as the as-built
record of what that revision actually became in code. Read those before
touching anything; this README is just the map.

## Status

**Phase 0.5 — v0.35 built** (§13.4). 4 mocks + 7 real:

| Role | Tier | Since |
|---|---|---|
| Sensory | deterministic | Phase 0 — as of v0.35a it fans out to four agents in parallel, the one ungated hop in the pipeline |
| Impulse | deterministic | Phase 0.3 — real drive-vector drift + weighted appraisal; the only role that can open the Critical fast path |
| Analytics | substrate-backed | Phase 0.2 — the first role that genuinely reasons. v0.35e cut it back to unbiased analytical keywords: isolated from Security in every way, gating nothing |
| **Personality** | **mock** | **v0.35b** — archive-grounded lookup over the identity store, read-only by construction |
| **Knowledge** | **mock** | **v0.35b** — the same class, pointed at the knowledge store |
| Governance | deterministic | Phase 0.1 — the role turned out to need no model at all. v0.35c made it the universal router: it now buffers the four parallel answers and bundles them for Intent |
| Intent | substrate-backed | Phase 0.4 — the role with a persona. **v0.35e gave it a real veto**: both of Security's non-green lanes route here, and it decides `proceed` on them |
| **Consolidator** | **substrate-backed** | **v0.35f** — the former "Consolidating" mode of Intent, now its own role. Sole writer of long-term memory, running off the live path on a background thread |
| Security | mock | rule engine still to come; the mock always clears |
| Action | mock | executes what Governance hands it, and nothing else |
| Archive | deterministic | the only door to memory; a dumb executor by design |

Analytics, Intent and Consolidator each need a credential. Setting
`roles.<role>.mock: true` runs the whole ecosystem at zero cost — every
named budget tier sets all three explicitly rather than leaving a stale
value from a prior tier in place.

**Phase 0.2.1 — budget mode** adds adaptive throttling on top: real
reasoning while the substrate is healthy and spend is under the cap, the
existing deterministic fallbacks when it isn't. Latches on classified
substrate failures or a spend ceiling, manually switchable, persisted
across restarts. Gated tasks decline in budget mode — see
[`docs/phase-0.2.1-budget-mode.md`](docs/phase-0.2.1-budget-mode.md).

**Phase 0.2.2 — budget tiers** names four manifest-level combinations
(`budget_tier: minimal | budget | default | super`) for which vendor
backs each cognitive role — a design-time choice, orthogonal to budget
mode's runtime latch. Minimal and Budget point Analytics/Intent at a
self-hosted local model (`local-fast`, OpenAI-compatible — Ollama, LM
Studio, vLLM); Default matches what the manifest already shipped with;
Super reserves an expensive specialist for consolidation only. See
[`docs/phase-0.2.2-budget-tiers.md`](docs/phase-0.2.2-budget-tiers.md).

**Phase 0.3 — Impulse** makes the reaction real: drive vectors now drift
back toward their manifest-seeded baseline over wall-clock time (per-vector
`drift_tau_sec`), and the reaction itself is a weighted appraisal collapsing
5 drive vectors into 3 legible axes (alertness/warmth/engagement), not a
2–3-branch lookup. Still deterministic — no substrate — since this is the one
hop every event must cross before Governance. The Elevated severity ceiling
(drive-vector state alone can never manufacture a Critical read) is now a
hard invariant, not just tidiness: it's the guardrail the still-deferred
Critical reflex will depend on. See
[`docs/phase-0.3-impulse.md`](docs/phase-0.3-impulse.md).

**Phase 0.4 — Intent** gives the ecosystem a voice. A persona (Core
Anchors + an Evolving Trait Delta) hydrated from Archive on every call —
data, not manifest YAML or a code constant — turns Analytics' verdict into
speech in one of two registers (Advise/Refuse), guarded against parroting
Analytics' own words back (the Analytics-writes-analysis /
Intent-writes-speech boundary is the one guarantee this phase can't
break). Consolidation (§7.4) now reasons for real over the temp log, a
recent queue window, and prior epochs into an immutable epoch record, and
can nudge Impulse's drive-vector *baselines* by a small, hard-clamped
amount — the "slow coloring" coupling named in §5.3 and now actually
wired, not just described. See
[`docs/phase-0.4-intent.md`](docs/phase-0.4-intent.md).

**Phase 0.5 — v0.35** is a deliberate, large break from the topology
Phase 0.4 finished, and the biggest single change in the project so far.
Four things moved:

*The pipeline forked.* Sensory now fans out to **four agents in
parallel** — Impulse, Analytics, and two new archive-grounded lookups,
Personality and Knowledge — with no Governance hop on that one fan-out.
Governance buffers all four answers and sends Intent **one bundle**. Four
short cheap calls racing beat one long call doing all four jobs, and none
of them needs to see another's answer to do its own.

*Intent gained a veto, and Analytics lost one.* Both of Security's
non-green lanes (yellow and red) now route to Intent, which decides
`proceed` on them — reversing the "Intent holds no veto" property Phase
0.4 was built around. The reasoning: by the time Security says anything,
Intent already holds every analytical read of the event plus the broader
conversation none of the single-event agents see. Analytics is severed
from Security entirely and cut back to unbiased analytical keywords.
A red buys **exactly one revision**, and the model is told so; a second
red is an outcome, not another loop — a deterministic blocked notice
carrying an expression drawn from Impulse's live state, a security alert,
and a frustration nudge back into the drive vectors.

*Intent split in two.* The `Awake → Consolidating → ReadyToSwap` fleet
model is gone. **Consolidator** is now its own role: it batches concluded
events, reasons over the batch in one call, and emits N mechanical
Archive writes — running off the live dispatch path on a background
thread so nobody waits on it. Intent's persona is cached in memory and
refreshed only when Consolidator writes a new epoch, removing a
per-event Archive read.

*The Critical reflex is real.* Named-but-deferred since v0.34, now built:
a genuine emergency routes straight to Security, skipping cognition on
the way in — and still through Governance, not around it.

See [`docs/phase-0.5-v0-35.md`](docs/phase-0.5-v0-35.md).

Reading: [`docs/ECI-spec-revisions-v0-35.md`](docs/ECI-spec-revisions-v0-35.md)
(the current topology) and [`docs/phase-0.5-v0-35.md`](docs/phase-0.5-v0-35.md)
(what it became in code, and the four decisions taken while building it),
then [`docs/ECI-spec-revisions-v0-34.md`](docs/ECI-spec-revisions-v0-34.md)
(why Governance is deterministic and what the verdict lanes are),
[`docs/phase-0.1-governance.md`](docs/phase-0.1-governance.md),
[`docs/phase-0.2-analytics.md`](docs/phase-0.2-analytics.md),
[`docs/phase-0.3-impulse.md`](docs/phase-0.3-impulse.md),
[`docs/phase-0.4-intent.md`](docs/phase-0.4-intent.md). The last four
predate v0.35 — each carries a banner naming what it superseded. §14 has
the phased roadmap.

## Structure

```
eci-cas/
  docs/                   ECI-spec-revisions-v0-33.md — the source of truth
                           (v0.30-v0.32 kept for history)
                          phase-0.1-governance.md — this cycle's implementation notes
  manifests/              ecosystem-manifest.yaml — declarative topology (§10)
  bus/                    embedded pub-sub bus + message envelope (§3)
  substrates/             provider-agnostic LLM access (§10.2)
    base.py                 LLMProvider ABC, Substrate, FailureKind, pricing
    providers.py            Anthropic, OpenAI-compatible, Echo
    registry.py             manifest substrate class -> Substrate
    parsing.py              tolerant JSON extraction, shared by agents
  budget/
    state.py                budget mode: latch, spend cap, persistence
    tiers.py                budget tiers: minimal/budget/default/super presets
  tools/
    console.py              watch the queue live
    preflight.py            check substrates before bootstrapping
  agents/
    sensory/                REAL — deterministic; fans out to 4 (§5.2, v0.35a)
    governance/             REAL — deterministic universal router (§5.1, v0.35c)
      routing.py              the routing contract, as data
      buffer.py               per-event bundling state (v0.35a/c/g)
      agent.py                the dispatcher itself
    analytics/              REAL — substrate-backed (§5.4, Phase 0.2)
      contract.py             one task, response schema, fallback
      base.py                 working memory, loop detection, emission
      agent.py                AnalyticsMock  — templated, zero cost
      live.py                 AnalyticsAgent — substrate-backed
    impulse/                REAL — deterministic drift + weighted appraisal (§5.3, Phase 0.3)
    archive_lookup/         MOCK — the archive-grounded family (v0.35b)
      contract.py             the shared keyword-findings format
      base.py                 one class; Personality and Knowledge configure it
      agent.py                ArchiveLookupMock — templated, zero cost
    intent/                 REAL — substrate-backed, with a veto (§5.5, v0.35e)
      contract.py             four registers, fail-closed on the two that gate
      base.py                 persona cache, conversation window, emission
      agent.py                IntentMock  — templated, zero cost
      live.py                 IntentAgent — substrate-backed
    consolidator/           REAL — substrate-backed (v0.35f/g)
      base.py                 batching, epochs, the background worker
      agent.py                ConsolidatorMock  — templated, zero cost
      live.py                 ConsolidatorAgent — substrate-backed
    security/  action/      MOCKS — replaced one per cycle per §13.4
    archive/                JSON-files-on-disk store (§5.8, §13.2)
  recovery/
    bootstrap.py            deterministic bootstrapper (§9, §9.1)
    watchdog.py             passive 5-level escalation monitor (§11)
  tests/
    test_phase0_e2e.py            §13.3 exit-criteria harness
    test_phase01_governance.py    the dispatcher, three verdict lanes, fail-safe
    test_phase02_analytics.py     contract, fallback, the one remaining task
    test_phase05_consolidator.py  batching, multi-writes, worker thread, persona cache
    test_phase05_archive_lookup.py the family shape, read-only posture, bundle slot
    test_phase05_fanout.py        fan-out, bundling, severity, Critical reflex
    test_phase05_intent_veto.py   the routing reversal, fail-closed, one-chance revision
    test_phase03_impulse.py       drift no-op/relaxation, appraisal, severity ceiling
    test_budget_mode.py           every latch path, spend cap, console commands
    test_budget_tiers.py          tier resolution, the no-op cases, boots with no key
    test_substrate_providers.py   real vendor SDKs against a local wire stub
    test_phase02_analytics_live.py a real endpoint — needs a key
```

## Running it

```bash
python -m venv .venv
source .venv/bin/activate        # Windows Git Bash: source .venv/Scripts/activate
pip install -r requirements.txt

# Analytics is substrate-backed as of Phase 0.2, so it needs a key.
export ANTHROPIC_API_KEY=...           # or point substrates: elsewhere
pip install -r requirements-dev.txt    # vendor SDKs

# Is it wired up? Offline and free — the same check Recovery runs at boot.
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml

# Does it answer? One tiny call per model.
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml --live

# Bootstrap the ecosystem from the manifest (§9.1)
python -m recovery.bootstrap --manifest manifests/ecosystem-manifest.yaml

# The full offline suite — no key needed
pytest tests/ -v

# Watch spend while you work
python -m tools.console --manifest manifests/ecosystem-manifest.yaml
#   > budget                     mode, calls, tokens, estimated spend
#   > switch to budget mode      stop calling the substrate
#   > switch to live mode        resume real reasoning

# Against a real endpoint (costs a small number of tokens).
# Use -s: it prints what the model actually said.
ECI_LIVE_TESTS=1 pytest tests/ -v -s -m live
```

No key? Set `roles.analytics.mock: true` and everything above runs at zero
cost.

A successful bootstrap prints each of the seven §9.1 steps and ends with
`system live.`

The test suite proves two things. `test_phase0_e2e.py` proves the full
worked example (§3.2) is reproducible from a cold bootstrap, twice in a
row, with identical queue traces — the literal Phase 0 exit criterion,
now a permanent topology regression suite. `test_phase01_governance.py`
proves Governance dispatches all three verdict lanes correctly, holds no
substrate, and — the property worth having — that the pipeline's one
irreversible step is reachable by exactly one verdict value. Anything
that isn't literally `green` goes to Analytics.

Inspect the resulting Archive (JSON files, deliberately `cat`/`jq`-able
per §13.2):

```bash
cat data/archive/queue/events_*.jsonl | jq .
cat data/archive/working/drive_vectors.json
```

`data/archive/` is gitignored — it's runtime output, regenerated on
every bootstrap, never committed.

## Build order (§13.4)

One mock replaced with a real agent per cycle:

```
Governance → Analytics → Impulse → Intent → Security → Action
    ✓done       next        ↑                    ↑
                   Critical reflex (v0.34)   stays rule-based, no LLM
```

Each cycle: replace exactly one mock, re-run Recovery, re-run the test
suite, confirm the trace still holds before moving to the next.

## Cost posture (Phase 0-1)

- **Phase 0**: zero LLM calls, by construction — all 7 mocks respond
  with templated/hardcoded output (§13.1). Still reachable any time via
  `roles.governance.mock: true`.
- **Phase 0.1**: still zero. Governance holds no substrate — every hop is
  settled by the envelope, and a verdict it can't read goes to Analytics
  rather than to a model. See [v0.34](docs/ECI-spec-revisions-v0-34.md).
- **Phase 0.2**: one call per event — Analytics. Loop detection, the
  working window and the control plane stay native code, so they cost
  nothing. The prompt is bounded by the rolling window rather than growing
  with history, which is the mechanism behind flat cost per request.
- **Phase 0.5 / v0.35**: the fan-out changes the shape of the bill, not
  its size in the way you might fear. Four agents answer every event, but
  two of them (Personality, Knowledge) are still mocked, and when they go
  live they are short, cheap, single-event lookups — the whole reason for
  fanning out rather than asking one model to do four jobs. Consolidator
  adds one call per BATCH, not per event, and runs off the live path.
  Intent's per-event Archive read is gone entirely (v0.35g's persona
  cache), and its conversation window is tier-scaled so the one thing
  that could grow the prompt is capped by the same knob that caps
  everything else.
- **The steady state today**: two calls per event — Analytics and Intent
  — plus one per consolidation batch. Six of the eleven roles are
  deterministic, so the flat-cost claim still rests on a short list of
  things to keep cheap.
- **Budget mode** (§0.2.1) is the floor under all of it: a classified
  substrate failure or a spend ceiling drops the ecosystem to $0/event
  without stopping it. Prices live in the manifest beside the model, so
  the estimate follows a vendor swap automatically.
- **Phase 1**: single substrate class for all cognitive roles is
  mandated by the spec (§14) — no multi-substrate decision to make yet.
  The manifest defaults `fast-reflex` and `deep-reasoning` to the same
  cheap model; real diversity arrives at Phase 3 (§7.5) with a one-line
  manifest change (§10.2).
- Tunable levers for keeping iteration cheap: `rotation.batch_size_events`
  (fewer, larger consolidation batches) and
  `timers.impulse.idle_musing_interval_sec` (less unprompted content) —
  see §15 for the full tunable-defaults table.

## Swapping vendors

Agents ask for a substrate *class*; only the manifest knows the vendor
(§10.2). Changing providers is an edit to one table and nothing else:

```yaml
substrates:
  fast-reflex:
    provider: "ollama"                  # anthropic | openai | groq | vllm | echo | ...
    model: "llama3.1:8b"
    api_key_env: null                   # keyless, so base_url is required
    base_url: "http://localhost:11434/v1"
```

One OpenAI-compatible adapter covers OpenAI, Groq, Together, Mistral,
OpenRouter, vLLM, Ollama, LM Studio and in-house gateways. Adding a
vendor that speaks neither dialect is one `LLMProvider` subclass plus a
`register_provider()` call — no agent code changes.

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
