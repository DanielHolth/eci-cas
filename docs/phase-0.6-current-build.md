# ECI-CAS — Current Build State (post Phase 0.6)

## Roles: all real, no mocks in the shipped manifest

```
Sensory      real
Impulse      real, deterministic
Governance   real, deterministic dispatcher (no substrate)
Analytics    real
Intent       real
Personality  real
Knowledge    real
Consolidator real (tier exists; lightly exercised — see Open Items)
Security     real, deterministic rule engine
Action       real, sink-based
Archive      store + bus door
```

Every role's mock class is still selectable via `roles.<name>.mock: true`,
so a zero-cost/offline ecosystem remains one manifest edit away.

## Pipeline shape

```
Sensory ──┬─→ Impulse      ─┐
          ├─→ Analytics    ─┤  (parallel, no Governance hop)
          ├─→ Personality  ─┤
          └─→ Knowledge    ─┘
                             └─→ Governance bundles all four → Intent
                                 (or fast-paths a Critical reflex → Security)

Intent  → Governance → Security
Security green  → Action
Security yellow → Intent (Review, one attempt)
Security red    → Intent (Revise, one attempt)
non-green twice → Action (Blocked incident)
Action → Governance → Consolidator (once event concludes)

Archive: direct write/query, plus bus door on events.archive
         (emits ArchiveWritten receipts on system.control)
```

## Config & boot behavior

- `config/security_rules.json` — externalized rule file, resolved via
  CWD / manifest dir / `./config` / repo root / shipped source config.
- Security refuses to boot without a usable rules file (silent-green
  failure is worse than not booting).
- Any lookup-family role declared "real" with an unusable substrate
  refuses to boot.
- Action: unknown sink type stops boot; empty sink list warns and runs a
  null sink (fails loud, not silent).

## Open items

- **Consolidator's live tier is unexercised.** Real reconciliation
  reasoning hasn't been driven end-to-end; batch threshold (25) means
  it's rarely hit even in live testing.
- **Security's 12 rules are a starting point**, not a tuned policy.
  `SecurityAgent.metrics` tracks green/yellow/red distribution but
  doesn't enforce one.
- **Consolidation doodle (UI concept):** on consolidation-pass
  completion, surface a clickable notice; click re-enters via Sensory.
  Blocked on `EpochWritten` carrying a real payload (epoch id + human-
  readable line) — Consolidator-live work.
- **Analytics `proceed` expression** — open since v0.35, undecided.
- **`spoken.jsonl` has no rotation** — unbounded growth, fine at current
  scale.
- **Intent fleet rotation (N>1)** — deferred, Phase 2+.

## Standing constraints

- `local-fast` requires a real local endpoint (Ollama/LM Studio/vLLM at
  `localhost:11434`) — never substitute a hosted model here.
- Manifest's current fast-reflex/deep-reasoning substrate is a
  deliberate cheap stress-test config, not a default to assume.
