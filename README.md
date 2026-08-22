# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as an
8-agent ecosystem on a pub-sub message bus. Personality emerges from the
interplay of narrowly specialized roles, not from any single agent, and
every identity change is written as an immutable, source-attributed
epoch you can audit.

Full architecture: [`docs/ECI-spec-revisions-v0-34.md`](docs/ECI-spec-revisions-v0-34.md)
— the living specification and technical source of truth. Read that
before touching code; this README is just the map.

## Status

**Phase 0.1 — Governance real** (§13.4). 6 mocks + 2 real (Sensory,
Governance). Governance is the first mock replaced — and building the
LLM-backed version established that the role needs no model at all, so
it ships **deterministic**. Doubt about a safety verdict routes to
Analytics rather than to a model in the router seat.

The cognitive tier is therefore exactly Analytics and Intent. See
[`docs/ECI-spec-revisions-v0-34.md`](docs/ECI-spec-revisions-v0-34.md)
for the reasoning and [`docs/phase-0.1-governance.md`](docs/phase-0.1-governance.md)
for the implementation. §14 has the phased roadmap.

## Structure

```
eci-cas/
  docs/                   ECI-spec-revisions-v0-33.md — the source of truth
                           (v0.30-v0.32 kept for history)
                          phase-0.1-governance.md — this cycle's implementation notes
  manifests/              ecosystem-manifest.yaml — declarative topology (§10)
  bus/                    embedded pub-sub bus + message envelope (§3)
  substrates/             provider-agnostic LLM access (§10.2)
    base.py                 LLMProvider ABC, Substrate, request/response
    providers.py            Anthropic, OpenAI-compatible, Echo
    registry.py             manifest substrate class -> Substrate
  agents/
    sensory/                REAL — deterministic (§5.2)
    governance/             REAL — deterministic dispatcher (§5.1, v0.34)
      routing.py              the routing contract, as data
      agent.py                the dispatcher itself
    impulse/  analytics/  intent/  security/  action/
                            MOCKS — replaced one per cycle per §13.4
    archive/                JSON-files-on-disk store (§5.8, §13.2)
  recovery/
    bootstrap.py            deterministic bootstrapper (§9, §9.1)
    watchdog.py             passive 5-level escalation monitor (§11)
  tests/
    test_phase0_e2e.py         §13.3 exit-criteria harness
    test_phase01_governance.py Phase 0.1 — the dispatcher, the three lanes,
                               the fail-safe, and the substrate registry
```

## Running it

```bash
python -m venv .venv
source .venv/bin/activate        # Windows Git Bash: source .venv/Scripts/activate
pip install -r requirements.txt

# No API key needed: every role in Phase 0.1 is deterministic or mocked.
# The substrates table is declared and checked, but nothing consumes it
# until Phase 0.2 puts Analytics on it.

# Bootstrap the ecosystem from the manifest (§9.1)
python -m recovery.bootstrap --manifest manifests/ecosystem-manifest.yaml

# Run the full test suite — offline, free, no key needed
pytest tests/ -v
```

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
- **Phase 0.1**: still zero, by construction. Governance holds no
  substrate — every hop is settled by the envelope, and a verdict it
  can't read goes to Analytics rather than to a model. See
  [v0.34](docs/ECI-spec-revisions-v0-34.md).
- **The steady state**: two model calls per event, ever — Analytics and
  Intent. Six of the eight roles are deterministic, so the flat-cost
  claim rests on a short list of things to keep cheap.
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
