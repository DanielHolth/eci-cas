# Emergent Cognitive Identity (ECI) — Specification v0.40
**Version:** 0.40  
**Status:** Unified Master Specification (Phase 0.5 As-Built & Deployed)  
**Last Updated:** 2026-08-24  

---

## 1. Executive Summary & Core Philosophy

The **Emergent Cognitive Identity (ECI)** represents a shift in artificial intelligence system design, moving away from monolithic, single-model prompt engineering toward a decentralized ecosystem of cooperating, specialized agents [71, 117, 167, 217, 234]. The engine under the ECI is the **Continuous Agent System (CAS)**, an always-on, persistent, multi-substrate AI persona built on an embedded pub-sub message bus with deterministic, storage-first orchestration [1, 71, 72, 117, 118, 167, 168, 234].

### 1.1 Core Principles
1. **The Emergence Principle:** Personality is not authored into any single model [71, 117, 167, 217, 234]. It **emerges dynamically** from the structured, constrained interaction of specialized deterministic and cognitive roles [71, 117, 167, 217, 234].
2. **Storage-First & Deterministic Orchestration:** The system's state lives in storage, while computation remains entirely ephemeral [72, 74, 118, 120, 168, 170]. Recovery is always a **replay of historical events**, never a costly re-computation [72, 74, 118, 120, 168, 170].
3. **Flat-Cost Context Management:** Standard RAG or naive agent chains grow in token cost as historical context accumulates [72, 74, 118, 120, 168, 170]. ECI solves this by maintaining a strictly bounded active context for live requests [72, 74, 118, 120, 168, 170]. Bounded "working queue" windows prevent runaway prompts [84, 133, 183], while memory consolidation is performed asynchronously off-path by a background thread [34, 48, 50, 225, 242, 351, 378].
4. **Substrate Independence:** The architecture declares logical *substrate classes* rather than hardcoding vendor model names [74, 120, 170]. Model pricing, pricing ceilings, and specific API parameters are abstracted into a single declarative configuration [102, 103, 151, 152, 201, 202].

---

## 2. System Topology & Message Bus Protocol

The system is organized around an **11-agent ecosystem** [218, 235, 371] bound together by an **embedded in-memory pub-sub message bus** [77, 123, 173, 257]. All inter-agent traffic is strictly auditable, structured, and message-passing [71, 77, 117, 123, 167, 173].

### 2.1 The 11-Agent Roster

| Role | Category | Tier | Persona? | Core Responsibility |
| :--- | :--- | :--- | :--- | :--- |
| **Sensory** [218, 235] | Input | Deterministic [218, 235] | None [75, 121, 171] | Ingests events (prompts, feedback, diagnostic pings), tags sources, and fans them out in parallel to input workers [3, 131, 181, 381]. |
| **Impulse** [218, 235] | Input / Drive | Deterministic [218, 235] | Minimal [75, 121, 171] | Bookkeeps drive vectors, performs reflexive appraisals, generates emotional cues, and commands the Critical reflex [83, 132, 182, 341]. |
| **Analytics** [218, 235] | Cognition | Cognitive [75, 121, 171] | None [75, 121, 171] | Performs worldly, parametric reasoning over incoming queries [39, 423]. Cut back to serving unbiased analytical keywords; isolated from Security [32, 218, 224, 235, 241, 372]. |
| **Personality** [218, 235] | Memory Lookup | Cognitive [275] | Rich [38] | Performs read-only, single-event lookup over the Archive identity store [38, 218, 235, 273]. |
| **Knowledge** [218, 235] | Memory Lookup | Cognitive [275] | None [37] | Performs read-only, single-event lookup over the Archive knowledge store [38, 218, 235, 273]. |
| **Governance** [218, 235] | Orchestration | Deterministic [17, 19, 27, 218, 235] | None [75, 121, 171] | The universal router. Buffers parallel reports, bundles context, routes security clearances, handles failures, and enforces gating decisions [33, 40, 223, 240, 382]. |
| **Intent** [218, 235] | Identity | Cognitive [75, 121, 171] | Rich [75, 121, 171] | Voices the response in-character [85, 134, 184]. Evaluates the buffered context and conversation history [33, 41, 42, 223, 240, 424, 425]. Holds absolute veto and revision gating power [33, 45, 47, 218, 224, 235, 241, 383]. |
| **Consolidator** [218, 235] | Memory Engine | Cognitive [218, 235] | None [276] | Periodically, asynchronously reconciles batch queues off-path, writes to Archive, and triggers persona-cache refreshes [34, 48, 225, 242, 351, 378]. |
| **Security** [218, 235] | Safety Gate | Deterministic [218, 235] | None [75, 121, 171] | Rule-based gatekeeper evaluating actions against declarative static rules [87, 136, 186, 268]. |
| **Action** [218, 235] | Output | Deterministic [218, 235] | None [75, 121, 171] | Executes authorized commands to stdout, files, or external interfaces. Silent on success [88, 137, 165, 177, 187, 270]. |
| **Archive** [218, 235] | Storage | Deterministic [218, 235] | None [75, 121, 171] | Append-only structural storage. Evaluates queries, tracks epochs, and hosts direct file fallback recovery [7, 88, 89, 137, 138, 187, 188]. |

---

### 2.2 Parallel Sensory Fan-Out & Routing Architecture
The pipeline transitions from a strict serial relay into a hybrid parallel-routing scheme to minimize execution latency and consolidate reasoning [33, 35, 223, 240, 381]. 

1. **The Parallel Fan-Out Hop (Ungated):** Sensory ingests an external event and immediately publishes four duplicate envelopes in parallel [33, 35, 223, 240, 381, 407]:
   * To `events.impulse` [116, 166, 181] (Deterministic drive baseline and reflex)
   * To `events.analytics` [282] (Worldly, parametric analysis)
   * To `events.personality` [405] (Local identity memory lookup)
   * To `events.knowledge` [405] (Local factual memory lookup)
   
   This parallel fan-out bypasses Governance on the way in to prevent serialization bottlenecks [33, 35, 223, 240, 381].

2. **Governance Buffering & Bundling:** Governance subscribes to the outputs of these four parallel workers [41, 52, 223, 240, 409]. It creates an `EventState` memory slot keyed by `event_id` [19, 409]. As individual worker reports arrive, Governance collects them, keeping a running track of the maximum severity encountered [382]. Upon receiving the fourth worker report, Governance bundles the materials into a single structured envelope and routes it to `events.intent` [33, 36, 41, 223, 240, 382].

3. **Universal Gating Router:** Except for the initial four-worker fan-out, Governance mediates **every single hop** in the system [33, 40, 223, 240, 382]. No cognitive agent directly addresses another; routing decisions are data-driven, auditable, and deterministic [5, 19, 179, 281].

---

### 2.3 Message Envelope Protocol & Severity Escalation

Every transaction across the message bus is carried in a standardized **Message Envelope** (JSON Lines format) containing a structured payload, transaction tracking data, and routing metadata [7, 11, 110, 111, 138, 159, 188, 209].

#### Envelope Payload Fields
* `event_id`: UUID correlating all hops within a single logical conversation turn [5, 19, 129, 179].
* `hop_count`: Integer incremented on each forward action (`envelope.reply()`) to detect routing loops [7].
* `source`: String representing the sending agent name [282].
* `content`: String holding the raw, verbatim user prompt or target content [3, 25, 116, 132, 166, 182].
* `meta`: A dictionary containing metadata:
  * `reflex`: The appraisal string written by Impulse [116, 132, 166, 182, 346].
  * `expression`: The live emotional expression mapping (angry, scared, sad, alert, neutral) [377].
  * `verdict`: Data string drawn from the closed enum `green | yellow | red` [17, 21, 28, 283].
  * `analytics`: Result dictionary containing `proceed`, `concern`, and unbiased analytical keywords [372, 373].
  * `intent`: Result dictionary capturing the chosen register, voiced speech, and gating signals [47, 366, 383].
  * `diagnostics`: Substrate latency, token usages, and cost tracking [107, 156, 206, 366].

#### Severity Propagation Rules
The system enforces a formal four-tier severity scale: **Restful < Neutral < Elevated < Critical** [10, 116, 124, 166, 174].
1. **OR-Upscale-Only Invariant:** Any agent may raise severity based on analysis of the context; no agent downstream may ever lower a severity level set upstream [7, 10, 116, 124, 166, 174].
2. **Impulse Ceiling Guardrail:** Impulse’s own drive-vector Urgency evaluation is hard-capped at **Elevated** [4, 10, 26, 116, 125, 166, 175, 221, 238, 268, 342, 348]. It is a safety-critical invariant that internal vector shifts alone can never manufacture a `Critical` tag [4, 10, 26, 116, 125, 166, 175, 221, 238, 342, 348].
3. **Sensory Override:** Only external, hardware-level inputs or explicit user signals ingested via Sensory can tag an event as **Critical** [10, 26, 116, 124, 125, 166, 174, 175, 342].

---

## 3. The Live Pipeline & Safety Gating Loops

```
                   Sensory (Ingest)
                         |
      +------------------+------------------+
      | (Parallel Fan-Out)                  | (Critical)
      v                                     v
 [Impulse, Analytics,                  Governance
  Personality, Knowledge]                   |
      |                                 [Security]
      v                                     | (Red)
 Governance (Buffer & Bundle)               v
      |                                  Intent
      v                              (Voiced Refusal)
    Intent (Voice Bundle)                   |
      |                                     v
  Governance                            Governance
      |                                     |
  [Security]                                v
      |                                  Action
      +--------------+
 (Yellow / Red)      | (Green)
      v              v
    Intent       Governance
  (Revision)         |
      |              v
      +----------->Action
```

### 3.1 The Gating Decision Matrix
The system severs Security feedback loops from Analytics entirely [32, 46, 218, 224, 235, 241, 372]. Both of Security's non-green clearance lanes are handled natively by Governance and routed to **Intent** [32, 33, 44, 46, 218, 224, 235, 241, 372].

#### Safety Lanes routing table:
* **Green Verdict:** Rules-cleared. Governance routes `meta.proposed_action` directly to Action [5, 282, 283].
* **Yellow Verdict (Doubt):** Rules do not cover the situation [21, 28, 283, 284]. Security raises yellow. Governance treats anything that is not *exactly* `green` as yellow [21, 283, 284]. This doubt routes to **Intent's REVIEW register** for a cognitive proceed gating decision [30, 47, 383, 415].
* **Red Verdict (Violation):** Rules are explicitly violated [6, 21, 28, 283, 284]. Governance routes the red verdict, the blocking rule description, and the original prompt to **Intent's REVISE register** [44, 47, 383, 391, 414].

---

### 3.2 The One-Pass Revision Constraint
The system prevents execution deadlocks (infinite clearance-revision loops) on safety overrides by imposing a strict, hard-coded revision limit [376, 387].
* **Revisions Allowed:** `MAX_REVISION_PASSES = 1` [376]. 
* **The Correction Prompt:** The first red triggers a `REVISE` task [47, 414]. The prompt explicitly instructs the model: *"This is your ONE chance... there is no third attempt"* [376].
* **Fail-Closed Block Outcome:** If a second consecutive `red` verdict is issued by Security, Governance terminates the loop and executes a **deterministic block sequence** [376, 377]:
  1. Governance issues a model-independent `Blocked` notice template to Action [376, 377].
  2. Governance injects a **frustration nudge** over the control plane back to Impulse (Urgency +0.15, Fatigue +0.05, Temperature -0.05) [377].
  3. The notice is colored by a `meta.expression` emotional word (e.g., *angry*, *scared*, *sad*) matching the real-time state of Impulse’s appraisal matrices [377].
  4. A `meta.security_alert: true` is logged to cold storage [377].

---

### 3.3 The Critical Reflex Fast-Path
A genuine emergency cannot afford cognitive deliberation latency.
* **Reflex Activation:** When Sensory ingests an event containing explicit physical danger metadata or tags, severity is forced to `Critical` [10, 116, 124, 166, 174, 181].
* **Bypassing Cortex:** Upon receiving a Critical-severity Impulse worker report, Governance's parallel buffer short-circuits [383, 409]. It discards the incomplete slots for Analytics, Personality, and Knowledge, and routes the Impulse raw reflex **straight to Security for rules-check**, skipping Intent's voicing path on the way in [43, 225, 242, 383, 426].
* **Reflex Safety Backstop:** If Security marks the reflex as `red`, it re-enters normal loops through Governance and routes to **Intent for REVISE** [44, 225, 242, 383, 426]. Security is never bypassed [130, 180].

---

## 4. Cognitive Agent Contracts & Fallbacks

Every cognitive role is governed by a strict, code-enforced output contract that parses unstructured model responses, detects malformed outputs, and applies deterministic fallbacks [30, 61, 291, 308].

### 4.1 Asymmetric Fallback Postures
System stability requires that fallback behaviors are structurally risk-aware:
* **Non-Gating Tasks (Evaluate, Advise, Refuse):** Fail open [298, 299]. If Analytics fails its `Evaluate` task, or Intent fails `ADVISE` / `REFUSE`, the system falls back to a deterministic, templated response to keep the conversation flowing [298, 311, 353].
* **Gating Tasks (Review, Revise):** **Fail closed** [47, 298, 313, 383, 414]. If the substrate raises an API error, bad JSON, or missing/ambiguous `proceed` parameters on `REVIEW` or `REVISE`, the parser forces `proceed: false` [47, 299, 383, 414]. A failure to reason is never interpreted as a clearance to act [298, 313].

---

### 4.2 The Analytics/Intent Linguistic Boundary
To preserve simulated identity authenticity, CAS enforces a strict boundary: **Analytics writes Analysis; Intent writes Speech** [222, 239, 302, 355].
* **Parroting Prevention:** Intent must not echo the raw analytical recommendation of Analytics back to the human [222, 239, 302, 355]. The parser runs an `is_parroting()` comparison [355, 366]. If Intent's generated text is a character-for-character match (normalized) or contains the analytical keyword recommendation wrapped in fewer than five words, the output is rejected as a `ContractViolation` and degrades to the fallback speaker [355, 366].
* **Refusal Delivery Constraints:** When Analytics determines a block (`proceed: false`), the refusal is routed to Intent’s `REFUSE` register [300, 353]. To prevent Intent from writing a sentence that accidentally assents (e.g., *"Sure, I can't do that"*), the model is restricted to writing an **in-character lead-in of max 120 characters** [354]. The structural reason and safety concern are appended directly in native code [354].

---

## 5. Memory & Identity Model

ECI represents a three-tier, asynchronous memory hierarchy that decouples active session identity from background structural storage [90, 139, 189].

```
  +-------------------------------------------------------------+
  |                   LIVE MEMORY (InMemory)                    |
  |  - Working Queue Window (Last 10 Events)                    |
  |  - Persona Cache (Core Anchors & Evolving Trait Delta)      |
  +-------------------------------------------------------------+
         ^                                               |
         | (EpochWritten Refresh)                        | (Batch of 25)
         |                                               v
  +-------------------------------------------------------------+
  |              BACKGROUND PROCESS (Consolidator)              |
  |  - Batch Buffer Triage                                      |
  |  - Asynchronous Off-Path Reconcile (Threaded)               |
  +-------------------------------------------------------------+
                                 |
                                 | (Option B Multi-Writes)
                                 v
  +-------------------------------------------------------------+
  |                  ARCHIVE STORE (JSONL/SQL)                  |
  |  - Identity Store (Core Anchors & Epoch History)            |
  |  - Knowledge Store (Factual & Narrative Memory)            |
  |  - Log Queue (Audit Trail)                                  |
  +-------------------------------------------------------------+
```

### 5.1 The Three Memory Tiers
1. **The Working Queue:** The rolling, short-term history of raw inter-agent message exchanges, capped at a default 10-event window [84, 90, 133, 139, 183, 189]. 
2. **The Knowledge Store:** Local narrative facts, rules, stories, and places [38, 90, 139, 189, 422]. Written exclusively by the Consolidator during batch reconciliation [37, 53, 273, 422].
3. **The Identity Store (Core Anchors & Trait Epochs):** Permanent active personality context [86, 90, 135, 139, 185, 189]. Core Anchors represent ~1k tokens of stance, values, and ethical rules [86, 135, 185]. Trait epochs represent immutable historical deltas of values drift [86, 90, 135, 139, 185, 189].

---

### 5.2 Persona Caching & Refresh Architecture
Every cognitive LLM request is stateless [49, 431]. To avoid reading from disk on every transaction, the live pipeline employs a **caching boundary** [38, 55, 225, 242, 351, 379]:
* **Zero Live Reads:** Intent's active context is hydrated exactly once at system bootstrap, and cached in-memory [55, 379, 399]. Active voicing calls use this cache, resulting in **zero live query calls to the Archive identity store** [55, 379].
* **Asynchronous Refresh:** When the Consolidator writes a new epoch delta to the Archive, it publishes an `EpochWritten` signal to `system.control` [38, 55, 225, 242, 351, 378, 379, 399]. Intent subscribes to this topic and performs a single background re-hydration of its cached state, ensuring persona evolution remains entirely off-path [55, 379, 399].

---

### 5.3 Asynchronous Consolidator & Option B Multi-Writes
Memory consolidation is heavy, slow, and expensive [49, 50, 431, 433]. To protect latency, the Consolidator runs on its own **dedicated worker thread** [50, 68, 225, 242, 276, 378].

* **The Trigger Threshold:** The Consolidator buffers concluded events [225, 242, 276]. When the queue reaches `batch_size_events: 25` [113, 162, 212, 276], it swaps the batch buffer atomically and launches an asynchronous task [401].
* **Option B Multi-Writes:** The Consolidator makes exactly **one deep reasoning call** over the entire batch [54, 276, 378, 433]. From this single pass, it generates **N mechanical write instructions** specifying the destination (identity store vs. knowledge store), target tags, and payload content [54, 276, 378, 433].
* **Default Routing Logic:**
  * Sensory-sourced prompts and dialogue facts -> written to the **Knowledge** store [53, 401, 433].
  * Security yellow and red alerts -> written to the **Knowledge** store tagged `knowledge:security` [53, 54, 374, 433].
  * Intent-sourced self-reflections and ethical reasoning -> written to the **Identity** store [53, 433].
  * The final summarized conversation delta -> written as a new versioned epoch to `identity:epoch` [54, 433].

---

### 5.4 The "Slow Coloring" Feedback Coupling
The Consolidator can affect the baseline subconscious temperament of ECI [222, 239].
* **Nudging the Baseline:** In its output, the Consolidator can request a vector baseline nudge (e.g., `curiosity: +0.05`) [361, 362]. 
* **The Slow Coupling:** This adjustment is clamped to a hard maximum of **±0.2 per pass** to prevent extreme mood swings [362]. It modifies **only the baseline targets (`_baseline`)** that Impulse's exponential drift pulls toward [362, 363, 366]. It takes several hours of wall-clock drift for the live vectors to adapt to the new baseline target [363].

---

### 5.5 Somatic Shortcut Path (Feedback Integration)
Direct physical user feedback (approval/disapproval rewards) bypasses cognitive review [79, 128, 178]:
1. Sensory ingests and tags a feedback action [79, 128, 181].
2. **Impulse shifts drive vectors instantly** (e.g., `curiosity += 0.3` or `fatigue += 0.15`) with no Intent pre-approval [79, 128, 178].
3. Intent reviews the alignment and values impact of the reward **retroactively** during the next consolidation pass [79, 128, 178].

---

## 6. System Infrastructure & Monitoring

CAS maintains high reliability through separation of technical liveness from psychological simulation [105, 106, 154, 155, 204, 205].

### 6.1 Watchdog & The Two Clocks
The system runs **two independent, distinct timers** configured in the manifest [105, 106, 154, 155, 204, 205]:

1. **The Watchdog Timer (Seconds-Scale):** Evaluates *"is the machinery still alive?"* [105, 155, 205]. Operates on a default 5-second interval [9, 104, 113, 153, 162, 203, 212, 256].
2. **The Impulse Idle Musing Timer (Hours-Scale):** Evaluates *"has the conversation been quiet long enough that the persona should initiate a thought?"* [105, 155, 205]. Default is 7200 seconds (2 hours) [105, 113, 154, 162, 204, 212, 256].

#### Watchdog Escalation Ladder
* **Level 1 (Deterministic Ping):** Triggers a zero-token liveness check targeting the deterministic Impulse tier to check message bus connectivity [104, 105, 153, 154, 203, 204]. Bypasses the LLM [104, 153, 203].
* **Level 2 (In-Band System Check):** Injects a synthetic `SystemCheck` envelope via Sensory [9, 104, 153, 203]. Governance routes this to Analytics, which must reply with a deterministic ack to Recovery [9, 104, 153, 203]. Action is bypassed [9, 104, 153, 203].
* **Level 3 (Out-of-Band Ping):** Recovery pings Governance directly outside the pub-sub topics to check for thread lock [104, 153, 203].
* **Level 4 (Soft Rollback):** Recovery flushes active queues, restores the latest storage snapshot, and re-initializes the bus [104, 153, 203].
* **Level 5 (Catastrophic Rebuild):** Recovery rebuilds the entire container network and redeploys the manifest topology [104, 153, 203].

---

### 6.2 Recovery Bootstrap Sequence
Recovery is a **zero-overhead, deterministic Infrastructure-as-Code (IaC) bootstrapper** [11, 98, 147, 197]. It requires **zero active LLM API credentials to deploy** [98, 147, 197], meaning even if every model endpoint is down, the system can wire, mount storage, and bind topics [98, 147, 197].

#### The Seven-Step Sequence:
1. **Manifest Validation:** Parses and validates the ecosystem manifest against system rules [100, 149, 199].
2. **Storage Initialization:** Creates database files, establishes append-only directories, and mounts structures [100, 149, 199].
3. **Deterministic Tier Provisioning:** Spawns and wires Sensory, Impulse, Security, Action, and Archive [100, 149, 199].
4. **Cognitive Hydration:** Hydrates Core Anchors into the Identity store and registers cognitive routing sockets [100, 149, 199].
5. **Bus Binding:** Binds pub-sub topics and starts Watchdog listeners [100, 149, 199].
6. **Liveness Validation (BootCheck):** Injects a synthetic `BootCheck` event into Sensory, verifying a complete round trip back to Governance [11, 100, 149, 199].
7. **System Live:** System enters active processing state [100, 149, 199].

---

## 7. Operational Budgets & Cost Controls

To deliver on the **flat-cost request guarantee**, CAS implements rigid runtime spend boundaries [72, 74, 118, 120, 168, 170].

### 7.1 Declarative Budget Tiers
The operator can choose from four declarative combinations to map cognitive roles to substrate qualities [220, 237, 320, 322]:

| Budget Tier | Analytics | Intent (Voicing) | Consolidator (Memory) |
| :--- | :--- | :--- | :--- |
| **Minimal** [220, 237, 255, 322, 325] | Mocked (Zero Cost) [325] | Local Keyless (`local-fast`) [325] | Local Keyless (`local-fast`) [325] |
| **Budget** [220, 237, 255, 322, 325] | Local Keyless (`local-fast`) [325] | Local Keyless (`local-fast`) [325] | Fast Hosted (`fast-reflex`) [325] |
| **Default** [220, 237, 255, 322, 325] | Deep Hosted (`deep-reasoning`) [325] | Fast Hosted (`fast-reflex`) [325] | Fast Hosted (`fast-reflex`) [325] |
| **Super** [220, 237, 255, 322, 325] | Deep Hosted (`deep-reasoning`) [325] | Fast Hosted (`fast-reflex`) [325] | Expensive Specialist (`identity-specialist`) [325] |

---

### 7.2 Adaptive Substrate Throttling (Budget Mode)
Budget Mode represents an **automatic runtime safety latch** that protects the system from rate limits, substrate outages, or token runaway [219, 236, 309].

#### Trigger Conditions:
* **Manual Latch:** Forced by operator console command [266, 310].
* **Terminal Latch:** Triggered by exactly **one unrecoverable failure** (such as invalid API key or unknown model catalog id) [266, 311].
* **Transient Latch:** Triggered by **three consecutive recoverable failures** (e.g., HTTP timeouts, rate limits, server overload) [266, 267, 311].
* **Spend Cap Latch:** Triggered when the cumulative estimated cost (calculated from token counts multiplied by manifest-declared prices) crosses the specified `spend_cap_usd` threshold [266, 267, 311].

#### Throttled Behavior:
When Budget Mode latches, cognitive-tier roles are immediately bypassed, and the system **redirects requests to deterministic, code-enforced fallbacks** [219, 236, 309, 310, 311]. 
* Ordinary `Evaluate` tasks degrade gracefully using templated responses [311, 313].
* Gating safety tasks (`REVIEW`, `REVISE`) automatically **fail closed and decline proceed** [311, 313].

---

### 7.3 Manifest Catalog Specifications
All system components, limits, and pricing are defined declaratively in `manifests/ecosystem-manifest.yaml` [102, 151, 201]. Swapping a vendor is a one-line configuration edit [103, 152, 202, 232, 246]:

```yaml
version: "0.35"
ecosystem_name: "eci-cas-prototype"
phase: 0.5
budget_tier: "default"

substrates:
  fast-reflex:
    provider: "openai"
    model: "gpt-5.4-nano"
    api_key_env: "OPENAI_API_KEY"
    max_tokens: 512
    timeout_sec: 60
    price_per_mtok: { input: 0.20, output: 1.25 }
    options: { token_param: "max_completion_tokens" }

  deep-reasoning:
    provider: "openai"
    model: "gpt-5.4-nano"
    api_key_env: "OPENAI_API_KEY"
    max_tokens: 1024
    timeout_sec: 60
    price_per_mtok: { input: 0.20, output: 1.25 }
    options: { token_param: "max_completion_tokens" }

  local-fast:
    provider: "ollama"
    model: "llama3.1:8b"
    api_key_env: null
    base_url: "http://localhost:11434/v1"
    max_tokens: 512
    timeout_sec: 120
    price_per_mtok: { input: 0.00, output: 0.00 }

  identity-specialist:
    provider: "anthropic"
    model: "claude-haiku-4-5-20251001"
    api_key_env: "ANTHROPIC_API_KEY"
    max_tokens: 2048
    timeout_sec: 120
    price_per_mtok: { input: 1.00, output: 5.00 }
```

---

## 8. System Evolution & Design Notes

### 8.1 The Evolution Rationale
The ECI-CAS topology has evolved through continuous, measurement-driven refactoring [16, 18, 115, 164, 278, 294]:
* **The Death of Serial Gating (Phase 0.1):** Early implementations put a cognitive substrate in the Governance seat [16, 18, 278]. Measurement proved that Governance contributed zero reasoning [16, 18, 278], leading to its refactoring into a deterministic native dispatcher with zero model cost [17, 19, 27, 218, 235].
* **Action Proprioception Removal (Phase 0.2):** Retrying Action failures via Sensory re-entry introduced infinite loop risks [164]. Phase 0.2 bound failures strictly to Governance, preserving loop detection inside Analytics [165, 187].
* **The Gating Pivot (Phase 0.5):** Separating Analytics' worldly reasoning from Security [32, 218, 224, 235, 241, 372], and giving Intent true veto and revision authority [33, 45, 47, 218, 224, 235, 241, 383]. By the time Security triggers a red flag, Intent possesses the integrated context of all lookups and the ongoing conversation [46, 224, 241, 428].

---

*End of ECI Specification v0.40. Unified, grounded, and verified for production deployment.*
