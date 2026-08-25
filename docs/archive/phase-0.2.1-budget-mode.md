# Phase 0.2.1 — Budget mode

**Status:** implemented
**Scope:** additive. No topology change, no envelope change, no new agent.
**Spec:** ECI-spec-v0-34 (§5.4 Analytics, §5.7 Action, §10.2 substrates)

Real reasoning while the substrate is healthy and spend is within budget;
the deterministic fallbacks Analytics already has when it isn't. The
pipeline never stops, and it never degrades silently.

---

## What changed from the original design, and why

The design docs targeted a Claude Pro subscription and latched on "Pro
quota exhausted". That mechanism doesn't exist — a Pro subscription
cannot serve an API client, so there was no such error to catch, and the
`except RateLimitError` in the sketch would never have fired.

**The architecture around it was right.** Every scenario in
`budget-mode-design.md` still works. Three things changed:

| Design doc | As built | Why |
|---|---|---|
| Latch on Pro quota exhaustion | Latch on classified substrate failures + spend cap | Pro quota isn't reachable from an API client; these are |
| `$240/yr ÷ 365 = $0.66/day` headroom | Estimated USD from token usage vs. manifest prices | Subscriptions aren't metered in tokens — that comparison was a category error |
| Commands in `Sensory.ingest()` | Commands in `tools/console.py` | §5.2 keeps Sensory "an input field plus source-tagging"; a mode switch isn't perceived, it's control-plane |
| `NotificationQueue` → Action prepends alerts | Alerts print at the console | Action "executes exactly what Governance hands it" (§5.7). Authoring a status line would break that the same way a router writing dialogue would |
| New mock answers per task | Reuses `contract.fallback` verbatim | Budget mode having its own degraded behaviour is one more thing to get wrong |

That last row matters most. Budget mode has **no fallback behaviour of
its own** — it reuses Phase 0.2's per-task contract exactly. `Evaluate`
degrades and proceeds; `Review` and `Revise` decline.

---

## What latches it

```
manual       an operator said so
terminal     ONE unrecoverable failure — 400/401/403/404/413/422
transient    N consecutive recoverable failures — 408/409/429/5xx/529
spend_cap    estimated spend crossed the manifest ceiling
```

**The asymmetry between terminal and transient is the whole design.** The
vendor SDK already retries transients twice with backoff before we see
one, so a single 429 reaching us is noise, not a pattern — it takes three
in a row (`failure_threshold`) to latch. A 401 is different: the next
call fails identically, so counting to three is just three wasted calls.
It latches on the first.

A **contract violation is not a substrate failure.** The call succeeded
and was paid for; the model just answered out of shape. Those are counted
separately, so a run of bad JSON never latches budget mode — otherwise
one badly-worded prompt would look like an outage.

Unclassified failures are treated as transient. Mistaking a permanent
failure for temporary costs a few wasted calls; mistaking a temporary one
for permanent takes the pipeline down until a human intervenes.

---

## The safety property

Budget mode is most likely to be active exactly when something is already
wrong. So the question that matters isn't "does it switch" — it's **what
it lets through while switched**:

| Task | In budget mode |
|---|---|
| `Evaluate` | proceeds, with the templated recommendation |
| `Review` (yellow lane) | **declines** |
| `Revise` (red lane) | **declines** |

A gate that approves things *because the reasoner is unavailable* would
be worse than having no gate at all. This is the same reasoning as
v0.34's `anything-but-green → Analytics` rule, one layer down.

---

## Spend accounting

Prices are declared in the manifest **beside the model**, for the same
reason model ids are (§10.2) — they're vendor-specific and they change:

```yaml
substrates:
  deep-reasoning:
    model: "claude-haiku-4-5-20251001"
    price_per_mtok: { input: 1.00, output: 5.00 }
    timeout_sec: 60
```

An unpriced substrate reports **zero, not a guess** — and bootstrap warns
that the cap can't protect you.

> **The spend figure is an estimate.** It uses list prices and ignores
> cache discounts, batch pricing and tier differences. It's a smoke alarm
> for runaway loops, not an accounting control. The real number is in the
> vendor's console.

Also new: `timeout_sec`. The vendor SDK default is effectively unbounded,
and the bus dispatches synchronously (§3) — one hung call would hang the
whole pipeline.

---

## Console commands

Recognised before anything reaches Sensory, so they cost nothing and
never become events:

```
switch to budget mode     stop calling the substrate
switch to live mode       resume real reasoning
budget                    mode, calls, tokens, estimated spend
reset budget              zero the spend counters
```

Manual always wins — including re-enabling live after a spend-cap latch.
The cap guards against mistakes; it isn't a lock the operator can't open.
It does say so honestly: *"Note: estimated spend is still over the cap,
so it will latch again on the next call unless you raise it."*

Diagnostic (§12) is the natural long-term owner of these — it's already
the human-facing meta layer that never injects into the live queue.

---

## Persistence

State lives in Archive under a new working-tier `budget` kind, restored
at bootstrap **before any agent that could spend is provisioned**. A
latch that vanished on restart would be worse than no latch: the system
would come back live and start spending again before anyone noticed.

```
[recovery] budget mode: budget ($0.0024 spent, $5.00 cap)
```

Archive failures are swallowed — losing a state write must never break
the pipeline, and the in-memory mode is still correct for the session.

---

## What did not change

- Governance routing — table identical, still deterministic, still zero-cost
- Impulse, Security, Action, Archive interface
- The envelope protocol and `Envelope.reply()`
- Analytics' interface — it just doesn't call its substrate
- Loop detection and the control plane — both still native code, both still free

`BudgetManager` is deliberately **not an agent**: no inbox, publishes
nothing, sits outside the eight like Recovery and Watchdog (§2.2).

---

## Tests

```bash
pytest tests/ -v          # 205 offline, no key needed
```

`tests/test_budget_mode.py` (59) covers every latch path, the spend cap
and its warning, manual override, persistence across restart, the
console commands, and failure classification — including a test that runs
the **real vendor exception classes** through the classifier, so a vendor
renumbering a status code is caught here rather than in production.

---

## Known rough edge

`roles.analytics.temperature: 0.2` has no effect on the current Anthropic
SDK, which removed `temperature` from `Messages.create()`. The adapter
detects this, drops the parameter, and prints a one-time note; preflight
reports it too. It's kept in the manifest because it's meaningful for
other providers and may return.
