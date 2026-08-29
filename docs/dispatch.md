# Dispatch Notes

Running capture log of planning items dictated in transit. This is raw intake, not specification — items graduate into `current-spec.md` or `roadmap.md` once refined.

## Open items

### 1. Frontend should mirror the console workflow — *moved to roadmap M5*

### 2. Qwen 1.5B via MLC LLM for the free (minimal) tier

Begin parallel testing using the Qwen 1.5B model with MLC LLM as the substrate for the free/minimal tier.

Per the substrate-agnostic principle, treat this as one concrete instance of a minimal-tier substrate class, not a hardcoded vendor dependency. What matters is the capability floor the tier must tolerate.

### 3. Plain text LLM output — *done (Phase 0.8)*

All cognitive agents (except Consolidator) now return plain text instead
of JSON. Code handles all structure deterministically.

### 4. reflection agent and domain into archive — *done (2026-08-29, see docs/roadmap.md)*

some ideas for dispatch.md: Im thinking to add domain to the archive. this would be a new datamodel: domain/category/topic/subtopic/subject/key = value (in add
not sure what domain should hold. im thinking of somethink as basic as... internal, external. thus everything consolidator currently writes is from external sources. but if it wanted to learn from its own reflections and evolve, that would be internal.
i.e. after N events, we feed both input to intent and output from intent for the N events  + related topics from archive to both reflect and "sleep on it" to uncover some spark of genious to log as internal knowledge records.
If the reflection triggest an idea --> ping sensory with source "Idea" and let it run through as a normal event.
Please dump the refined version into dispatch.md as "reflection agent"

### 5. latency issues and LLM models — *done (2026-08-29)*

Swap from Low = luna to Mistral 3B. because we are located in EU. and Mistral(ministral-3b-2512) has office in france. 
Also we need more than local, low, medium, high models
we need fast-local, fast-low, fast-medium, fast-high and slow-local, slow-low, slow-medium, slow-high
i.e. luna (medium) and luna (none) cost the same. But medium is more accurate/intelligent, but time to first token is over 2 sec. while luna(none) has lower intelligence, but time to first token is under 1 sec

so for async processes like consolidator and reflection we fine with the time to first token toll and want the smarter, slower version.
while for everything before the answer comes we need fast models. (analytics, personality, knowledge and intent)

we might even consider a totaly separate one for intent that combines the best of both. because we want smart, fast answers (but not at any cost)
minimal tier = consolidator and reflection on slow-local, everything else on fast-local
minimal tier = consolidator and reflection on slow-low, everything else on fast-low
default tier = consolidator and reflection on slow-medium, intent on fast-medium, everything else on fast-low
super tier = consolidator and reflection on slow-high, intent on fast-high, everything else on fast-low

fast-low = ministral-3b-2512
fast-medium = ministral-3b-2512
fast-high = mistral-small-2603

slow-low = GPT-5.6 Luna (medium)
slow-medium = GPT-5.6 Luna (medium)
slow-high = GPT-5.6 Luna (medium)

keep the old models as --comments

