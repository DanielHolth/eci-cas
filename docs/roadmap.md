# ECI-CAS — Roadmap

The C# backend and its Next.js companion surface (`morrow-eci/`) are both
built and wired end to end — see [`architecture.md`](architecture.md) for
what exists. This tracks what's still ahead.

## Long-term goals

**Minimal-tier local LLM.** A free 1.8B–3B model (Phi, Qwen, or
similar) for the `minimal` budget tier, so ECI-CAS can run on-device
where cloud connectivity is unreliable. Scope TBD: fine-tuning,
quantization, latency targets.

**Android native client.** On-device minimal-tier agent running the
full agent roster, or a remote-client mode where only Perception and
Action cross process boundaries and all reasoning stays server-side.
Stretch: iOS via shared business logic. Needs UI parity with
Morrow-ECI.

## Companion & knowledge extensions (not started)

Four capabilities for device-sharing and persistent user identity, none
built yet:

**Multi-user profiles.** Multiple users per device; a new name in
conversation offers to create a profile. Each profile is a separate
knowledge graph. Surface: Morrow-ECI profile picker.

**Voice recognition for user detection.** Speaker ID as the primary
detector (continuous, harder to spoof than camera alone), camera as a
fallback for ambiguous cases. Integration point: Perception, before
Impulse fires. Needs baseline voice samples from the original user.

**Biometric + camera authentication.** Device biometrics authenticate
the original user at unlock; a different person picking up the device
triggers camera-based profile-creation. Surface: lock screen / auth
flow. Backend: a user-context field on Perception's meta.

**Diary knowledge category.** A Recall category for entries that
accumulate rather than overwrite — recurring appointments, dated
milestones — so a new doctor's visit doesn't clobber the last one.
Query: Recall surfaces diary entries in temporal order, not as
overwriting facts.

These layer on top of the core system and don't block anything else.
Voice + biometric + camera compose as one "who is this" pipeline feeding
profile context, which diary-aware archiving then reads:

```
biometric unlock → voice/camera check → profile context →
diary-aware knowledge archiving
```

Profiles and auth are Morrow-ECI surface features; diary is a
Recall-agent feature that can be prototyped independently.

## Data quality

**Normalize archive writes to English.** Consolidator and Reflection both
write `ArchiveRecord`s straight from whatever language the turn (or the
substrate's own reply) happened to be in. A user (or a persona) switching
languages mid-conversation currently produces separate archive entries for
the same fact under different words, since lookup is keyword/path-based, not
semantic — no dedup happens across languages. Translating to English before
write (or before path/keyword extraction) would keep one fact as one entry
regardless of what language it arrived in.

## Knowledge-swarm retrieval (semantic two-stage lookup, scalable storage)

Today's `RecallAgent`/`JsonlArchiveStore` do purely deterministic retrieval —
literal ≥5-letter word extraction from the raw turn text
(`SignificantWords.Extract`) proposing lookup paths, then exact-string
matching against `ArchiveRecord.Path`, newest-N-per-path truncation, no
relevance ranking (see [`gap-analysis.md`](gap-analysis.md) §2.1). This
diverges from the Python prototype's actual design, which is semantic at
both stages:

1. A category/topic-selector LLM reads the *distinct* `(category, topic)`
   pairs present in the archive (a small, bounded index regardless of total
   row count) and picks X relevant to the current turn — genuine meaning
   matching, e.g. "tell me about your system" maps to `architecture`/
   `identity` without either word appearing literally in the question.
2. One knowledge LLM per selected `(category, topic)` reads the rows under
   that pair and picks Y by relevance, not recency.

Results are collected into a buffer and handed to Intent as a clean
path+value array.

**Storage scaling — one Parquet file per category.** Confirmed on the
roadmap: partitioning the archive by category (rather than one monolithic
file/store) keeps lookups routed directly to the relevant shard and keeps
predicate pushdown cheap per file. This is a storage/routing fix, not a
substitute for bounding candidate-set size within a category — a single
category can still hold far more rows than fit in one LLM's context (up to
billions, per discussion), so per-category partitioning alone doesn't solve
unbounded fan-in to the knowledge LLM.

**Query shape — keep the two-stage swarm, deepen only on demand.** Decision:
don't default to a fixed deeper tree (e.g. a 3-level 1→3→9 swarm) — that
pays for many extra substrate calls even when a category's row count is
small. Instead, keep the two-layer query (selector LLM → one knowledge LLM
per selected category) as the default, and only have the initial
selector LLM spawn a *larger* swarm — an extra selection layer under a
specific category — if that category's candidate set is still too large for
one knowledge LLM's context after the per-category Parquet shard narrows it
down. Depth grows adaptively with actual data skew, not uniformly for every
lookup.

This needs its own design pass before implementation: how `IArchiveStore`
represents category/topic as first-class queryable fields (today `Path` is
one flat string), how the selector LLM's "distinct pairs" index gets
computed/maintained cheaply as the archive grows, and what threshold
triggers the adaptive third layer.

## Reflection Agent redesign (drive-gated, batched)

Today's `ReflectionAgent` (see [`gap-analysis.md`](gap-analysis.md) for how
it diverges from the Python original) fires on every single conclusion and
unconditionally reposts an "idea" back onto `events.perception`, which
reruns the entire pipeline as a second full turn — doubling substrate cost
and console output per real message, with no batching and no way to write
a quiet internal insight instead of a loud one.

The replacement, sketched in conversation:

- **Buffer, not immediate action.** Accumulate concluded events in-memory
  (same shape as `ConsolidatorAgent._pending`) instead of reflecting on
  every one. A new `ReflectionOptions.BatchSize` (mirroring
  `ConsolidatorOptions.BatchSize`) decides when enough has accumulated to
  look for a pattern — matching Python's `batch_size` (default 5).
- **`Domain` field on `ArchiveRecord`.** `"external"` for Consolidator's
  ordinary keyword writes (the default), `"internal"` for Reflection's own
  derived insights, sharing the same path space but distinguishable and
  independently dedup'd — this is currently missing entirely; there is no
  way today to tell an ordinary fact from Reflection's own thought.
- **Rank, don't spam.** When a batch surfaces more than one candidate idea,
  Reflection ranks them and treats only the single best one as a candidate
  for surfacing — every other candidate (and the top one, if it isn't
  surfaced — see below) is written to the archive as `domain="internal"`
  knowledge. Nothing is discarded; it just isn't always spoken.
- **Drive-gated push vs. write.** Whether the best-ranked idea gets pushed
  back through `events.perception` (visible, spoken path) or just written
  as internal knowledge depends on persona drive state — an eager/curious
  persona that judges the idea too good to sit on pushes it; otherwise it's
  written quietly and stays retrievable (the user can still ask about it
  later via ordinary Recall lookup, it's just not proactively volunteered).
  This is the same "impulse vector" state Python's `current-spec.md` §5.3
  (slow-coloring feedback) and §5.4 (somatic shortcut) describe and that
  [`gap-analysis.md`](gap-analysis.md) already flags as entirely absent from
  the C# port — **this redesign depends on that drive-vector state existing
  somewhere first**, most likely living on `ImpulseAgent` since that's
  where Python kept it. Needs its own design pass: how the state is
  represented and persisted across turns, how Reflection (a different,
  decoupled agent) reads it without C#'s loose-coupling rule turning into a
  direct agent-to-agent reference, and what threshold value counts as
  "eager enough to push."
- **Scoring mechanism, open.** However ranking/eagerness gets decided, it's
  probably one substrate call per batch that returns candidate ideas plus
  either a confidence/insight-worthiness score or enough text for a
  deterministic ranking step to compare — same shape as `ConsolidatorAgent`'s
  existing `ParseFacts`-style line parsing, not a new pattern.

This is new scope beyond a straight gap-fix — it introduces persistent
persona state that doesn't exist anywhere in C# today, not just a Reflection
change — so it needs a real plan (and probably the drive-vector design
question resolved) before implementation starts.

## Open design questions

**Swappable personas.** Switching which persona is active ("which
tamagotchi am I playing with today?"). Recall should stay shared
across personas (it's "what happened," not character); Self should
not — each persona needs its own trait bank that only develops while
active. Open question: does a swap create a new Intent instance or
re-hydrate the same one from a different store? Probably wants its own
design doc before any code — this is the largest single piece of
unscoped work in the project.

**Match input to output, not just retrieve.** Self and Recall
currently answer "what does the archive say that's relevant to this
event" — a retrieval question. The sharper version is "given this
event, what do I already know that changes how I should read it" — an
inference question. Tension: archive-lookup's own design principle is
"report what the records say, not what you happen to know — never
invent a record." Pushing toward inference risks turning Recall/Self
into a second Reasoning. Needs a real design conversation.
