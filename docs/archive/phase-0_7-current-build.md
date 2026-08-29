# Phase 0.7 — Current Build

Date: 2026-08-25.

**Test suite: ~585 tests, all passing.** All remaining mocks eliminated. Every cognitive role runs on live substrate (LLM, store, or bus gate).

---

## 0. Consolidator: Fixed partial-batch loss

Daniel reported consolidation appearing to do nothing. Investigation found three interconnected issues:

1. Batch threshold (`batch_size_events: 25`) meant short console sessions never accumulated enough events to trigger.
2. Asynchronous processing (`synchronous: false`) meant background flushes completed after console output finished.
3. No explicit flush on shutdown — partial batches died with the process.

**Built:**

- `consolidate_now()` — force a pass over buffered events. Returns `False` on empty batch.
- `shutdown(timeout)` — consolidate partial batch then drain worker. Called automatically on process exit.
- Console command `consolidate` (aliases: `consolidate now`, `reconcile`) that forces an immediate pass and reports result.
- Flag `--consolidate-every N` — overrides threshold for current session only (does not edit manifest).
- Console banner displays active threshold at startup.

---

## 1. Security: Rules-based live agent (§5.6)

`SecurityMock` always returned green, leaving the hard stop unchecked.

**The solution: declarative rule patterns** (`config/security_rules.json`)

Rules are order-independent, pattern-based declarations. A rule fires when: (any of `any`) AND (all of `all`) AND (none of `unless`). No DSL, no scripting, no keyword lists — regularity patterns only.

**Rule shape:**
```json
{
  "id": "...",
  "verdict": "red|yellow",
  "concern": "...",
  "description": "...",
  "any": [...regex patterns...],
  "all": [...regex patterns...],
  "unless": [...regex patterns...]
}
```

**Design principles:**
- Order-independent: every rule tested, highest verdict wins.
- No green rules: green is the absence of a match.
- Rules with no conditions match nothing (safe default).
- Patterns compile at load time (typos fail bootstrap, never mid-request).
- Every rule requires a `concern` (templated, not free-form — non-green verdicts must be actionable).

**Shipped rule set:** 12 rules (v0.6.0). 7 red, 5 yellow. Covers credentials, impersonation, irreversible effects, autonomous spending, weapons/precursors, self-harm method detail, surveillance, system-boundary-bypass, system internals, third-party contact, private location, medical dosing.

**Implementation:**
- `SecurityAgent` (§5.6) evaluates `meta.proposed_action` (v0.35's input contract). Non-LLM, non-reasoning — audit trail stays complete.
- `meta.security_concern` comes from matching rule's own field (templated, not free-form).
- `meta.security_rules_matched` carries full audit trail; explanation is drawn from decisive rules only.
- Bootstrap **fails closed**: missing, unparseable, or empty rules file stops the system.

---

## 2. Action: Wired with real sinks (§5.7)

`ActionMock` always cleared; real-world output never tested.

**Built:**
- `ActionAgent` subclasses `ActionMock` (inheritance, not duplication). Executed/blocked logs, failure envelope, and `force_next_failures` remain the role contract.
- Configurable sink list. Two shipped sinks: `stdout` and `file` (log-path relative to `storage.root`). Fan-out is per-sink — one broken channel doesn't stop others.
- Failure envelope carries **original content**, not error messages — Governance's fallback quotes it through Intent, so diagnostics stay in `meta.action_errors`.
- Bootstrap: unknown sink type stops the boot (typos must not silently become silence). Empty sink list warns and runs null sink.

---

## 3. Personality & Knowledge: Archive-lookup family live (§5.1–§5.2)

v0.35b shipped these mock-first. Archive lookup was the only design element left open: relevance judgment over a bounded, supplied record set.

**Implementation:**
- `ArchiveLookupAgent` — one class, two configured instances (Personality, Knowledge).
- Model is asked for relevance judgment **only over supplied records**. Not asked to recall anything — parametric knowledge would silently become a second Analytics.
- Empty-store short-circuit: zero records → no substrate call, reported as `deterministic` (not degraded — this runs twice per event).
- Silence on degraded paths (outage, bad JSON, budget mode). This family gates nothing; bad answer costs more than no answer.
- `records_considered` in diagnostics — `relevant: false` means different things over 8 records vs. 1.
- Read-only view preserved: no write method reachable from these roles.

**Bootstrap:** Now honors `mock: false` instead of reporting it and running mock anyway. Unusable substrate stops the boot.

**Manifest:** Both roles flipped to `mock: false` on `fast-reflex`. Budget tiers mock entirely (Minimal has no credentials); Default/Super on `fast-reflex`.

---

## 4. Archive: Now an agent with bus door (§5.8)

Archive was the only role with no presence on the message bus.

**What was missing:**
- Store access required holding the store object — a much stronger grant than "ask Archive to append this".
- No external visibility — epoch writes produced no bus event.

**Built:**
- `ArchiveAgent` wraps the store and exposes a bus door.
- Topic `events.archive` for write requests (`Write`/`ArchiveWrite` envelopes).
- `ArchiveWritten` receipts on `system.control` for every completed request — **including when instructions were dropped** (visibility of silent loss).
- Total delegation: `write`, `query`, `query_queue`, `log_event`, vector ops, `root`. Callers cannot tell they're talking to an agent.
- **Reads stay synchronous** — direct `query` method unchanged.

**Design choice:** Consolidator was **not** migrated to async writes. It is the sole writer of long-term identity, synchronously by design. Fire-and-forget messaging would trade an auditable fact for a hope.

---

## 5. Manifest state

| Role | Change |
|---|---|
| `security` | `mock: false`, `rules: security_rules.json` |
| `action` | `mock: false`, `sinks: [stdout, file]` |
| `personality` | `mock: false`, on `fast-reflex` |
| `knowledge` | `mock: false`, on `fast-reflex` |
| `archive` | `mock: false` (bus door on) |

---

## 6. Still open

- **Consolidator's live tier exercise** — exists but untested end-to-end. Use `--consolidate-every 3` to watch real reconciliation.
- **Whether Analytics should stop expressing `proceed`** — flagged open since v0.35, still never decided.
- **Security rule tuning** — 12 rules is a starting position. Verdict counters on `SecurityAgent.metrics` are the instrument: high yellow rate on normal conversation means the rules need adjustment, not the engine.
- **`spoken.jsonl` rotation** — transcript grows without bound. Not urgent at this scale; real deployments will want date partitioning like the queue log.
