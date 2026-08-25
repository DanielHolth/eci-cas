# ECI-spec-v0-33.md

**Version:** 0.33  
**Status:** Stable (Phase 0 mockup complete, ready for Phase 0.1)  
**Last Updated:** 2026-08-22  

---

## Executive Summary

The Emergent Cognitive Identity (ECI) runs on a Continuous Agent System (CAS)—eight cooperating agents on a pub-sub message bus with deterministic, storage-first orchestration.

**Core pipeline (v0.31 established, unchanged):**
```
Sensory + Impulse → Governance → Analytics → Intent → Security → Governance → Action
(sole trigger)     (routes)    (reasons)  (advises) (gates)    (routes)   (executes)
```

**Failure handling (v0.33 simplified):**
```
Action fails → Governance issues Prompt (deterministic fallback, no retries)
```

---

## The Three Revisions

### v0.31: Sensory→Impulse→Governance Strict Relay
**Problem:** Merge buffer was adding state without capability.  
**Solution:** Impulse becomes sole trigger; Governance receives original content + Impulse's reflex as metadata.  
**Result:** Stateless merge, clearer signal flow.

### v0.32: Action Reports Failure to Governance
**Problem:** Action was reporting via Sensory proprioception (feedback loop).  
**Solution:** Action fails → report directly to Governance (not Sensory).  
**Result:** Governance owns failure decisions, no proprioception loop.

### v0.33: Governance's Deterministic Fallback Protocol
**Problem:** v0.32 left failure handling ambiguous (retry? escalate? threshold?).  
**Solution:** Governance has one fallback rule: Action failed? → issue Prompt.  
**Result:** Governance is a router, not a state machine; deterministic fallback.

---

## The Eight Agents

### Sensory (Real, Phase 0)
- **Role:** Convert external input (text prompt, vision, audio) into a structured Envelope
- **Output:** Single Envelope to Impulse with source_type, content, severity (default: Restful)
- **No persistence:** Purely reactive; all state lives downstream

### Impulse (Mock, Phase 0)
- **Role:** Assess internal drive vectors (curiosity, fatigue, urgency, social_drive, temperature) and issue a reflex response
- **Vectors:** Updated per event; default state is calm (urgency=0.0)
- **Severity guardrail:** Capped at Elevated (only Sensory can set Critical)
- **Output:** Single Envelope to Governance with reflex as metadata, triggered_by=sensory
- **Sole trigger:** v0.31 — Governance receives ALL events through Impulse, never directly from Sensory

### Governance (Mock Phase 0, Real Phase 0.1+)
- **Role:** Non-thinking backbone router; no state except transparent event_id correlation
- **Logic:**
  - From Impulse: route to Analytics for evaluation
  - From Intent: route to Security for clearance check
  - From Security (Green): route to Action with proposed action
  - From Security (Red): loop back to Analytics for revision
  - From Action (Failure): issue Prompt action immediately (v0.33, no retries)
- **No memory across events:** Stat context reset per envelope

### Analytics (Mock, Phase 0)
- **Role:** Reason about the event and the internal state; produce a recommendation
- **Input:** Original prompt + Impulse reflex + any revision request from Governance
- **Output:** Recommend a response (content, proposed_action, severity metadata)

### Intent (Mock, Phase 0)
- **Role:** Advise on how to communicate; mirror back confidence and tone
- **Output:** Advice envelope to Governance with proposed_action refined

### Security (Mock, Phase 0)
- **Role:** Gate-keeper; check for safety and consistency
- **Verdicts:** Green (proceed) or Red (block, ask Analytics to revise)

### Action (Mock, Phase 0)
- **Role:** Execute exactly what Governance hands it after Security clearance
- **On success:** Silent (no envelope, no re-entry) — v0.33
- **On failure:** Report directly to Governance with original content
- **No retry logic, no state tracking** — v0.33 simplified

### Archive (Mock, Phase 0)
- **Role:** Append-only storage interface (JSON lines format)
- **Endpoints:** 
  - `POST /archive/write` — append one record
  - `GET /archive/query` — read with predicate filter
- **Tracks:** All envelopes, Impulse vector states, any events
- **No deletion:** Immutable audit log

---

## Message Envelope Protocol

### Structure
```
{
  "event_id": "<UUID>",           # Unique per ingest; propagated by reply()
  "timestamp": "<ISO-8601>",
  "source": "<Agent name>",
  "destination": "<Agent name>",
  "type": "<Message type>",       # e.g. "prompt", "Evaluate", "Advice", "Speech", "Failure"
  "content": "<String>",          # Primary payload
  "severity": "<Severity level>", # Restful, Neutral, Elevated, Critical
  "meta": {<dict>},              # Optional metadata (e.g. reflex, proposed_action)
  "triggered_by": "<Agent name>"  # Optional: trace origin (v0.31)
}
```

### Reply Pattern
- `envelope.reply()` creates a new envelope with same event_id, incremented hop count
- Severity is OR-upscale-only: once raised, never lowered along the chain

### Severity Scale
- **Restful** — calm, low urgency (default for routine prompts)
- **Neutral** — normal processing
- **Elevated** — high urgency or concern (Impulse max via vectors)
- **Critical** — danger/emergency (only Sensory can set)

---

## The Main Pipeline (Happy Path)

```
1. Sensory ingests prompt
   └→ Envelope: {source: Sensory, dest: Impulse, type: prompt, severity: Restful}

2. Impulse assesses vectors, reflects
   └→ Envelope: {source: Impulse, dest: Governance, type: prompt, 
                  meta: {reflex: "...", triggered_by: Sensory}}

3. Governance routes to Analytics
   └→ Envelope: {source: Governance, dest: Analytics, type: Evaluate,
                  content: "Evaluate intent based on prompt and reflex"}

4. Analytics reasons
   └→ Envelope: {source: Analytics, dest: Intent, type: Recommend}

5. Intent advises
   └→ Envelope: {source: Intent, dest: Governance, type: Advise,
                  meta: {proposed_action: "..."}}

6. Governance routes to Security
   └→ Envelope: {source: Governance, dest: Security, type: Clear,
                  meta: {proposed_action: "..."}}

7. Security gates (Green assumed)
   └→ Envelope: {source: Security, dest: Governance, type: Verdict, content: Green}

8. Governance routes to Action
   └→ Envelope: {source: Governance, dest: Action, type: Speech,
                  content: proposed_action}

9. Action executes, succeeds
   └→ (silent—no envelope, per v0.33)

Total hops logged to Archive: 8
```

---

## Failure Handling (v0.33)

```
[After step 8 above, Action executes but fails]

9. Action reports failure to Governance
   └→ Envelope: {source: Action, dest: Governance, type: Failure,
                  content: original_request}

10. Governance deterministic fallback: issue Prompt
    └→ Envelope: {source: Governance, dest: Action, type: Prompt,
                   content: "The previous action failed. Explain to the human..."}

11. Action executes Prompt
    └→ (silent on success)

Total hops logged: 10 (v0.32 was 14 with retries + Analytics escalation)
```

**Key points (v0.33):**
- No retry loop in Governance
- No consecutive_failures counter in Action
- No Analytics escalation
- No threshold logic
- Governance is a router, not a state machine

---

## Watchdog (System Monitor)

### Level 1 (Baseline)
- Transition interval threshold: 5.0 seconds
- If any agent silent > 5s, log warning (no action)

### Level 2 (Escalation)
- Transition interval threshold: 15.0 seconds
- If silent > 15s:
  1. Recovery publishes SystemCheck to Governance
  2. Governance routes to Analytics
  3. Analytics replies SystemCheckAck to Recovery
  4. **Action is bypassed** (diagnostic only)

### Implementation
- Passive monitoring (no state mutations)
- Triggers Recovery diagnostic events (system.diagnostic topic)
- Reports level, timestamp, and last observed transition

---

## Severity Propagation Rules

1. **OR-upscale-only:** Any agent may raise severity; none may lower once set upstream
2. **Impulse cap:** Internal drive vectors alone max at Elevated (v0.31)
3. **Sensory override:** Only Sensory can set Critical
4. **Transparent propagation:** Once set by Impulse, severity unchanged through all subsequent hops

---

## Storage Interface (Archive)

### Endpoints
- **POST /archive/write** — append one JSON record
- **GET /archive/query** — read with optional predicate filter

### What's stored
- All envelopes (one record per hop)
- Impulse vector snapshots
- Watchdog check results
- Any agent-specific diagnostic records

### Format
- JSON Lines (one JSON object per line, no outer array)
- Immutable (append-only)
- No deletion

---

## Recovery Bootstrap

- **Sole deployment mechanism:** Infrastructure-as-Code via manifest.yaml
- **Deterministic:** Same manifest → identical topology every time
- **Health check (BootCheck):** Recovery verifies Governance responds before declaring "live"
- **Phase 0:** 7 mocks + 1 real (Sensory)
- **Phase 0.1:** 6 mocks + 2 real (Sensory, Governance)

---

## Summary of Changes (v0.30 → v0.33)

| Aspect | v0.30 | v0.33 |
|--------|-------|-------|
| **Sensory→Impulse** | Parallel inputs to merge buffer | Strict relay (Impulse sole trigger) |
| **Merge state** | Governance tracked merge | None (Impulse only) |
| **Action failure path** | Sensory proprioception loop | Direct to Governance |
| **Failure handling** | Retry + escalate | Deterministic Prompt fallback |
| **Governance state** | Merge buffer, retry count | None (pure router) |
| **Severity guardrails** | Basic | OR-upscale-only, Impulse capped |
| **Watchdog** | Level 1 only | Level 1 + 2 |
| **Happy path hops** | 8 | 8 (unchanged) |
| **Failure hops** | N/A | 10 (no retries) |
| **Spec lines** | 555 | ~603 (growth is revision detail, not bloat) |

---

## What Didn't Change

- Bus architecture (embedded pub-sub)
- Agent count (8 agents)
- Pipeline topology (Sensory → ... → Action)
- Storage interface (Archive)
- Recovery bootstrap design
- Severity scale concept
- Watchdog monitoring levels

---

## Phase 0.1 Readiness

After v0.33:

✅ Architecture is clean (router, not state machine)  
✅ Code is simple (47 fewer lines than v0.32)  
✅ Failure handling is deterministic (Prompt fallback)  
✅ Governance structure is identical for real implementation  
✅ All 13/13 tests pass  
✅ Ready to replace Governance mock with real Claude agent  

---

## Next Steps

**Phase 0.1:**
1. Replace GovernanceMock with real LLM-backed Governance
2. Use Fable for Phase 0.1 implementation (architectural complexity)
3. Use Haiku inside Governance's system prompt (inference model)
4. Observe queue traces; refine Governance reasoning

**Phase 0.2+:**
1. Replace Analytics, Intent, Security with real agents
2. Same structure, higher reasoning quality
3. Flat-cost, always-on operation via CAS

---

*v0.33: Complete, coherent, ready to ship.*
