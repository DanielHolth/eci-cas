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

## Knowledge-swarm retrieval (semantic two-stage lookup, scalable storage) — design finalized

Today's `RecallAgent`/`JsonlArchiveStore` do purely deterministic retrieval —
literal ≥5-letter word extraction from the raw turn text
(`SignificantWords.Extract`) proposing lookup paths, then exact-string
matching against `ArchiveRecord.Path`, newest-N-per-path truncation, no
relevance ranking (see [`gap-analysis.md`](gap-analysis.md) §2.1). This
diverges from the Python prototype's actual design, which is semantic at
both stages. Design is now settled; implementation not yet started.

**Record schema.** Replaces the flat `Path`/`Content` shape. One full
worked example, every field filled:

```
category=person  topic=family  subtopic=son  subject=marcus holth
key=birthdate  value=2020-08-28

category=event  topic=wedding  subtopic=family  subject=maria holth
key=location  value=drammen kirke
```

The second example is deliberate: `person`/`family` and `event`/`wedding`
are two structurally different category types (an entity-centered record vs.
an occurrence-centered one) — the writer needs both shapes to learn the
category/topic split isn't just "person stuff," it's a real taxonomy.

- `Category` — 1 word.
- `Topic` — 1 word.
- `Subtopic`, `Subject` — 1-2 words each. `Subject` is usually a unique name
  or entity (a person, a specific event); `Key` is the attribute of that
  subject being recorded (`birthdate`, `location`) — the two play different
  roles even though both are short.
- `Key` — 1-3 words.
- `Value` — 1-5 content words (semantically-loaded terms only — no stop/
  filler words like "is"/"it"/"the", and no full sentences).
- `Timestamp`.
- `Domain` (`Internal`/`External`) — now load-bearing: gates which of
  Intent's two result arrays (see below) a record surfaces in.
- `Importance` (0.0-1.0) — set by the writer at write time. `Consolidator`
  scores it per the rules the user gave (name > birthday/title > address,
  etc. — the writer's own judgment against that ordering, not a fixed
  lookup table). `Reflection`'s self-generated ideas always get a fixed
  score instead of a rules-based one: 0.1 for an idea archived quietly,
  0.2 for one judged worthy of pushing back onto `events.perception` — internal
  ideas stay low-importance by construction, so they don't crowd out real
  facts in a topic's importance-sorted trim. Used to pre-trim a topic's
  candidate rows deterministically before any knowledge LLM sees them, so a
  topic with 10,000 rows doesn't just get truncated by recency.

Writers (Consolidator and Reflection) share this exact schema and prompt
shape — same params on the Archive write call either way, `Domain`
distinguishing which agent wrote it. To prevent topic-name drift across
writers, both are shown the current bundle's existing category/topic
selections (the same data Intent receives) and instructed to match an
existing pair before inventing a new one.

**Consolidator gets the strictest writer instructions.** "Strict" doesn't
mean a content blocklist — it means enforced discipline on which fields are
*structural* vs. *free*: `Category`/`Topic`/`Subtopic` must follow
consistent, matched-against-the-existing-index conventions (this is what
keeps the taxonomy from drifting across a model swap — the rule is about
form, not content), while `Subject`/`Key` have more latitude since they're
naming a specific real-world entity/attribute pair that can't be
pre-enumerated. This distinction (rigid structural fields vs. flexible
content fields) needs to be explicit in Consolidator's prompt, not just
implied by field length limits, so a weaker substitute model still holds
the taxonomy together.

**Category/topic/subtopic index.** One `index.parquet` holding the distinct
`(category, topic, subtopic)` triples present in the archive, plus each
category's Parquet filename. Read once at boot to hydrate an in-memory
cache (the selector LLM needs a populated index on the very first event,
not just after the first live write) and then updated in-memory on every
subsequent write — appended to, not re-read from disk. Same lifecycle as
`SelfAgent`'s persona cache otherwise: invalidated/refreshed on the write
epoch broadcast on `system.control` if a write happened out from under the
in-memory copy (e.g. the seed import), never re-read from disk per event.

**Reasoning — selector only, no advisory text.** `ReasoningAgent` drops its
current "offer relevant reasoning" advisory sentence entirely — Intent now
owns all advisory/reply framing. Reasoning's one substrate call instead
reads the cached index and returns X selected `(category, topic, subtopic)`
triples for the current turn — genuine semantic matching, e.g. "tell me
about your system" maps to `system`/`architecture` without either word
appearing literally in the question.

**Recall — one substrate call per selected triple, run in parallel.** For
each of Reasoning's X selected `(category, topic, subtopic)` triples,
`RecallAgent` opens that category's Parquet shard, pre-trims candidate rows
by `Importance` down to `MaxPerTopic`, and fires one substrate call scoped
to *only* that triple's candidates, picking Y relevant rows. The prompt for
that call shows **only `Subject`/`Key`/`Value`** — `Category`/`Topic`/
`Subtopic` are withheld (the call is already scoped to one fixed triple, so
repeating them is redundant) and `Timestamp`/`Domain`/`Importance` are
withheld too, to keep the knowledge LLM's context as lean as possible. Rows
are still handed to it pre-sorted by `Importance` descending (Archive does
the sort; the LLM never needs the raw score to pick well). Implementation
detail: this is X parallel calls made from inside one `RecallAgent.HandleAsync`
(matching how the existing per-path lookup already works), not X separate
bus agents or a Governance roster change — each call's prompt and result
must stay scoped to its single triple, never see another triple's rows.
`MaxPerTopic` default: 50 (tune per tier alongside `MaxPaths`/`MaxPerPath`
once real usage data exists).

Recall becomes substrate-calling, gated by the existing `UseSubstrate` tier
flag with a deterministic (recency-capped) fallback underneath —
`FallbackPosture.Open`: a failed or unavailable Recall call just means
Intent's reply is less well-grounded that turn, not a blocked turn.

**Two arrays into Intent.** `RecallAgent` returns External and Internal
result sets separately (gated by `Domain`). `IntentAgent.BuildPrompt`
presents them as two distinctly-instructed sections: External knowledge is
stated as fact and weighs heavier; Internal (Reflection-derived) knowledge
is voiced as tentative inference — this lets Intent draw on a detected
pattern confrontationally ("are you sure you went to the gym today? your
BMI says otherwise") without treating its own inference as a stated fact.
This is a second, independent surfacing path alongside `ReflectionAgent`'s
existing drive-gated proactive push.

**Storage scaling — one Parquet file per category.** Categories are
discovered, not predefined — created lazily on first write; "what
categories exist" is a directory listing plus `index.parquet`. Partitioning
by category keeps lookups routed directly to the relevant shard and keeps
predicate pushdown cheap per file — this is a storage/routing fix, not a
substitute for the `Importance`-based per-topic trim above, since a single
category can still hold far more rows than fit in one LLM's context.

**Query shape — keep the two-stage swarm, deepen only on demand.** Default
stays selector LLM → one knowledge LLM per selected triple (not a fixed
deeper tree like a 1→3→9 swarm, which pays for extra substrate calls even
when a topic's row count is small). If a selected triple's candidate set is
still too large for one knowledge LLM's context after the per-category
shard and `Importance` trim, the selector spawns a larger swarm under that
one triple instead of applying uniform extra depth everywhere.

**`memory.jsonl` retirement.** The current live JSONL store is retired
outright under the new schema, not migrated — re-seeded once from the
*original* prototype Parquet data (not from the polluted live file; see
`ConsolidatorAgent`/self-write-pollution note above). One-time throwaway
conversion script, same pattern as the earlier `seed-memory.jsonl` import.

Still open before implementation: exact `IArchiveStore` interface method
shapes, and whether Consolidator skips extraction outright for
Reflection-originated (`triggered_by:"self"`) turns vs. always tagging them
`Domain=Internal` and letting low `Importance` do the filtering naturally.

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
