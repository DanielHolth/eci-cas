# Dispatch Notes

Running capture log of planning items dictated in transit. This is raw intake, not specification — items graduate into the relevant `docs/` revision (see `roadmap-to-v1.md`) once refined.

## Open items

### 1. Frontend should mirror the console workflow — *moved to roadmap M5*

### 2. Qwen 1.5B via MLC LLM for the free (minimal) tier

Begin parallel testing using the Qwen 1.5B model with MLC LLM as the substrate for the free/minimal tier.

Per the substrate-agnostic principle, treat this as one concrete instance of a minimal-tier substrate class, not a hardcoded vendor dependency. What matters is the capability floor the tier must tolerate.

### 3. Plain text LLM output — *done (Phase 0.8)*

All cognitive agents (except Consolidator) now return plain text instead
of JSON. Code handles all structure deterministically.
