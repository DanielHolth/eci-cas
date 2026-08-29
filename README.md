# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as a
12-agent ecosystem on a pub-sub message bus. Personality emerges from the
interplay of narrowly specialized roles, not from any single agent, and
every identity change is written as an immutable, source-attributed
epoch you can audit.

Full architecture: [`docs/current-spec.md`](docs/current-spec.md) — the
living specification and technical source of truth. Read it before
touching anything; this README is just the map.

## Status

All 12 roles run live by default (`roles.<role>.mock: true` drops any of
them to zero-cost templated output):

| Role | Tier | What it does |
|---|---|---|
| Sensory | deterministic | Entry point; fans every event out to Impulse, Analytics, and Personality in parallel |
| Impulse | deterministic | Drive-vector drift + weighted appraisal; the only role that can open the Critical fast path |
| Analytics | substrate-backed | Unbiased analytical read of the event, in keywords — isolated from Security, gates nothing; also proposes which knowledge paths are worth a look |
| Personality | substrate-backed | Archive-grounded identity lookup, read-only by construction |
| Knowledge | deterministic | Parquet-backed structured retrieval: a swarm of parallel predicate-pushdown lookups over the paths Analytics proposed, run inline by Governance — no LLM call |
| Governance | deterministic | Universal router; buffers the three parallel fan-out answers plus the Knowledge swarm result into one bundle for Intent, and forks it to Consolidator |
| Intent | substrate-backed | The persona's voice. Holds the veto: yellow gets one revision attempt then proceeds regardless; red blocks immediately, no revision |
| Consolidator | substrate-backed | Sole writer of long-term memory; writes per event, immediately, off Governance's bundle fork — no buffer, no batch |
| Reflection | substrate-backed | Meta-cognition off Governance's conclude fork; every `batch_size` concluded events, looks for a durable pattern and writes an internal insight, raises an Idea, or stays silent |
| Security | deterministic | Rule engine (`config/security_rules.json`, 9 rules: 6 yellow, 3 red) |
| Action | deterministic | Executes what Governance hands it after clearance, fans out to configured sinks, silent on success |
| Archive | deterministic | The only door to memory; a dumb executor by design |

Analytics, Personality, Knowledge, Intent, Consolidator, and Reflection
each need a credential. Named budget tiers (`budget_tier: minimal |
budget | default | super`) pick which vendor backs each cognitive role.
Budget mode adds a runtime latch on top: a classified substrate failure
or a spend ceiling drops the ecosystem to deterministic fallbacks at
$0/event without stopping it. See
[`docs/current-spec.md`](docs/current-spec.md#7-budgets-and-cost-control)
for both.

Two LLM calls per event in the steady state (Analytics, Intent), plus a
Personality lookup, plus one Consolidator call per event and one
Reflection call per `batch_size` concluded events (off the live dispatch
path). Knowledge's swarm is deterministic — N parallel Parquet lookups
tier-scaled by `budget_tier`, no substrate call. Seven of the twelve
roles are deterministic and cost nothing.

## Structure

```
eci-cas/
  docs/                   current-spec.md — the source of truth
  manifests/              ecosystem-manifest.yaml — declarative topology
  bus/                    embedded pub-sub bus + message envelope
  substrates/             provider-agnostic LLM access
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
    sensory/                deterministic; fans out to 3 (Impulse, Analytics, Personality)
    governance/              deterministic universal router
      routing.py              the routing contract, as data
      buffer.py               per-event bundling state
      knowledge_swarm.py      deterministic Parquet retrieval over the paths
                               Analytics proposes, run inline here
      agent.py                the dispatcher itself
    analytics/              substrate-backed
      contract.py             one task, response schema, fallback, knowledge_paths
      base.py                 working memory, loop detection, emission
      agent.py                AnalyticsMock — templated, zero cost
      live.py                 AnalyticsAgent — substrate-backed
    impulse/                deterministic drift + weighted appraisal
    archive_lookup/         the archive-grounded family; backs Personality
      contract.py             the shared keyword-findings format
      base.py                 one class, configured per role
      agent.py                ArchiveLookupMock — templated, zero cost
      live.py                 ArchiveLookupAgent — substrate-backed
    intent/                 substrate-backed, holds the veto
      contract.py             four registers, fail-closed on the two that gate
      base.py                 persona cache, conversation window, emission
      agent.py                IntentMock — templated, zero cost
      live.py                 IntentAgent — substrate-backed
    consolidator/           substrate-backed
      base.py                 per-event fact writer, no buffer/batch
      agent.py                ConsolidatorMock — templated, zero cost
      live.py                 ConsolidatorAgent — substrate-backed
    reflection/             substrate-backed; dispatch #4
      contract.py             write/idea/silent response schema
      base.py                 the rolling incident window, applying an outcome
      agent.py                ReflectionMock — always silent, zero cost
      live.py                 ReflectionAgent — substrate-backed
    security/                deterministic rule engine (config/security_rules.json)
    action/                  deterministic executor; sinks.py fans out to stdout/file/callback
    archive/                JSON-files-on-disk store + Parquet-backed structured store
  recovery/
    bootstrap.py            deterministic bootstrapper
    watchdog.py             passive 5-level escalation monitor
  tests/                    phase-numbered suite covering each role's contract, fallback,
                             and failure path — see the tests/ directory for the current list
```

## Running it

```bash
python -m venv .venv
source .venv/bin/activate        # Windows Git Bash: source .venv/Scripts/activate
pip install -r requirements.txt
pip install -r requirements-dev.txt    # vendor SDKs

export ANTHROPIC_API_KEY=...           # or point substrates: elsewhere in the manifest

# Is it wired up? Offline and free — the same check Recovery runs at boot.
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml

# Does it answer? One tiny call per model.
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml --live

# Bootstrap the ecosystem from the manifest
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

No key? Set `roles.analytics.mock: true` (and the other cognitive roles)
and everything above runs at zero cost.

A successful bootstrap prints each recovery step and ends with
`system live.`

Inspect the resulting Archive (JSON files, deliberately `cat`/`jq`-able):

```bash
cat data/archive/queue/events_*.jsonl | jq .
cat data/archive/working/drive_vectors.json
```

`data/archive/` is gitignored — it's runtime output, regenerated on
every bootstrap, never committed.

## Cost posture

- **Deterministic roles cost nothing.** Sensory, Impulse, Governance,
  Security, Action, Archive, and Knowledge hold no substrate.
- **Two calls per event in the steady state** — Analytics and Intent —
  plus one Consolidator call per event (off the live dispatch path, no
  buffer or batch) and one Reflection call per `batch_size` concluded
  events (further off the live path still). Personality adds a short,
  cheap, single-event lookup on the same fan-out; Knowledge's swarm adds
  N parallel Parquet lookups tier-scaled by `budget_tier`, at zero
  substrate cost.
- **Budget mode** is the floor under all of it: a classified substrate
  failure or a spend ceiling drops the ecosystem to $0/event without
  stopping it. Prices live in the manifest beside the model, so the
  estimate follows a vendor swap automatically.
- Tunable levers for keeping iteration cheap: `roles.reflection.batch_size`
  (fewer, larger reflection passes) and
  `timers.impulse.idle_musing_interval_sec` (less unprompted content).

## Swapping vendors

Agents ask for a substrate *class*; only the manifest knows the vendor.
Changing providers is an edit to one table and nothing else:

```yaml
substrates:
  low:
    provider: "ollama"                  # anthropic | openai | groq | vllm | echo | ...
    model: "llama3.1:8b"
    api_key_env: null                   # keyless, so base_url is required
    base_url: "http://localhost:11434/v1"
```

One OpenAI-compatible adapter covers OpenAI, Groq, Together, Mistral,
OpenRouter, vLLM, Ollama, LM Studio and in-house gateways. Adding a
vendor that speaks neither dialect is one `LLMProvider` subclass plus a
`register_provider()` call — no agent code changes.

## Roadmap

See [`docs/roadmap.md`](docs/roadmap.md) for what's left to build and
the longer-term direction, [`docs/current-spec.md`](docs/current-spec.md)
for full architecture detail, and [`docs/handover.md`](docs/handover.md)
for open design questions.

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
