# Handover — starting Phase 0.7

Read this before writing any code, then read
`docs/phase-0.6-as-built.md`. This is the pickup point for a fresh
session.

## 1. Where things actually stand

**Phase 0.6 closed out §13.4's replacement sequence. There are no mocks
left in the roster.** All eleven roles run real in the shipped manifest:

```
Sensory      real since day one
Impulse      real since Phase 0.3 (deterministic)
Governance   real since Phase 0.1 (deterministic dispatcher, no substrate)
Analytics    live since Phase 0.2
Intent       live since Phase 0.4/0.5
Personality  live since Phase 0.6   <- was mock
Knowledge    live since Phase 0.6   <- was mock
Consolidator live tier exists; runs in the shipped manifest
Security     live since Phase 0.6   <- was mock, deterministic rule engine
Action       live since Phase 0.6   <- was mock, sink-based
Archive      store + bus door since Phase 0.6   <- was store-only
```

Every mock class is retained and selectable via `roles.<name>.mock: true`
— a zero-cost ecosystem is still one manifest edit away, and the offline
suite still runs entirely on mocks and scripted providers.

**Test suite: 607 passed, 13 skipped, 1 known pre-existing failure**
(`test_budget_mode.py::TestManifestConfig::test_it_reads_the_shipped_manifest`
— a spend-cap value reflecting Daniel's live-testing config, deliberately
left alone; not something to fix). Anything else is a regression to
explain, not ship past.

## 2. The pipeline, unchanged in shape

```
Sensory ──┬─→ Impulse      ─┐
          ├─→ Analytics    ─┤  (parallel, no Governance hop)
          ├─→ Personality  ─┤
          └─→ Knowledge    ─┘
                             └─→ Governance bundles all four, sends
                                 Intent one message (or fast-paths a
                                 Critical reflex straight to Security)
Intent  → Governance → Security      (now a real rule engine)
Security green  → Action             (now really emits)
Security yellow → Intent             (Review — one attempt)
Security red    → Intent             (Revise — one attempt)
non-green twice → Action             (Blocked incident)
Action → Governance → Consolidator   (direct call, once the event concludes)

Archive: direct write/query for every existing caller, plus a bus door on
         events.archive with ArchiveWritten receipts on system.control.
```

## 3. What's genuinely open

Pulled from Phase 0.6's own "Still open" section, not guessed.

**Consolidator's live tier has never really been exercised.** The class
exists and the manifest runs it, but nothing in the offline suite drives
real reconciliation reasoning end to end, and the batch threshold (25)
means live substrate testing rarely reaches it either. This is probably
the highest-value Phase 0.7 target: it is the only role whose real
behaviour nobody has actually watched. `python -m tools.console
--manifest manifests/ecosystem-manifest.yaml --consolidate-every 3` is
the cheapest way to see it work; the `consolidate` command forces a pass
at any time.

**Security's rules need tuning against real traffic.** 12 rules is a
starting position, not a policy. `SecurityAgent.metrics` counts
green/yellow/red without enforcing any distribution — §5.6's ~90/9/1
shape is a property of a good rules file, and those counters are the
instrument that tells you whether you have one. If ordinary conversation
is producing yellows, the rules file is wrong, not the engine. Editing
`config/security_rules.json` requires no code change and no restart of
anything but the process.

**The clickable consolidation doodle** —
`docs/ideas/consolidation-doodle.md`, new from Daniel this session. When
a consolidation pass finishes, surface it to the human as something
clickable; clicking feeds a new event back through Sensory ("the user
checked out what we just learned"). Daniel's dedup rule, verbatim: the
first click reconciles as *"oh, the user found it interesting that I
share what I've learned"*, and after that it is a duplicate Consolidator
ignores. Blocked on `EpochWritten` gaining a real payload (an epoch id
and a human-readable line), which is Consolidator-live work — see above.

**Whether Analytics should stop expressing `proceed`.** Flagged open
since v0.35, still undecided. Small either way. Worth a deliberate call
rather than remaining a permanent question mark.

**`spoken.jsonl` has no rotation.** Action's transcript grows without
bound. Fine at this scale; a real deployment wants the date-partitioning
the queue log already has.

**Real rotation across an Intent fleet** (N>1) — deferred since Phase
0.4, explicitly Phase 2+. Listed only so it isn't rediscovered as new.

## 4. Process notes

Unchanged from Phase 0.6's handover, and all still true:

- **Read the "as-built" docs before touching code.** Every phase has one
  in `docs/`. They carry not just what was built but *why*, including
  decisions Daniel made verbally that narrowed or overrode the spec draft
  — grep for "Daniel", most load-bearing calls are attributed and dated.
- **The manifest's `substrates:` block has live comments explaining a
  deliberate preprod stress-test swap** (fast-reflex/deep-reasoning on
  `gpt-5.4-nano`, cheapest available, on purpose). Don't revert it
  without asking.
- **`local-fast` needs a real local Ollama/LM Studio/vLLM endpoint at
  `localhost:11434`.** Don't swap it for a hosted model "to make it
  work" — that breaks Minimal/Budget's $0-and-credential-free contract.
  This exact mistake has been made once before (Phase 0.4's as-built
  records the revert).
- **Full suite before any handover:** `python -m pytest -q` from the repo
  root.
- **Daniel does his own substrate/live-model testing separately.** The
  offline suite (mocks + scripted providers) is the primary development
  loop, as every phase so far has done it. A session does not need API
  keys to make progress.

## 5. Two new things a fresh session should know

**Config files now exist.** `config/security_rules.json` is the first.
`Recovery._resolve_config_path()` finds a bare filename relative to CWD,
the manifest, `./config`, the repo root, or the config directory shipping
with the source — so copying a manifest to a tmp dir (which every test
fixture does) doesn't strand it.

**Two roles now fail the bootstrap rather than degrade.** Security
without a usable rules file, and any lookup-family role declared real
with an unusable substrate. In Security's case the reasoning is worth
internalising: its only failure mode that matters is answering green when
it shouldn't, which is indistinguishable from the mock — so it must not
boot at all rather than boot degraded. Action is deliberately the
opposite: an unknown sink type stops the boot, but an empty sink list
warns loudly and runs a null sink, because a mute system is discovered by
the first person who says hello.
