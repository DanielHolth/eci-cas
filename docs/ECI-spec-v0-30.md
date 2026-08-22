# Emergent Cognitive Identity (ECI) — Specification v0.30

This is the technical source of truth for ECI. It is a living specification, versioned alongside the implementation; spec drift is a bug. Future revisions follow the naming pattern `ECI-spec-vX-YY.md`.

---

## TL;DR

ECI is a persistent, evolving AI persona — a personal "Jarvis" — built as an **8-agent ecosystem** on a pub-sub message bus. Personality is not authored into any single agent; it *emerges* from the interplay of narrowly specialized roles, and every change to it is written as an immutable, source-attributed epoch you can audit.

The engine underneath (the **Continuous Agent System, CAS**) keeps cost flat and uptime continuous: the live pipeline reads only recent context; history is digested asynchronously by a "sleeping" Intent node; identity snapshots persist in cheap analytical storage. State lives in storage, computation is ephemeral, and recovery is always a replay — never a re-computation.

**The 30-second architecture:** every event flows `Sensory + Impulse → Governance → Analytics → Intent → Security → Governance → Action`. Governance routes but never decides; Analytics reasons; Intent carries the personality (as a rotating fleet of N nodes that take turns being awake and consolidating); Security gates; Action and Archive are the only doors to the world and to memory. Recovery bootstraps everything from one declarative manifest; Watchdog monitors liveness; Diagnostic is a read-only mirror for the human.

**Build order:** Phase 0 runs 7 mocks + 1 real component (Sensory) to validate the queue topology with zero LLM cost, then replaces one mock per cycle. Storage starts as JSON files on disk and upgrades to SQLite/Parquet behind a stable interface. The Intent fleet starts at N=1 and scales to N=2 (zero-downtime) and N=3 (multi-substrate diversity) with no pipeline changes.

---

## 1. Vision & Terminology

```
THE PRODUCT:    Emergent Cognitive Identity (ECI)
THE ENGINE:     Continuous Agent System (CAS)
THE HOOK:       Persistent, multi-model AI that evolves 24/7
                at a fraction of standard context costs.
```

**ECI** is what the human experiences: a coherent persona that remembers context, learns preferences, and grows through interaction — with an auditable identity history. **CAS** is what makes it viable: a deterministic, always-on orchestration layer running the 8-agent ecosystem, with N-node Intent rotation, off-peak consolidation, and storage-first recovery.

**Why the architecture matters:** the live pipeline reads only recent context (a few K tokens); consolidation of history happens asynchronously on a sleeping Intent node; identity snapshots persist as immutable, versioned epochs. Cost per request stays flat as history grows, the persona never cold-starts, and state always survives computation failure.

### 1.1 Core working principles

1. **Storage-first & deterministic** — state lives in immutable storage; computation is ephemeral; recovery is a replay, never a re-computation.
2. **Substrate agnostic** — the manifest declares *substrate classes* (§10.2), not vendor model names. Vendor mapping is one table, changed in one place.
3. **Living specification** — this document is versioned alongside the implementation.
4. **80/20 proof of concept** — edges don't need to be perfect; personality and continuity emerge from role interaction, not from any single agent.

---

## 2. Architecture at a Glance

### 2.1 The 8 roles

| Role | Category | Tier | Persona? | Core job |
|---|---|---|---|---|
| **Governance** | Orchestration | Cognitive (temp 0.0) | None | Routes messages between agents; manages Intent node rotation. Decides nothing itself. |
| **Sensory** | Input | Deterministic | Minimal | Ingests events (prompts, feedback signals; vision/audio/HTTP later). Tags source. |
| **Impulse** | Input / drive | Deterministic | Minimal | Reflexive first reaction; absorbs reward signals; self-triggers idle musing. Tunable drive vectors + temperature. |
| **Analytics** | Cognition | Cognitive (temp 0.2) | None | Reasoning, pattern/trend detection, working memory. The heavy lifter. |
| **Intent** | Identity | Cognitive (temp 0.7, N-node) | Rich | Personality, values, moral compass. Advisory only. |
| **Security** | Safety gate | Deterministic | None | Graded, stateless-per-event pass/warn/block. Watches everything, acts rarely. |
| **Action** | Output | Deterministic | None | The only door to the outside world. |
| **Archive** | Storage | Deterministic | None | The only door to memory. Pure query executor. |

**Tiers:** "Deterministic" = native code service, no LLM call per event (fast, cheap, predictable). "Cognitive" = LLM-backed reasoning, used only where flexible judgment or personality is the point. Tier is an implementation detail, not a change to any role's responsibility.

### 2.2 External infrastructure (not part of the 8)

| Component | Access | Trigger / role |
|---|---|---|
| **Recovery** | Write (deploy/restore) | Sole deployment & bootstrap backbone. Invoked by Governance (catastrophic failure), Watchdog (deadlock escalation), or direct user request. |
| **Watchdog** | Passive monitor | Always running. Tracks queue-transition intervals, escalates through 5 levels, can invoke Recovery. |
| **Diagnostic** | Read-only | On-demand, human-initiated meta-analysis of long-term behavior and persona evolution. Never touches the live queue. |

None of these carry a persona, and none are part of the roster Recovery rebuilds as "the ecosystem." The symbiotic loop is the 8 agents + human; Diagnostic is the mirror.

---

## 3. Message Bus & Protocol

All 8 agents communicate over a shared structured queue:

```
{ Source, Destination, Type, Content, Severity }
```

Implemented as an embedded pub-sub bus, one topic per agent inbound channel, plus two system topics:

```
events.sensory | events.impulse | events.governance | events.analytics
events.intent  | events.security | events.action

system.diagnostic  (BootCheck / SystemCheck synthetic pings)
system.control     (Governance → Intent-node rotation signals, §7.2)
```

Watchdog needs no dedicated topic of its own — it passively listens to the existing `events.*` topics and measures the silence between transitions (§11).

Control traffic never mixes with business events.

### 3.1 Sensory source types

```
prompt    — human text input (POC)
feedback  — reward signal: { valence, driver, note }
vision    — future
audio     — future
https     — future (webhooks / external events)
```

Feedback payload example:

```json
{ "source": "feedback", "valence": 0.3, "driver": "curiosity", "note": "That was good." }
```

### 3.2 Worked example — "Hello there, are you awake?"

```
Sensory   → Governance : { Type: Prompt, Content: "Hello there, are you awake?", Severity: Restful }
Impulse   → Governance : "Calm reaction"
Governance→ Analytics  : "Evaluate intent based on the prompt and the calm reaction"
Analytics → Intent     : "All agents awake. Prompt asks if you are awake."
Intent    → Governance : "Awake and pleased to interact. Let's give a warm response."
Governance→ Security   : "Warm response to prompt 'is awake'"
Security  → Governance : "Green"
Governance→ Action     : { Type: Speech, Content: "Hey there! I'm awake..." }
```

Sensory and Impulse fire **in parallel** — Governance's first job on any event is to wait for and merge two near-simultaneous inputs.

---

## 4. Core Reactive Pipeline

```
Sensory + Impulse → Governance → Analytics → Intent → Security → Governance → Action
```

- Analytics and Intent are **advisory**. Only Governance issues the action, and only after Security clears it.
- A Security **hard "No"** is the one loop in an otherwise linear pipeline: Governance returns to Analytics + Intent for a revised course, then resubmits to Security.
- Action's outcome (success **or failure**) re-enters the system exclusively as a new **Sensory** input — no separate feedback channel (proprioception model).

### 4.1 Reward path (somatic shortcut)

Reward/penalty signals (`source: feedback`) take a deliberately shorter path:

1. Sensory ingests and tags the feedback event.
2. **Impulse shifts drive vectors immediately** (e.g. `curiosity += 0.3`) — no Intent pre-approval.
3. **Intent reviews alignment retroactively** during its next consolidation: *did the reward match our values?*
4. Security observes all traffic and can flag safety concerns, but reward ingestion is **not** a Security gate — it's somatic. Dopamine first, reflection later.

Only direct human input is accepted on the feedback channel. Any future feedback source (automated evaluators, external services) must be authenticated and weighted before it reaches Impulse — the somatic path is a reward-hacking surface and is guarded by construction.

---

## 5. Role Details

### 5.1 Governance — *deterministic backbone, cognitive implementation*
No persona, no opinions, never explains itself. Runs at temperature 0.0 with a **per-event statutory context reset** — it holds no memory across events, by design, which is what keeps it from accumulating opinions.

- Listens to Sensory + Impulse; delegates reasoning to Analytics, ethical judgment to Intent.
- Clears every action with Security before it reaches Action; on a Security "Red," returns the payload to Analytics for revision.
- The only one of the 8 allowed to request Recovery.
- Owns the Intent node rotation registry (round-robin list of node IDs) and issues `SweepAndProcess` control signals over `system.control` (§7.2). Routing to `events.intent` never changes with fleet size — scaling Intent requires zero Governance code changes.

### 5.2 Sensory — *deterministic*
Input channel only. Tags every event with its source type (§3.1) and, where natural, a light category — the heavy lifting stays with Analytics. May carry structured/non-text data when the source isn't a prompt. Also the re-entry point for Action's outcomes (proprioception), the ingestion point for feedback signals (§4.1), and the channel Recovery uses for synthetic diagnostic pings (`BootCheck`, `SystemCheck`).

### 5.3 Impulse — *deterministic*
The reflexive gut, subconscious drive, and spark of spontaneity.

- **Drive vectors** — continuous scalars (`Curiosity`, `Fatigue`, `Urgency`, `Social Drive`) that drift over time or shift instantly on input, including feedback signals. They shape reflexive output directly: high `Curiosity` → exploratory reactions; high `Urgency` → terse, protective ones.
- **Somatic priming** — injects an immediate emotional baseline into Governance *before* Analytics has processed context.
- **Idle musing** — when the queue has been organically quiet past `idle_musing_interval` (hours-scale, §11.1), Impulse self-triggers: pulls a random or recent high-sentiment item from Archive and pushes a speculative observation, question, or story-starter to Governance (*"We haven't checked on that project in two days"*). This is the **only** channel through which unstimulated behavior — content not triggered by a Sensory input — enters the queue; Watchdog audits liveness but never originates content (§11.1), so animation stays sole-sourced and doesn't risk double-triggering.
- **Trigger tagging** — every emission carries `triggered_by: sensory | self`, so Governance can distinguish reflex from unprompted musing.
- **Temperature** — scale `0.1` (reactive, quiet) → `1.0` (volatile, frequent unprompted musing). Persisted; survives Recovery.
- **Recalibration** — if Impulse repeatedly draws Security flags or bogs down Analytics, Governance logs the noise level, but only **Intent** adjusts the temperature, during consolidation — a values judgment, not a security one.

### 5.4 Analytics — *cognitive, temp 0.2, rolling 10-event window*
Two jobs:

1. **Reasoning** — takes Sensory + Impulse input, produces a recommendation, hands it to Intent for a final read.
2. **Working/trend memory** — pattern and loop detection over recent history; owns the rolling concern-trend counter (kept here so Security stays stateless per event). During consolidation, de-duplicates Intent's temp reflections, detects "twin" traits, and produces concise delta reports for the consolidating Intent node.

Also the agent that queries Archive for prior context, iterating on the query a few times before answering Governance. An action repeated **3 times** without state change is declared a loop and triggers graceful degradation.

### 5.5 Intent — *cognitive, temp 0.7, N-node fleet*
The core of simulated personality. Deliberately under-specified so it can develop its own direction. **Advisory only** — Governance and Security hold all real veto power, so Intent's autonomy only ever manifests as pushback in the reasoning chain. Sticks to its acquired integrity over the socially easier answer.

Owns escalation-tier ethical judgment: after repeated Security reds, Intent is the one that renders "that would be unethical," which Governance relays without originating.

Intent runs as a fleet of N interchangeable nodes rotating through `Awake → Consolidating → ReadyToSwap` (§7). The structure is N-generic from day one; the prototype runs N=1, scaling to N=2 (zero-downtime) and N=3 (multi-substrate diversity) with no pipeline changes.

**Scaled memory storage:**

| Layer | Size | Lives in |
|---|---|---|
| Core Anchors | ~1k tokens, fixed, non-negotiable | Permanent active context |
| Evolving Trait Delta | ~2k tokens, active | Active context, updates each consolidation |
| Archived Persona Epochs | Unbounded | Cold storage via Archive, queried only for deep historical reflection |

### 5.6 Security — *deterministic, rule-based*
A passive observer. Sees **all** inter-agent traffic, not just final actions — but watching is different from acting, and it intervenes rarely. **Stateless per event**, evaluated against `security_rules.json` — every decision must be justifiable from that single event alone.

- **Graded response:** ~90% silent · ~9% advisory warning (non-blocking) · ~1% hard "No" (blocking; forces Governance back to Analytics + Intent).
- **Situational absolute overrides** tied to physical safety context (e.g. driving, operating a stove) apply regardless of the graded score.

### 5.7 Action — *deterministic*
The only door to the outside world. Speech for now; future actuators register via the manifest's skill registry, with Security rules extended per skill. No persona, no judgment — executes exactly what Governance hands it after Security clearance. Reports outcomes back exclusively via Sensory. Three failures trigger a loop check via Analytics and graceful degradation (e.g. failed speech → fall back to a display/text action).

### 5.8 Archive — *deterministic*
The only door to memory. Symmetric with Action but for reads/writes instead of world-effects. The thinnest blueprint of the eight: *"executes queries, holds no opinion about what they mean."*

**Storage is phased** (§14); the *interface* is stable across all phases:

```
POST /archive/write   (append)
GET  /archive/query   (parameterized read)
```

| Phase | Hot layer | Cold layer | Query engine |
|---|---|---|---|
| 0 — Mockup | JSON files (JSONL append) | JSON files | grep / jq |
| 1 — Real agents | SQLite / embedded KV (WAL) | JSON files | SQL + jq |
| 2+ — Scale | SQLite / embedded KV (WAL) | Partitioned Parquet `/archive/{year}/{month}/{kind}.parquet` | DuckDB read-only columnar scans |

**Direct recovery access** (all phases): an un-indexed, file-level fallback lets Recovery read blueprints and historical queue state straight off disk if the database layer or live agents are unavailable.

---

## 6. Memory Model — Three Tiers, Three Owners

| Tier | Contents | Owner (decides what's kept) | Executor | Storage mechanics |
|---|---|---|---|---|
| **Identity** | Values, preferences, moral stance | Intent | Archive | Core Anchors + Evolving Delta live in context; historical epochs versioned to cold storage as immutable deltas (§7.4) |
| **Knowledge** | Facts, stories, rules, places, culture | Intent (triages at consolidation), Sensory (ingests) | Archive | Cold storage, tagged `kind`: `fact / rule / place / story / person / misc` — extensible |
| **Working queue** | Raw inter-agent message history | — | Archive | Hot layer, rolling 3-day cutoff, then flushed to cold storage or pruned |

A story Intent chooses to keep lands in the Knowledge tier, tagged as a story, written during the next consolidation pass — not held in Intent's own identity data.

---

## 7. Intent Lifecycle, Rotation & Consolidation

### 7.1 Node lifecycle states

Every Intent node maintains its own dedicated subscriber to `events.intent` and cycles through three states:

1. **`Awake`** — receives active queue events from Governance; generates live identity/ethical advice; logs provisional reactions to its live temp ledger.
2. **`Consolidating`** — completely unhooked from live event processing. Reconciles past event batches with Analytics, updates Evolving Trait Deltas, recalibrates Impulse's temperature, writes a versioned epoch to Archive. Signals `Status: ReadyToSwap` to Governance on completion.
3. **`ReadyToSwap`** — sits idle awaiting Governance's rotation signal; passively buffers unread `events.intent` messages **without invoking any LLM calls**.

### 7.2 Rotation protocol — Deterministic Sweep & Execute

```
(Node A: Awake) ──finishes batch──▶ (Node A: Consolidating)
(Node B: ReadyToSwap) ──Governance: SweepAndProcess──▶ (Node B: Awake)
```

1. **Rotation trigger** — Node A completes its configured event batch. Governance checks the round-robin registry for the next node in `ReadyToSwap`.
2. **Control signal** — Governance issues a lightweight payload on `system.control`: `{ Target_Node, State: Awake, Action: SweepAndProcess }`.
3. **Backlog sweep & formatting** — Node B's local wrapper consumes its unread subscriber backlog up to latest event N. Items 1…N−1 are formatted into a read-only `[Historical Context]` prompt section; item N is appended as `[Target Event — Respond Now]`.
4. **Single-pass execution** — Node B processes the payload in one context-aware turn, returns its advice to Governance, and takes over live duty. Node A transitions to `Consolidating`; its uncommitted temp-log entries are appended to Archive in one write, where Node B's next sweep picks them up.

**Why this shape:** zero token waste (no live "warming," no overlapping execution), prompt-boundary enforcement (`[Historical Context]` prevents accidental responses to stale messages), and frictionless scaling (new nodes just subscribe, manage their own sleep, and register in the round-robin list — zero Governance pipeline changes).

**Rotation trigger policy:** deterministic batch-count is the primary trigger. Impulse's `fatigue` vector may *advance* (never delay) a rotation, and Security's situational overrides can veto entering consolidation entirely (e.g. unit is driving / operating a stove). Rotation is silent — no user consent prompt at any N.

**Failure path:** Recovery snapshots the pre-swap state before every rotation. If anything breaks mid-swap, rollback to the snapshot is free; preventing every edge case is not. The temp log stays ephemeral in memory during POC phases — a mid-consolidation crash is *recoverable state loss, not data corruption*.

### 7.3 Degenerate cases: N=1 and N=2

The protocol is N-generic. Small fleets behave as follows:

- **N=1 (prototype):** rotation degrades to a *pause*: the single node finishes its batch, consolidates while Governance holds new Intent-bound events in the subscriber buffer, then sweeps its own backlog and resumes. Zero-downtime is intentionally not available at N=1 — acceptable for a POC where consolidation runs during natural idle periods.
- **N=2:** dual-hemisphere behavior falls out automatically — one node always Awake or Consolidating, the other buffering. First zero-downtime configuration.
- **N=3+:** enables one node per lifecycle state simultaneously, and (optionally) multi-substrate diversity (§7.5).

### 7.4 What consolidation reconciles, and the epoch format

Each consolidation pass reconciles three inputs:

1. Existing personality data (Core Anchors + Evolving Delta, before the batch)
2. The temp log — the node's own live, provisional reactions since the last batch
3. Analytics' delta report — the de-duplicated, digested summary of the full inter-agent queue since the last batch

Output: an **immutable, append-only, source-attributed epoch delta** written to Archive:

```json
{
  "epoch_id": "2026-08-21_node-b_consolidated",
  "source_substrate": "deep-reasoning",
  "source_model": "<resolved from manifest vendor map>",
  "node_id": "node-b",
  "timestamp": "2026-08-21T14:32:00Z",
  "consolidation_cycle": 247,
  "deltas": [
    { "trait": "curiosity_bias", "delta": 0.15, "prior_value": 0.65, "new_value": 0.80,
      "rationale": "High engagement with exploratory prompts; boosted exploratory drive." },
    { "knowledge_entry": "user_feedback_pattern", "action": "archive",
      "tags": ["reflection", "meta-learning"],
      "content": "User rewards exploratory thinking over prescriptive answers." },
    { "drive_recalibration": "urgency", "delta": -0.08,
      "rationale": "No time-critical events this cycle; reduced baseline urgency." }
  ]
}
```

Epochs are **immutable once written**; subsequent consolidations append new deltas rather than modifying prior ones. On waking, a node hydrates by **recency-weighted summarization** of the epoch history into its ~2k-token Evolving Trait Delta — newest deltas weigh heaviest, Core Anchors are never summarized away.

### 7.5 Multi-substrate diversity (Phase 3, N≥3)

Nodes may be assigned **different substrate classes** (§10.2) to introduce genuine reasoning diversity into consolidation. Different substrates consolidate the same experiences differently — terse and pattern-focused, exploratory and hypothesis-driven, orthogonal framings — and because epochs are append-only with source attribution, none overwrites the others. **Persona is synthesis, not consensus:** the identity record becomes a layered tapestry of multiple reasoning styles engaging with the same life.

The substrate behind every event is logged (`source_substrate`, §7.4), so Diagnostic (§12) can trace which model produced which trait shift — without needing to judge which one was "right."

---

## 8. Escalation & Safety Patterns

**Ethical escalation ladder** (inside the live pipeline):

```
Security reds repeat → Governance asks Analytics "pattern?" → Analytics confirms via trend data
→ Governance asks Intent to advise → Intent renders judgment (possibly "that would be unethical")
→ Governance relays it, never originates it
```

**Fault-severity ladder** (when something breaks):

1. **Normal fault** — handled inside the queue (Analytics loop check → graceful degradation). No external component.
2. **Deadlock / silence** — handled by Watchdog (§11), a 5-level escalation from a gentle Impulse nudge up to catastrophic rebuild.
3. **User-initiated** — direct request for rollback or rebuild, no fault required.

---

## 9. Recovery — Deterministic Bootstrapper (not one of the 8)

Recovery is a zero-overhead, deterministic Infrastructure-as-Code bootstrapper — not a generative agent — and **the only service that deploys and bootstraps the ecosystem**. It reads a declarative manifest and deploys/restores it, carrying **zero LLM API dependency during deployment**: even if every model endpoint is offline, Recovery can still construct, wire, and health-check the network, container suite, and storage layers.

Manifest *authoring* (writing `system_instruction` content) is a human activity outside Recovery's runtime scope — the manifest lives in version control; revisions are deliberate commits, not runtime events.

**Triggers:** Governance (catastrophic failure), Watchdog (deadlock escalation), or direct user request.

**Boundary with the live queue:** Recovery does not participate in real business event cycles. Its one narrow exception is injecting synthetic diagnostic pings (`BootCheck`, `SystemCheck`) via Sensory purely to test pass-through liveness — these bypass Action entirely and are structurally distinct from real events.

**Restore behavior:** after a rollback/rebuild, Recovery silently repopulates storage and lets the next Sensory event start clean; no synthetic "restored, resume" event enters the queue.

### 9.1 Bootstrap sequence

1. **Manifest parsing** — read and validate `ecosystem-manifest.yaml`.
2. **Storage init** — create the `/archive/` structure for the current phase.
3. **Provision deterministic tier** — Sensory, Impulse, Security, Action, Archive (mock or real per manifest).
4. **Cognitive hydration** — load system instructions, drive vectors, and Core Anchors into cognitive-tier agents; register Intent nodes in the round-robin list.
5. **Bus binding** — bind message topics; Watchdog begins passively listening for transition silence.
6. **Health check** — inject a synthetic `BootCheck` via Sensory; verify full pass-through to Governance and back. If any role fails, Recovery logs the failure and **stops**. Fix, re-run — deterministic and reproducible.
7. System live.

One `recovery.py` orchestrates all bootstraps. As the ecosystem evolves, only the manifest and Recovery change — agents just listen to topics and respond.

### 9.2 Deployment modes

`deployment_mode: local-process | docker-nested | azure-bicep`

Localhost with outbound HTTPS to the LLM APIs is sufficient for POC work (`local-process` / `docker-nested`) — no inbound traffic needed while Sensory is prompt-only. Move to `azure-bicep` (Container Apps, Service Bus, Blob Storage) when Sensory must *receive* external events or persistence must outlive a single session.

---

## 10. Ecosystem Manifest

The entire topology — infrastructure, storage, bus topics, every agent's runtime config and system instructions — lives in one version-controlled `ecosystem-manifest.yaml`, snapshot-versioned into Archive.

### 10.1 Manifest sketch

```yaml
version: "0.30"
ecosystem_name: "emergent-unit-01"
phase: 0                              # 0=mockup, 1=real N=1, 2=N=2, 3=N>=3 multi-substrate

infrastructure:
  deployment_mode: "local-process"    # local-process | docker-nested | azure-bicep
  network_name: "agent-bus-net"

timers:                               # two independent clocks — see §11.1
  watchdog:
    interval_x_sec: 5.0               # Level 1: Deterministic Ping threshold
    interval_y_sec: 10.0               # Level 2: SystemCheck escalation threshold
    max_check_retries: 2
  impulse:
    idle_musing_interval_sec: 7200    # organic/social quiet-period timer (minutes–hours scale)

storage:
  profile: "json"                     # json | hybrid-sqlite | hybrid-parquet  (phase-coupled, §5.8)
  root: "/data/archive/"

message_bus:
  type: "embedded-pubsub"             # in-memory for POC
  topics: [ events.*, system.diagnostic, system.control ]

substrates:                           # substrate classes → vendor mapping, ONE place (§10.2)
  fast-reflex:     { model: "<vendor-model-id>", notes: "live duty: concise, cheap, low-latency" }
  deep-reasoning:  { model: "<vendor-model-id>", notes: "consolidation: depth over speed" }
  orthogonal:      { model: "<vendor-model-id>", notes: "perspective diversity, different bias patterns" }

roles:
  # Deterministic tier — native code (mock: true in Phase 0)
  sensory:   { tier: deterministic, mock: false }
  impulse:   { tier: deterministic, mock: true,
               initial_vectors: { curiosity: 0.8, fatigue: 0.1, urgency: 0.0, social_drive: 0.5, temperature: 0.4 } }
  security:  { tier: deterministic, mock: true, rules: "security_rules.json" }
  action:    { tier: deterministic, mock: true }
  archive:   { tier: deterministic, mock: true }

  # Cognitive tier — LLM-backed (mock: true in Phase 0)
  governance:
    tier: cognitive
    mock: true
    substrate: "fast-reflex"
    temperature: 0.0
    context_strategy: "per_event_statutory_reset"
    system_instruction: |
      You are GOVERNANCE, the non-thinking backbone and routing engine.
      NEVER generate opinions, personal answers, or explanations.
      Route Sensory + Impulse → Analytics → Intent → Security → Action.
      If Security responds 'Red', return payload to Analytics for revision.
  analytics:
    tier: cognitive
    mock: true
    substrate: "deep-reasoning"
    temperature: 0.2
  intent:
    tier: cognitive
    mock: true
    temperature: 0.7
    nodes:                            # N-generic from day one; Phase 0–1 lists exactly one
      - { id: "node-a", substrate: "fast-reflex" }
      # - { id: "node-b", substrate: "deep-reasoning" }   # Phase 2
      # - { id: "node-c", substrate: "orthogonal" }       # Phase 3
    rotation:
      batch_size_events: 25           # primary rotation trigger (tunable, §16)
```

*(The full manifest carries complete `system_instruction` blocks for Governance, Analytics, and Intent, plus full deterministic-tier config.)*

### 10.2 Substrate classes, not vendor names

The manifest declares **substrate classes** (`fast-reflex` / `deep-reasoning` / `orthogonal`); the class→vendor mapping lives in exactly one manifest table. Swapping vendors, models, or prices is a one-line manifest change with zero code or spec churn — today's model names are tomorrow's legacy code. Epoch records store both `source_substrate` (stable, analytical) and `source_model` (resolved at write time, forensic).

**Observation:** the Haiku substrate performs well at low token consumption and is a good baseline for comparing cost/quality across substrate classes.
---

## 11. Watchdog (not one of the 8)

A passive monitor tracking message-queue transition intervals, running independently of the live agents.

| Level | Condition | Action |
|---|---|---|
| 1 — Deterministic Ping | interval > X (default 5s) | A zero-token liveness ping to Impulse (deterministic, no LLM call). Confirms the bus and deterministic tier are alive before anything more expensive runs. Produces no content and never reaches the queue or the human. |
| 2 — In-band System Check | interval > X+Y (default 15s) | Alerts Recovery, which injects a `SystemCheck` via Sensory; Governance routes it to Analytics, which replies directly to Recovery. Action is bypassed. |
| 3 — Out-of-band ping | Level 2 unanswered | Recovery pings Governance directly, outside the queue, checking for thread death. |
| 4 — Soft rollback | Ping fails | Recovery restores the last clean snapshot, flushes the queue, reissues `SystemCheck`. |
| 5 — Catastrophic rebuild | Post-rollback check still fails | Recovery regenerates blueprints from the manifest, rebuilds the interaction map, resets the ecosystem. |

### 11.1 Two clocks

The system runs **two separate, independently configured timers**, both declared under `timers:` in the manifest — and the two-order-of-magnitude gap between them (seconds vs. hours) is intentional, not an oversight, because they answer different questions:

- `timers.watchdog.*` (seconds-scale) — **"is the machinery still alive?"** A technical crash/deadlock detector, purely internal. It audits; it never animates. None of its five levels produce queue traffic, content, or anything the human perceives — Level 1 specifically targets Impulse (deterministic, zero-token) so the frequent first-tier check costs nothing; only a persisting silence escalates to Level 2, which is the first check to spend tokens on the cognitive tier.
- `timers.impulse.idle_musing_interval_sec` (hours-scale) — **"has it been quiet long enough that the persona should say something unprompted?"** This is Impulse's own province, and the *only* channel through which unstimulated behavior — a story, a question, an observation — enters the queue. Keeping it sole-sourced avoids two systems independently deciding to animate the persona and colliding or spamming the queue.

Same word ("quiet") answering two different questions on two different clocks: is the *system* still running (Watchdog, silent, seconds-scale), and has it been quiet long enough that the *persona* should originate something (Impulse, visible, hours-scale). They must never share a value or a role — if Watchdog's audit ever produced queue content, the two systems could independently decide to animate the persona at the same time, spamming the queue for no value. Co-locating the timers under `timers:` in the manifest keeps the boundary visible.

---

## 12. Diagnostic Agent (not one of the 8)

An out-of-band meta-observer and the human-facing "symbiotic UX" layer — strictly read-only, never injects into the live queue. It queries epochs and message ledgers straight from Archive's cold layer, bypassing live agent memory, and evaluates long-term behavior, value drift, and emotional trajectory free of the prompt constraints the live agents operate under.

**Capabilities:**

- Compares versioned Intent persona epochs over time to map value/preference shifts.
- Reconstructs past reasoning states — why the unit reacted a certain way at a given `Event_ID` — from archived drive vectors and persona states.
- Scans routing logs for bottlenecks, frequent Security flags, or loop patterns.
- Visualizes Impulse's drive-vector history alongside temperature recalibration curves.
- **Substrate analysis** (Phase 3): divergence detection between substrates' trait recalibrations, coherence auditing, influence tracing ("which substrate authored the curiosity spike?"), and substrate-bias profiling.
- Serves as the conversational partner for meta-questions: *"How has your perspective on our work evolved this week?"*

This is the surface where the human sees and discusses the system's growth, separate from talking *to* the system live. Not part of the symbiotic loop (that's the 8 agents + human); Diagnostic is the mirror.

---

## 13. Initial Phase: Mockup & Mimic Implementation

The first buildable iteration. Goal: **validate the queue topology end-to-end before paying for a single LLM reasoning call.**

### 13.1 Configuration: 7 mocks + 1 real (Sensory)

- **Real:** **Sensory** — it's essentially an input field plus source-tagging; there is nothing meaningful to mock. It is also the injection point for every test.
- **Mocks (7):** Governance, Impulse, Analytics, Intent, Security, Action, Archive-as-service. Each mock: listens to its inbound topic, logs the message, responds with hardcoded or templated output matching the protocol envelope (§3). Security's mock always answers `Green`. Intent's mock runs as a single registered node (`node-a`) — the N-node registry exists from day one, it just has one entry.
- **Bootstrap:** Recovery only (§9). `python -m recovery.bootstrap --manifest ecosystem-manifest.yaml` with `phase: 0`. If a role fails its health check, Recovery stops; fix and re-run.

### 13.2 Archive as JSON files on disk

```
/archive/
  /queue/      events_2026-08-22.jsonl       # append-only event log
  /identity/   intent_epochs.json            # epoch deltas (same schema as §7.4)
  /knowledge/  knowledge_store.json
  /working/    temp_log.json, drive_vectors.json
```

Two endpoints, stable across all later storage upgrades: `POST /archive/write` (append), `GET /archive/query` (grep/jq over files). Everything is inspectable with `cat` / `grep` / `jq` — debuggability is the point. The upgrade path JSON → SQLite → Parquet/DuckDB (§5.8) changes nothing above the Archive interface.

### 13.3 End-to-end test: "Hello, are you awake?"

1. Inject the prompt via Sensory (real).
2. Watch it traverse Governance (mock) → Analytics (mock) → Intent (mock) → Security (mock) → Action (mock), with Impulse (mock) firing in parallel at the start.
3. **Verify:** every hop logged in `/archive/queue/`; Impulse vectors present in `/archive/working/`; Action's mock output re-enters via Sensory as an outcome event (proprioception check); no Watchdog escalation triggered (queue stayed active throughout).

**Exit criteria for Phase 0:** the full worked example of §3.2 is reproducible from a cold Recovery bootstrap, twice in a row, with identical queue traces (modulo timestamps).

**Watchdog escalation check (run once, separately):** the test above never stalls long enough to trip anything, which only proves Watchdog stays quiet on the happy path — it doesn't prove escalation works. Level 1 has no observable trace by design (a zero-token ping with no queue footprint, §11.1), so it can't be checked without adding instrumentation out of scope for Phase 0. Level 2 can: hold Sensory idle past X+Y (default 15s) and confirm a `SystemCheck` event appears in `/archive/queue/`, routed to Analytics and replied to Recovery, with Action bypassed. Run this once to confirm the ladder works, not on every re-run of the core loop above.

### 13.4 Replacement sequence — one real agent per cycle

```
Governance → Analytics → Impulse → Intent → Security → Action
```

Each cycle: replace exactly one mock with its real implementation, re-run Recovery, re-run the §13.3 test, confirm the trace still holds. Archive-as-service goes real alongside whichever agent first needs actual queries (typically Analytics). Once all 8 are real, the system enters Phase 1.

---

## 14. Phased Roadmap

| Phase | Intent fleet | Substrates | Storage | Unlocks |
|---|---|---|---|---|
| **0 — Mockup & Mimic** | N=1 (mock) | none (no LLM calls) | JSON + jq | Queue topology validated; Recovery proven as bootstrap backbone |
| **1 — All real, N=1** | N=1 (real) | single class for all cognitive roles | JSON → SQLite hot layer | Real reasoning; consolidation as *pause* (§7.3); first genuine epochs |
| **2 — Zero-downtime** | N=2 | still single class | SQLite hot + Parquet cold, DuckDB | Rotation protocol live; dual-hemisphere behavior falls out of the N-generic design |
| **3 — Multi-substrate** | N=3 | fast-reflex / deep-reasoning / orthogonal | full hybrid | Perspective diversity in consolidation; Diagnostic substrate analysis |

Nothing in Governance, the bus, or the manifest schema changes between phases — only `phase`, the `roles.*.mock` flags, `storage.profile`, and the `intent.nodes` list.

---

## 15. Tunable Defaults

Values the spec treats as configuration, not law. Each is a working default expected to be calibrated against real behavior; changing any of them is a manifest edit, not a spec revision.

| Parameter | Default | Calibrate when |
|---|---|---|
| `rotation.batch_size_events` | 25 | Phase 1 shows real event rhythms |
| `timers.watchdog.interval_x_sec` / `interval_y_sec` | 5.0 / 10.0 | Real transition latencies are measured |
| `timers.impulse.idle_musing_interval_sec` | 7200 | The musing frequency feels wrong in daily use |
| Analytics loop threshold | 3 repeats | Loops are declared too eagerly or too late |
| Analytics working window | 10 events | Trend detection misses or over-fits |
| Impulse seed vectors | `curiosity 0.8, fatigue 0.1, urgency 0.0, social_drive 0.5, temperature 0.4` | The persona's baseline energy needs tuning |
| Working-queue hot cutoff | 3 days | Hot-storage size or query patterns demand it |

---

*End of specification v0.30.*
