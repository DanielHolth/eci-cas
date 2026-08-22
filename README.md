# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as an
8-agent ecosystem on a pub-sub message bus. Personality emerges from the
interplay of narrowly specialized roles, not from any single agent, and
every identity change is written as an immutable, source-attributed
epoch you can audit.

Full architecture: [`docs/ECI-spec-v0-30.md`](docs/ECI-spec-v0-30.md) —
the living specification and technical source of truth. Read that
before touching code; this README is just the map.

## Status

**Phase 0 — Mockup & Mimic** (§13). 7 mocks + 1 real component (Sensory)
validate the queue topology end to end, at zero LLM cost. See the
phased roadmap in the spec (§14) for what Phase 1+ unlocks.

## Structure

```
eci-cas/
  docs/                  ECI-spec-v0-30.md — the source of truth
  manifests/              ecosystem-manifest.yaml — declarative topology (§10)
  bus/                    embedded pub-sub bus + message envelope (§3)
  agents/
    sensory/               REAL — the only non-mocked role in Phase 0 (§5.2)
    impulse/  governance/  analytics/  intent/  security/  action/
                            MOCKS — replaced one per cycle per §13.4
    archive/               JSON-files-on-disk store (§5.8, §13.2)
  recovery/
    bootstrap.py            deterministic bootstrapper (§9, §9.1)
    watchdog.py              passive 5-level escalation monitor (§11)
  tests/
    test_phase0_e2e.py       §13.3 exit-criteria harness
```

## Running it

```bash
python -m venv .venv
source .venv/bin/activate        # Windows Git Bash: source .venv/Scripts/activate
pip install -r requirements.txt

# Bootstrap the ecosystem from the manifest (§9.1)
python -m recovery.bootstrap --manifest manifests/ecosystem-manifest.yaml

# Run the Phase 0 exit-criteria test suite (§13.3)
pytest tests/ -v
```

A successful bootstrap prints each of the seven §9.1 steps and ends with
`system live.` The test suite proves the full worked example (§3.2) is
reproducible from a cold bootstrap, twice in a row, with identical
queue traces — the literal Phase 0 exit criterion.

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
```

Each cycle: replace exactly one mock, re-run Recovery, re-run the test
suite, confirm the trace still holds before moving to the next.

## Cost posture (Phase 0-1)

- **Phase 0**: zero LLM calls, by construction — all 7 mocks respond
  with templated/hardcoded output (§13.1).
- **Phase 1**: single substrate class for all cognitive roles is
  mandated by the spec (§14) — no multi-substrate decision to make yet.
  The manifest defaults `fast-reflex` and `deep-reasoning` to the same
  cheap model; real diversity arrives at Phase 3 (§7.5) with a one-line
  manifest change (§10.2).
- Tunable levers for keeping iteration cheap: `rotation.batch_size_events`
  (fewer, larger consolidation batches) and
  `timers.impulse.idle_musing_interval_sec` (less unprompted content) —
  see §15 for the full tunable-defaults table.

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
