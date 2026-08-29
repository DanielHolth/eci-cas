# ECI-CAS — Current Specification

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)**: an always-on, persistent, multi-substrate AI persona
built on an embedded pub-sub message bus with deterministic,
storage-first orchestration.

This document describes the **Python** system as it runs today,
including a real limitation worth naming plainly: the bus
(`bus/pubsub.py`) dispatches synchronously and recursively rather than
decoupling agents — `publish()` does not return until every subscriber
(and everything that triggers) has finished. Sections below use
"asynchronous"/"off the live path" to mean *doesn't gate the routing
decision*, not *doesn't block wall-clock time* — Consolidator and
Reflection still run inline on the same call stack as everything else.
A from-scratch C# rebuild with genuinely decoupled, independently-
listening agents is scoped separately: see
[`csharp-rebuild-spec.md`](csharp-rebuild-spec.md). For what's planned
next in this repo, see [`roadmap.md`](roadmap.md). For in-flight
thinking not yet promoted here, see [`dispatch.md`](dispatch.md).

## 1. Core principles

1. **Emergence.** Personality is not authored into any single model — it
   emerges from the structured interaction of narrow deterministic and
   cognitive roles.
2. **Storage-first, deterministic orchestration.** System state lives in
   storage; computation is ephemeral. Recovery replays historical events
   rather than recomputing anything.
3. **Flat-cost context.** A bounded working-queue window keeps every
   live prompt's size flat regardless of history length. Memory
   consolidation happens asynchronously, off the live path, on a
   background thread.
4. **Substrate independence.** Agents ask for a logical substrate
   *class* (`fast-reflex`, `deep-reasoning`, ...); only the manifest
   knows which vendor and model backs each class.

## 2. System topology

An 11-agent ecosystem bound by an embedded in-memory pub-sub bus. All
inter-agent traffic is structured, auditable, message-passing envelopes.

| Role | Tier | Persona? | Responsibility |
|---|---|---|---|
| Sensory | deterministic | none | Ingests events, tags sources, fans out to Impulse, Analytics, and Personality in parallel |
| Impulse | deterministic | minimal | Drive-vector bookkeeping, reflexive appraisal, commands the Critical fast path |
| Analytics | cognitive | none | Worldly, parametric reasoning in unbiased keywords; proposes which knowledge paths are worth a look; isolated from Security |
| Personality | cognitive | rich | Read-only, single-event lookup over the Archive identity store |
| Knowledge | deterministic | none | Parquet-backed structured retrieval — a swarm of parallel predicate-pushdown lookups over the paths Analytics proposed, run inline by Governance; no LLM call |
| Governance | deterministic | none | Universal router. Buffers the parallel fan-out, bundles context, routes security clearances, enforces gating |
| Intent | cognitive | rich | Voices the response in-character. Holds veto and revision-gating power |
| Consolidator | cognitive | none | Asynchronously reconciles batched events off-path, writes to Archive, triggers persona-cache refreshes |
| Security | deterministic | none | Rule-based gate evaluating actions against declarative static rules |
| Action | deterministic | none | Executes authorized commands (stdout, file, or other sinks). Silent on success |
| Archive | deterministic | none | Append-only structural storage; tracks epochs; the only door to memory |

### 2.1 Fan-out and routing

1. **Parallel fan-out (ungated).** Sensory ingests an event and
   publishes three duplicate envelopes in parallel — to Impulse,
   Analytics, and Personality — bypassing Governance to avoid a
   serialization bottleneck.
2. **Knowledge swarm.** Once Analytics reports back with a list of
   `(category, topic)` paths, Governance drills into each one via
   deterministic Parquet predicate pushdown (`agents/governance/knowledge_swarm.py`),
   in parallel, tier-scaled by `budget_tier`. This is retrieval, not
   judgment — the model's only job here was picking which paths to look
   at, in Analytics.
3. **Governance buffering and bundling.** Governance holds an
   `EventState` slot keyed by `event_id`, tracks the running maximum
   severity as the three worker reports and the swarm result arrive, and
   bundles everything into one structured envelope routed to Intent.
4. **Universal gating router.** Outside the initial fan-out, Governance
   mediates every hop. No cognitive agent addresses another directly;
   routing is data-driven and auditable.

### 2.2 Message envelope

Every bus transaction carries a standardized JSON-Lines envelope:

- `event_id` — correlates every hop within one logical conversation turn
- `hop_count` — increments on each `envelope.reply()`, detects routing loops
- `source` — sending agent name
- `content` — the raw prompt or target content
- `meta` — dict carrying `reflex` (Impulse's appraisal), `expression`
  (the live emotional mapping), `verdict` (`green | yellow | red`),
  `analytics` (recommendation + `knowledge_paths`), `intent` (register,
  voiced speech, gating signal), `diagnostics` (latency, tokens, cost)

### 2.3 Severity

Four-tier scale: **Restful < Neutral < Elevated < Critical**.

- **OR-upscale-only.** Any agent may raise severity; nothing downstream
  ever lowers a severity level set upstream.
- **Impulse ceiling.** Impulse's own urgency read is hard-capped at
  Elevated — drive-vector state alone can never manufacture Critical.
- **Sensory override.** Only external, hardware-level input or an
  explicit user signal ingested via Sensory can tag Critical.

## 3. The live pipeline and safety gating

```
                   Sensory (Ingest)
                         |
      +------------------+------------------+
      | (fan-out)                           | (Critical)
      v                                     v
 [Impulse, Analytics,                  Governance
  Personality] + Knowledge swarm            |
      |                                 Security
      v                                     | (red)
 Governance (buffer & bundle)               v
      |                                  Intent (Revise)
      v                                     |
    Intent (voice bundle)                   v
      |                                  Governance
  Governance                                |
      |                                     v
  Security                               Action
      |
      +----------------+----------------+
      | green           | yellow          | red
      v                 v                 v
   Action           Intent (Revise,    Action
                     one attempt,     (Blocked,
                     then Action      immediate)
                     regardless)
```

### 3.1 Gating decision matrix

Security feedback is severed from Analytics entirely. Both non-green
lanes are handled natively by Governance:

- **Green.** Rules-cleared. Governance routes `meta.proposed_action`
  straight to Action.
- **Yellow (doubt).** The rules don't cover the situation — this is not
  a violation, it's the rules declining to judge. Routes to Intent for
  one revision attempt; if the revised answer is yellow again, it
  proceeds to Action anyway rather than blocking. Blocking on mere
  ambiguity would make every unresolved judgment call a hard stop, which
  is red's job description, not yellow's.
- **Red (violation).** Rules are explicitly violated. Governance routes
  straight to a deterministic **Blocked** notice on Action — no
  revision attempt. A rule violation is an outcome, not a negotiation.

### 3.2 Blocked notice

A red verdict produces a model-independent block sequence:

1. A deterministic `Blocked` notice template goes to Action.
2. A frustration nudge is injected on the control plane back to Impulse
   (urgency +0.15, fatigue +0.05, temperature -0.05).
3. The notice carries a `meta.expression` word (angry, scared, sad, ...)
   matching Impulse's real-time appraisal.
4. `meta.security_alert: true` is logged to cold storage.

### 3.3 Critical reflex fast-path

When Sensory ingests an event tagged with explicit physical-danger
metadata, severity is forced to Critical. Governance's fan-out buffer
short-circuits — it discards the incomplete Analytics/Personality slots
and routes Impulse's raw reflex straight to Security, skipping Intent's
voicing on the way in. If Security marks the reflex red, it re-enters
normal routing through Governance to Intent's Revise register. Security
is never bypassed.

## 4. Cognitive agent contracts

Every cognitive role has a strict, code-enforced output contract that
parses model responses and applies deterministic fallbacks on failure.
Agents return plain text, not JSON — all structure is imposed
deterministically in code, on the code side of the contract.

### 4.1 Fallback posture

- **Non-gating tasks** (Evaluate, Advise, Refuse) **fail open** — a
  substrate failure falls back to a deterministic templated response so
  the conversation keeps flowing.
- **Gating tasks** (Review, Revise) **fail closed** — an API error, bad
  output, or ambiguous signal forces `proceed: false`. A failure to
  reason is never read as clearance to act.

### 4.2 Analytics/Intent boundary

Analytics writes analysis; Intent writes speech. Intent must not echo
Analytics' raw recommendation back to the human — an `is_parroting()`
check rejects a near-verbatim match as a contract violation and
degrades to the fallback speaker. A refusal is delivered as an
in-character lead-in (max 120 characters) from Intent, with the
structural reason and safety concern appended in native code — Intent
never gets to accidentally soften or assent to a block.

## 5. Memory and identity

Three-tier, asynchronous memory hierarchy decoupling active session
identity from background structural storage:

```
  LIVE MEMORY (in-process)
    - Working queue window (last N events)
    - Persona cache (Core Anchors + Evolving Trait Delta)
         ^                                   |
         | EpochWritten refresh              | batch threshold
         |                                   v
  CONSOLIDATOR (background worker thread)
    - Batch buffer triage
    - One reasoning call per batch, off the live path
         |
         | N mechanical writes
         v
  ARCHIVE STORE (JSON files + Parquet structured store)
    - Identity store (Core Anchors, epoch history)
    - Knowledge store (facts, narrative, structured swarm-queryable)
    - Log queue (audit trail)
```

- **Working queue.** Rolling short-term history of raw inter-agent
  exchanges, capped at a default 10-event window.
- **Knowledge store.** Local narrative facts, rules, places. Written
  exclusively by Consolidator; queried via the structured Parquet store
  by the Knowledge swarm.
- **Identity store.** Core Anchors (~1k tokens of stance, values,
  boundaries) plus immutable trait-delta epochs.

### 5.1 Persona caching

Every cognitive call is stateless. Intent's persona is hydrated once at
bootstrap and cached in memory — zero live Archive reads per event. When
Consolidator writes a new epoch, it publishes `EpochWritten` on
`system.control`; Intent subscribes and re-hydrates its cache in the
background.

### 5.2 Consolidator batching

Consolidator buffers concluded events. When the buffer reaches
`batch_size_events`, it swaps the buffer atomically and reasons over the
whole batch in **one call**, emitting N mechanical write instructions
(destination store, tags, payload) rather than one write per event.
Runs on a dedicated worker thread so the batch that trips the threshold
never makes the human wait.

Sensory-sourced prompts and dialogue facts route to Knowledge;
security yellow/red alerts route to Knowledge tagged
`knowledge:security`; Intent's self-reflection routes to Identity; the
summarized batch delta is written as a new versioned epoch.

### 5.3 Slow-coloring feedback

Consolidator can nudge a drive-vector *baseline* (not the live value) by
up to ±0.2 per pass — the coupling that lets consolidated experience
color future reflexive temperament without causing mood swings. It
takes hours of wall-clock drift for live vectors to catch up to a moved
baseline.

### 5.4 Somatic shortcut

Direct physical feedback (approval/disapproval) bypasses cognitive
review: Sensory tags it, Impulse shifts drive vectors instantly with no
Intent pre-approval, and Intent reviews the alignment impact
retroactively at the next consolidation pass.

## 6. Infrastructure and monitoring

### 6.1 Watchdog

Two independent timers, both manifest-configured:

- **Watchdog timer** (seconds-scale) — "is the machinery alive?" Default
  5-second interval, five-level escalation ladder: a zero-token
  deterministic ping, an in-band synthetic `SystemCheck` through
  Analytics, an out-of-band ping to Governance, a soft rollback
  (flush + restore snapshot + rebind bus), and a catastrophic full
  rebuild.
- **Idle musing timer** (hours-scale) — "has it been quiet long enough
  that the persona should initiate a thought?" Default 7200s.

### 6.2 Recovery bootstrap

Zero-credential, deterministic Infrastructure-as-Code bootstrapper —
runs even with every model endpoint down:

1. Manifest validation
2. Storage initialization
3. Deterministic-tier provisioning (Sensory, Impulse, Security, Action, Archive)
4. Cognitive hydration (Core Anchors into Identity, cognitive routing sockets)
5. Bus binding + Watchdog listeners
6. Liveness validation (`BootCheck` round trip)
7. System live

## 7. Budgets and cost control

### 7.1 Budget tiers

Declarative combinations mapping cognitive roles to substrate quality,
set via `budget_tier` in the manifest:

| Tier | Analytics | Intent | Consolidator |
|---|---|---|---|
| minimal | mocked (zero cost) | local keyless | local keyless |
| budget | local keyless | local keyless | fast hosted |
| default | deep hosted | fast hosted | fast hosted |
| super | deep hosted | fast hosted | expensive specialist |

Knowledge's swarm width/depth (`agents`, `max_results_per_agent`) is
tier-scaled independently — it's deterministic retrieval, not a vendor
choice.

### 7.2 Budget mode

An automatic runtime latch protecting against rate limits, outages, or
token runaway:

- **Manual** — operator console command.
- **Terminal** — one unrecoverable failure (bad key, unknown model id).
- **Transient** — three consecutive recoverable failures (timeout, rate
  limit, overload).
- **Spend cap** — cumulative estimated cost crosses `spend_cap_usd`.

When latched, cognitive roles are bypassed in favor of deterministic
fallbacks: ordinary tasks degrade gracefully, gating tasks fail closed.

### 7.3 Manifest

All components, limits, and pricing live declaratively in
`manifests/ecosystem-manifest.yaml`. Swapping a vendor is a one-line
edit to a substrate class's `provider`/`model`/`base_url` — no agent
code changes.
