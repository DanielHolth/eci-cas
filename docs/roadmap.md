# ECI-CAS — Roadmap

The C# backend and its Next.js companion (`morrow-eci/`) are built and wired
end to end — [`architecture.md`](architecture.md) says what exists. This
document owns everything else: what's next, what's parked, what's out of
scope, and compact design records for what shipped.

**Next up.** Nothing is outstanding against the Python prototype's business
logic. The live work comes from the September 2026 external review, below.
Two things lead it: giving the persona a sense of elapsed time, and
streaming Intent's tokens once the first sentence has cleared Security.

---

# What's next

## From the external review — the deferred half

An outside model reviewed the codebase in September 2026: fourteen findings
and twelve ideas. The findings are fixed (`ccab952`, `5609174`) and the
review document is gone — a worklist recording what was already done is a
changelog wearing the wrong hat. Three ideas were pulled forward and shipped
with the fixes, being defects in an idea's clothes: standing rules ahead of
volatile data in `librarian.txt` and `recall.txt`, and saying
`[Recall: nothing on file]` out loud. One was declined — a per-key in-flight
`Task` map in `CachingEmbeddingProvider`; releasing the lock across the call
gets the parallelism, the map only adds dedup for a batching caller that
does not exist.

### Latency — the three serial calls

The floor is **Librarian → Recall → Intent**, roughly 700 + 500 + 900 ms
before a word reaches the person. Impulse, Identity and Hindsight run beside
them and cost nothing. In the order to attempt:

**Stream Intent's tokens.** The largest win, and it moves perceived rather
than actual latency. The obstacle is that Red must never reach Action, so
streaming provisionally and retracting is out. Running `SecurityRuleSet`
incrementally over accumulating text is affordable, but whether a rule
written against a sentence stays sound on a prefix is an open question. So:
**stream only after the first sentence has cleared the rules.** Gives up the
first ~200 ms, keeps the invariant, commits to nothing.

**Prefetch Recall's file reads from Hindsight's leads.** The passage sweep
is local and finishes in microseconds; its leads are usually a subset of the
final selection. Reading those pair files while the selection call is still
in flight spends idle disk and warms `ParquetArchiveStore`'s cache. No
substrate cost, no new message, and a wrong prefetch is only a wasted read.

**Raise Recall's skip threshold.** With `MaxPickedPerWorker` at 6, an
archive of 40 rows still pays a full picking round to discard almost
nothing. A threshold at "as much as a prompt comfortably holds" removes a
serial call from most turns and degrades into today's behaviour when it
stops being true. Safe where `a0b43c9` was not: that skipped the *selector*,
and the index quietly became the answer whenever it fit. This skips picking
*after* selection, so no judgment is bypassed — only a filter with nothing
to filter. Config knob, measured by `RetrievalProbe`.

**Pre-warm the HTTP connections at boot.** Each named `HttpClient` pays DNS,
TCP and TLS on its first call, which lands on the first turn a person types.
A throwaway request per provider at startup moves it where nobody waits.
Pair with an explicit `PooledConnectionLifetime`.

### Interiority that is actually grounded

Constrained by the standing rule: **surface interiority only where something
actually happened to cause it.** Every item is an event the system already
detects and throws away. Nothing here makes the persona talk about itself
more; that is the failure mode, not the goal.

**Elapsed time — first.** Nothing knows whether the last turn was ninety
seconds or three weeks ago. The largest gap between this and a mind that
feels continuous, and nearly free: Perception knows `UtcNow` and the store
knows the last conclusion's timestamp. Stamp the gap on the perception
envelope, let Impulse map it to a drive nudge, and pass it to Intent as
words like `DriveTrend` is — *"[Since: three weeks]"*. Then "it's been a
while" is a claim about something measurable.

**Notice when a fact changes.** `ParquetArchiveStore.Merged` already detects
the collision and silently replaces. That is the persona changing its mind
about the world. Carrying the superseded value forward — as a prior, or a
one-line note to Reflection — buys *"you said Oslo before"* with no new
retrieval, no new call, no invention.

**Let salience decay without deleting anything.** `Importance` is fixed at
write time, so a fact that mattered once outranks one that matters now,
forever, and Recall's budget is spent on it. Decaying it with age unless
re-touched, purely as a *retrieval* weight, gives forgetting-shaped
behaviour with no data loss. The capsule cares what is stored; the persona
cares what surfaces. They are allowed to differ.

**Let the corpus grow while nobody is talking.** Firing Reflection on
silence so it **thinks** rather than speaks — coming back after a week to a
persona that has had thoughts is a different thing from one resuming
mid-sentence. Speaking unprompted needs the parked platform decision;
thinking unprompted does not. Sequenced after elapsed time: this is the one
item that acts with no person in the loop, so it wants the generation cap
honoured and a hard ceiling on notes per idle period.

**Make the echo depth do something.** Hindsight computes `EchoDepth` and
nothing reads it. It detects the persona resonating with its own past
thoughts rather than the person's present one — a real failure mode for a
system that feeds its own notes back. Last of the five: the damper can
suppress genuine continuity as easily as an echo, so log what it actually
does across real sessions before letting it change a reply.

## Companion & knowledge extensions (not started)

**Speech-to-text input.** Dictation only — push-to-talk filling the existing
composer, so what is sent stays reviewable text and
`sendPerceive(text, profileId)` is unchanged. Purely a surface feature: no
new topic, no audio on meta, no agent contract change. Speaker
identification is **cut** — see below; the mic answers *what was said*,
never *who said it*.

**Biometric + camera authentication.** Device biometrics at unlock; a
different person picking up the device triggers camera-based profile
creation. Backend is a user-context field on Perception's meta, which the
profile field already is.

**Diary knowledge category.** A category whose entries accumulate rather
than overwrite — recurring appointments, dated milestones — so a new
doctor's visit doesn't clobber the last one. Recall surfaces them in
temporal order, not as overwriting facts.

**Profiles, later increments.** A new name in conversation offering to
create a profile; profile deletion and merge.

## One instance per person (symbiosis)

**The intended shape is one Morrow-ECI per person**, not one shared persona
tracking who it is talking to. The relationship is symbiotic: the persona
develops against a single person over a long time, and that only works if
its drive state, its self-derived ideas and its personal archive all belong
to that one relationship. A family of four is four instances, not one
instance with four hats.

This is why **speaker identification is cut**. It only ever answered "which
user is this" on a device with one shared persona — a question that does not
arise when the instance already belongs to someone.

**Accessibility is a primary driver.** A companion that knows one person
deeply — their routine, their vocabulary, what they can and can't do
unaided — is most valuable to someone who needs it, and that value comes
from depth against one person rather than breadth across several.

Multi-user profiles keep their place as the **shared-device path**, not the
primary design. Nothing shipped in iteration 1 is invalidated; what changes
is that profile-scoped storage is the accommodation, while a dedicated
instance gets the whole archive by construction.

## Toolbox agent — IoT actions (not started)

Action today only produces speech. A companion that matters to someone with
a disability has to *do* things: lights, locks, thermostat, blinds. The
sketch is a **toolbox agent** owning a registry of callable device
capabilities, on the action side of Governance so every device call passes
the same verdict gate a reply does — an IoT action is exactly the class of
thing that must never fire on Red.

**A device response comes back in as perception.** Not a return value:
the toolbox publishes what the device said onto `events.perception` and it
runs as an ordinary turn, the same seam Reflection's ideas use with a
different tag (`"device"`). No new topic, no new contract. The payoff is
that Impulse colours on it for free — a lock that refuses to close is
something the persona should *feel*, and a return-value design would make
that a special case. Unsolicited state comes free too: a doorbell is a
perception with no preceding action.

Two hazards to settle in the design pass:

- **The loop.** Action → perception → action is a cycle, and a device turn
  firing another device call is how a house starts flapping. Probably a rule
  that a `triggered_by = "device"` turn may speak but may not act — stricter
  than a depth cap and easier to reason about. **That rule is Governance's**:
  it is a verdict on an action.
- **Archivist.** It hard-skips `"self"` today. Device turns need the same
  decision made deliberately: most acks are noise, a few are facts worth
  keeping. Likeliest shape is skip by default and let the toolbox write the
  rows that matter.

**Flood guard — `DeviceBlockCount`.** A flapping sensor drives a full agent
turn per event, which is real spend and a console nobody can see past. Count
events per device over a window and stop admitting past the threshold. Per
device, never global. **The count belongs in the toolbox, not Governance**:
by the time Governance sees a turn, four agents have already made substrate
calls, so filtering there pays for every flapping event and only then
declines to act. Admission control sits at the boundary, before publish.

**A trip is spoken, not silent** — suppression the person can't see is
indistinguishable from a device that stopped working, so the block enters as
one perception and Intent voices it, exactly once, on the transition. Two
details: the *count* is what's suppressed, not the drive nudge (a flood must
not colour Impulse per event, or a broken device rewrites the relationship
overnight); and **recovery is open**. Automatic decay hides a real fault
behind a re-flood cycle; manual-only loses a sensor permanently for someone
who cannot reach it. The escape is neither — the persona *raises* it after a
quiet window and stays blocked until a person answers. Mechanically that is
the drive-gated push Reflection already does.

Open beyond that: one agent with a tool registry or one per protocol; which
integration surface (Matter, Home Assistant, MQTT, vendor APIs); and how a
tool call is represented on the bus without giving Intent a second output
vocabulary. Wants its own design pass before code.

## Memory architecture — the layers not built

The passage corpus shipped (see the design records). These interlock with
it and with each other; pulling one out changes what the others are for.

The question that started it was whether the pair-addressed archive beats
RAG. The honest answer is that it *is* RAG — select, rank, splice — with a
symbolic index in place of a vector one. It wins on everything that matters
for a persona's own knowledge (addressable, hand-correctable, no reindex
when the embedding model changes, facts rather than chunks, zero
infrastructure) and loses badly on latency.

### Two-layer vector retrieval

Two vectors at two granularities, not five and not one per row component:

- **Pair layer.** One vector per `category/topic`, loaded at boot from JSON,
  replacing Librarian's substrate call with an in-memory cosine sweep.
- **Row layer.** One vector per `ArchiveRecord`, written by Archivist into
  the Parquet row.

**The row vector covers `subtopic/subject/key` and excludes both
`category/topic` and the value.** The pair layer already encodes the former.
The value is excluded for a sharper reason: a query never contains it. Match
"what's my name?" against a vector encoding `this/user/name = Daniel` and
the token *Daniel* pulls the row away from where the query lands, having
contributed nothing — worse as values lengthen. The value stays stored and
read; it simply isn't what you match against. If value-shaped queries prove
they matter, the fix is **a second arm unioned in, never a blended score**.

The rule underneath both: **embed what the query will look like, not what
the data looks like.**

### Aliases

The embedded text and the stored path are not the same string.
`assistant/identity` stays exactly that on disk. What gets *embedded* is a
retrieval-facing gloss written as the questions it should answer: *"my own
name, what I'm called, my traits — facts about me, the assistant, not about
the user"*. This fixes the question-versus-label asymmetry on the document
side, which is far cheaper than fixing it on the query side, and it is why
always-including `assistant/*` was rejected: unconditional inclusion makes
the persona faintly self-absorbed every turn, because facts in the prompt
get used. The alias is selective.

Aliases are few, read once at boot, in a plain JSON file. Derived, one-way,
disposable — never a second name for the pair, never written into a path,
never shown to Intent, so they stay clear of the store's no-drift property.
Hand-written for `assistant/*`; LLM-written once per user-space pair at
creation, never per turn. When Morrow keeps missing a topic, the fix is
**editing one line of English**.

### Union, not replacement

Selected pairs would be the union of `vector top-K` and `LLM selection`.
There is a class of question no embedding reaches: *"Am I old enough to rent
a car?"* needs `person/profile/birthdate`, and the link is an inference
chain, not a similarity. An LLM selector makes that leap; cosine
structurally cannot. Aliases narrow the gap only for neighbourhoods someone
thought to write down.

So the union buys accuracy, not latency — the LLM arm still gates the turn.
Latency comes back only from the row layer, which removes Recall's picking
call. To spend the LLM arm where it earns its keep: **escalate on low
confidence** — take the top cosine hits when they are high and
well-separated, call the model when they're flat. And below a size
threshold, skip retrieval entirely: a new profile has a few dozen facts and
the correct move is to send all of them.

### The episode corpus

Archivist writes only explicitly-stated facts, so a great deal is discarded:
circumstance, moods, plans, half-formed thoughts, themes recurring across
weeks. That is what a second store is for — semantic memory (**the archive**,
what Morrow *knows*: curated, structured, precise) versus episodic (**the
episode corpus**, what Morrow has *seen*). Separation is what lets the corpus
be permissive without diluting the archive.

An episode is **not a transcript** — agent chatter, bundles and security
passes are the bloat. Two fields with distinct jobs: a one-or-two-sentence
**summary**, which is what gets vectorized and is the retrieval handle; and
the **exchange**, ~150 tokens of what was said and answered, which is what
gets returned and read. Embed the short thing, return the real thing, so
Reflection reads actual language rather than a paraphrase of a paraphrase.

Three rules keep it lean: **no extra substrate call** (the summary is one
more field in Archivist's existing response); **nothing already a fact**;
and **most turns write nothing**, gated on salience, since Impulse's
appraisal is already on the bundle.

Storage reuses the Parquet store — a reserved category,
`episode/<year-month>/<profile>/<turnId>/…` — inheriting per-pair locking,
a monthly file as a natural unit, and the ArchiveTool REPL. The cost is that
`episode/*` must be excluded from Librarian's index and Recall's live path,
or Morrow starts reciting its own diary mid-conversation.

### Nothing is ever deleted

Decay was proposed and **withdrawn**. An exchange is roughly 600 bytes, so a
hundred turns a day is 22 MB a year and sixty years is under 1.5 GB. Storage
was never the constraint. The only thing that genuinely strains is
brute-force cosine over millions of vectors, a distant problem with known
answers.

Corpora partition by year, so no index is ever large, a year can be
reindexed alone when the embedding model changes, and searching two years
means opening two directories — "the file name is the index" one level up.

**Digests index upward; they never carry forward.** A distillation of 2026
does not move into 2027 — that is decay wearing a new hat. The digest layer
sits *above* the years and points down into them: Reflection reads digests
to learn which month is worth opening, then pulls the real episodes. The
rule that makes it safe: **a digest may summarise, but it must cite.** Every
digest row carries the addresses it came from, so a summary is a table of
contents and never a replacement.

### Reflection is already the cross-event agent

Archivist runs at `BatchSize: 1` — one turn, no history, structurally blind
to "third time this week they've mentioned being tired." Reflection already
batches concluded events; it is simply underfed. Raising the batch widens
the window without deepening it — the digest pyramid is what buys reach,
letting Reflection see a year in a prompt smaller than today's batch. Large
flat inputs are the worst option on cost, latency and accuracy, since models
degrade at spotting a pattern in a long undifferentiated list.

**Reflection deliberately stays on `slow-medium`.** A weaker model fails
loudly on bad instructions where a strong one quietly compensates and the
flaw ships. Upgrade after the prompts are good, not before.

### Async deep recall (far future)

The year is 2028 and someone asks *"did you make any reflections on this
topic in 2026?"*. Morrow answers immediately — *"let me ponder that"* —
dispatches deep retrieval through the toolbox, and comes back minutes later,
unprompted. Most of this exists: the self-triggered loop-back seam, a
fire-and-forget bus, and Impulse already answering instantly while slow work
runs. A request/response architecture could not do it at all; here it is a
new *trigger* for a path that already runs. Three things need designing:

- **A promised answer must arrive.** Reflection's `FallbackPosture` is
  Closed — it skips on substrate failure. Right for a self-generated idea,
  wrong for an answer someone is waiting on.
- **The deferred answer needs a thread back.** Fresh `CorrelationId`, so
  without a meta key carrying the original the person has no idea what it
  answers.
- **Rate limiting.** The most expensive call in the system. Same instinct as
  `DeviceBlockCount`.

### The capsule

The archive is meant to outlive the software — a design constraint, not a
sentiment. **Text is the artifact; everything else is a rebuildable index.**
Parquet is open and columnar, so DuckDB or pandas will read it in forty
years without a line of this C#. Vectors will strand on a dead embedding
model, and that is fine precisely because they are derived.

What a backup cannot add later is **legibility**. A disc of unexplained
Parquet is still opaque, so a plain-text README belongs *in the archive
directory itself*: what the columns mean, what the path convention is. That
costs nothing now and cannot be retrofitted onto media already written.
Physical durability is deliberately not solved here.

**Open: inheritance.** One instance per person is right for symbiosis, but a
legacy means a second person eventually opens the first's archive — a child
querying a parent's decades. Nothing says whether that is a read-only record
they can search, or whether their own Morrow may Recall against it. Those
are very different: an archive *of* someone versus a persona speaking *as*
them. Much easier to rule in or out now than after twenty years of rows.

## Security rule coverage — low priority

The eight rules in `config/security-rules.json` are a backstop, not the
primary safety mechanism, and a backstop that grows without bound stops
being auditable. Two findings look like defects; one limitation is inherent
and should stay.

**Every pattern is English.** `kill yourself`, `rm -rf` — matched against
the reply text, so the same reply in Norwegian passes all eight rules. Given
the persona is spoken to in both, this is the common path. The fix is not
translating one for one: some rules (`bypass-this-system`) are about
phrasings that don't translate, others (`weapons-and-precursors`) about
nouns that mostly do. Worth a pass that decides per rule.

**The irreversible rules are on the soft side of the split.**
`irreversible-world-effect`, `spend-money` and `disclose-credentials` are
Yellow — Intent revises once and proceeds. `irreversible-world-effect`'s own
description argues the other way: *"Action executes literally. Anything
destructive must not reach it by accident."* The description wants Red and
the verdict says Yellow; they disagree inside one rule. One word plus a test.

**Not a defect: Security sees only the proposed reply text.** It catches
phrasings, not intentions, so paraphrase walks past it. That is the cost of
keeping the hard stop mechanical and unarguable — a gate that could weigh
the case *for* a reply would be evaluating the argument, which is Intent's
job. Leave it.

**Why low priority.** The rules that exist fire correctly and the gate is
wired to Action. Revisit when the persona is routinely spoken to in
Norwegian by someone other than its author, or when Action gains a side
effect reaching outside the process.

## Still open on the surface

**The picker does not solve attribution.** `localStorage` keeps the last
person's identity until someone switches, so on a shared device the persona
happily attributes one person's turn to another. With speaker ID cut,
nothing closes that gap automatically. An explicit "not me" affordance is
probably worth more than pretending the picker handles it.

**No auth means the registry is open.** Profile ids are guessable and
`GET /api/profiles` is unauthenticated. Fine for a household device, not
beyond it — stated rather than implied, since the server-side stream filter
is a privacy boundary.

**Impulse's drive state is per profile; Reflection's slow colouring is
not.** A batch spans whoever was talking, so one person's tone colours
everyone's persona. Two shapes: partition the batch by profile and pay
per-profile calls, or scope the mood to whichever profile dominated —
cheaper and wrong-feeling. Partitioning is probably right, because the cheap
option contradicts the stated intent that what warms the persona toward one
child must not pre-colour how it meets the parent an hour later.

## Long-term goals

**Minimal-tier local LLM.** A free 1.8B–3B model for the `minimal` tier, so
ECI-CAS runs on-device where connectivity is unreliable. Scope TBD:
fine-tuning, quantization, latency targets.

**Android native client.** On-device minimal-tier agent running the full
roster, or a remote-client mode where only Perception and Action cross
process boundaries. Stretch: iOS via shared business logic.

---

# Design records — shipped

Kept for the reasoning, not as outstanding work. Where one contradicts
[`architecture.md`](architecture.md), architecture.md wins.

**Knowledge-swarm retrieval.** Replaced deterministic retrieval (literal
≥5-letter word extraction, exact-string path matching, newest-N truncation)
with semantic selection at both stages: Librarian selects pairs from the
index, Recall fans out one picking call per chunk. Introduced the five-part
record schema (`category/topic/subtopic/subject/key=value`) plus
`Timestamp`, `Domain` (Archivist-external vs. Reflection-internal) and
`Importance`, the last used to pre-trim deterministically so a huge topic is
not merely truncated by recency. Both writers share the schema and are shown
existing pairs to stop topic-name drift. One deliberate cut: **Recall does
not split results into External and Internal arrays for Intent** — merging
into one importance-sorted list lets a genuinely important self-derived
insight outrank a trivial external fact instead of being quarantined.

**Pair-addressed archive.** Librarian's prompt showed the entire triple
index, unbounded and ever-growing. Rejected: sharding into buckets with
parallel selector calls (bucketing is lossy — `system/identity` versus
`person/identity` disambiguates only if both are visible to one call), and a
hierarchical second selector (a whole new selector kind to resolve
subtopic). Shipped instead: drop subtopic from the index and let Recall read
it off the rows — a lossless dimensionality reduction, since every
cross-category distinction stays visible in one call. Storage became one
file per pair, `{esc(cat)}~{esc(topic)}.parquet`, so **the file name is the
index**: `index.parquet` deleted, `rebuild-index` removed rather than
reimplemented, per-file locks instead of one global one. `~` over `|`
because `|` is illegal in a Windows filename. No per-pair row cap — a
scientist may discuss one subtopic at enormous length, and truncating them
is exactly the wrong failure; `RowsPerWorker` chunks instead, and the only
ceiling is `MaxConcurrentRecalls`, trimmed breadth-first. Every worker
starts at once in one flat `WhenAll`; a per-pair `WhenAll` inside a loop
would serialize pairs behind the slowest.

**Reflection redesign (drive-gated, batched).** The old agent fired on every
conclusion and unconditionally reposted an idea, doubling cost per real
message. Now: buffer a batch, rank candidates and surface at most the best
one, write the rest quietly as `Domain=Internal`. Whether the best is pushed
or written depends on drive state — which is why this depended on the
drive-vector work landing first.

**Passage corpus (the first vector layer).** Deliberately neither designed
layer: nothing in the archive is embedded. What is embedded is a 5–15 word
note Reflection writes about **what the last batch failed to retrieve** — a
code review of its own recall. That is "embed what the query will look like"
pushed one step further: a miss is already phrased in the shape of the
question that caused it, and the note carries pair pointers as row metadata,
so a cosine hit is a **lead**, not an answer. Keeps three properties cheaply
— union not replacement, no second copy of a fact (pointers resolve against
the live index), and no new per-turn substrate call. **The revisit** ships
with it: a stored note is quoted into the next Reflection prompt and may be
rewritten in place. Which note gets quoted was at first the newest, making
the corpus a chain — a thought open to revision for one batch then frozen.
It now picks the note *nearest* the batch, which makes it a trail:
something written months ago becomes revisable the day the persona circles
back. `Reflection:RevisitMinScore` floors it, falling back to the newest
rather than to nothing, since a floor that stops an unrelated old thought
must not also stop the persona sharpening what it just wrote.

**Hindsight — what it is for.** Recall reads facts; Hindsight reads what the
persona made of them. A thought note is written for no one, about what a
batch of turns made the persona notice. Hindsight wakes one when a prompt
brushes against it, months later if that is when it fits, and hands it to
Intent as its own voice rather than as a fact. What we are after is
something the key:value archive structurally cannot produce: a direction the
persona chose, and a flavour nobody wrote for it. It may turn out to have a
personality, and that personality may not be flattering — that is the
experiment working. Three constraints: **a hit is a lead** (the cosine floor
is deliberately low, 0.25 — notes that restate the prompt tell the persona
what it knew, and the sideways ones are the point); **prose and facts stay
separate substances**, two bundle slots, never re-ingested as a fact; and
**the ring has no external grounding** — Hindsight → Intent → Reflection →
new note closes on itself, the pairs field being the only part reality can
contradict. If it starts agreeing with itself, look here first.

Built in `c69a34e` as its own agent rather than living inside Librarian, so
Intent weighs archive facts and the persona's own prose as two independent
bundle slots instead of one arriving as a passenger on the other's envelope.
Librarian kept the *pointer* half. Passages stay out of Archivist's
extraction scope by omission — it reads `perception.text` and
`librarian.selected_pairs` and nothing else — with a test for the same
reason the recalled-values boundary has one.

**The corpus had no model identity (closed).** `Cosine` returns 0.0 on a
width mismatch, so switching model dimension silently retired every note; a
swap at the *same* width was worse, with old vectors still scoring and no
longer meaning anything. Closed by stamping a model id on every row —
`onnx:<weights path>` (the path, not a friendly name: two operators pointing
at different downloads run different models whatever the file is called) or
`openai:<model>` — and refusing to boot on disagreement. Refusing over
re-embedding is the conservative option, not the cautious-sounding one:
re-embedding rewrites the corpus on a config change an operator may have
made by accident, and that is exactly the change that should rewrite
nothing. An empty id skips the check, and pre-stamp rows are excluded rather
than counted as disagreeing — an unrecorded model is not a conflicting one.

**A misspelled provider disabled the corpus in silence (closed).** The docs
said `Embedding:Provider = "api"`; the switch matched `"onnx"` and
`"openai"` and defaulted to `NullEmbeddingProvider`. What made it bad was
the disguise: byte-identical to the normal, announced "weights aren't
downloaded" state. `"api"` is now the documented alias, `"none"` means none
explicitly, anything else throws at startup. The general rule: **a corpus
nothing can search must be either deliberate or loud, never plausible.**
`scripts/get-embedding-model.ps1` came with it, because the gap between
Hindsight being complete and being *tryable* was a 90MB download nobody had
automated.

**The turn was embedded twice (fixed).** Librarian and Hindsight both embed
the same capped `perception.text`, and `OnnxEmbeddingProvider` holds a
semaphore across inference — so the second waited for the first and
recomputed a bit-identical result. Fixed in the provider, not on the bus:
`CachingEmbeddingProvider` wraps whichever embedder config selects, so the
API provider's two round trips collapse the same way. Neither agent learns
the other exists. The faster alternative — embed once in Perception and
forward the vector — is rejected on point 1 of the four-point plan: a float
array is the largest thing that would ever ride the bus, to save an
in-process recomputation. Related and already fixed (`f34b5a8`): debug lines
passing a `string.Join` as an argument ran the join at every log level.
Structured logging defers the *template*, never the arguments, so anything
data-proportional needs an `IsEnabled` guard.

**Multi-user profiles, iteration 1.** Personal knowledge scoped by
*directory*, not filename or a new column: `archive/` shared,
`archive/profiles/{id}/` personal, same naming convention inside each, so
"the file name is the index" holds in both and today's flat archive simply
*becomes* the shared tier with no migration. The profile is a **parameter**
on `IArchiveStore`, not a scoped view or a store factory — one singleton, no
new abstraction, and `null` is exactly the pre-profile behaviour. Reads
union both tiers with the profile winning; writes land in the profile
directory unless the category is on `Archive:SharedCategories`. The
allowlist is `["assistant"]`: the persona's identity belongs to nobody on a
shared device. Surface side: `GET/POST /api/profiles`, a full-screen picker
on cold start persisting to `localStorage`, `profileId` on every perceive
and on the stream subscription, and preset avatars rendered as an identity
ring *around* the Impulse-coloured circle, so avatar choice never touches
the expression mapping. Two things surfaced while building: switching
profiles is a **remount**, not a state reset (`Conversation` is keyed by
profile id, so accumulated turns go with the component); and `/api/stream`
now writes an SSE comment immediately on connect, because browsers hold
`onopen` until the first body byte and a profile-scoped client can sit there
reading "Disconnected" while perfectly connected.

**Expression is chosen on the backend.** Impulse appraises it from its own
drive vectors and publishes it on the advisory; Governance captures it when
the bundle completes — the verdict envelope never carried the advisories, so
that is the last place it exists — and forwards it on every
Action/Conclusion. The block path re-reads the face *after* the frustration
nudge, which is the whole point of nudging. The client draws the word it is
given and falls back to `neutral` rather than blanking the avatar. The
advisory had to move to the end of Impulse's handler so the face is the one
this turn produced. Tuning came with it: the instant nudges were ported
verbatim from the prototype and were an order of magnitude too small for
`DriveVectors`' bucket edges — a critical event moved alertness to 0.105
against a low edge of 0.35, so six drawn faces were unreachable in practice.
They are now sized against the edges. One departure from the Python bucket
order: raised alertness outranks warmth, since both can be high at once and
a face that smiles through an emergency reads as not having heard it.

**The turn was opaque.** A person saw an avatar and a reply, and nothing
about which faculties ran, what was read or written, what it cost. The fix
is a projection, not a renderer: `TurnProjection` folds a turn's envelopes
into one `TurnRecord` and `TurnLog` serves it three ways — `/api/log`,
`/api/log/stream`, and any `ITurnLogSink`. The drawer is one consumer of a
shape three things read, which is why the reduction did not go in React.
Getting the facts onto the bus was most of the work: `SubstrateResult`
carried latency, tokens and cost that all five call sites logged and
dropped, so `SubstrateTrace` now publishes one envelope per call on
`system.telemetry`; Archivist's written paths and Reflection's passages were
in the same position. Two things surfaced: Reflection's flush spans a batch,
so deriving its telemetry from a concluded turn would scope a shared cost to
one person's window — it creates its own correlation and reads as unowned;
and a record cannot be written when the reply lands, so sinks are handed one
after `TurnLog:SettleMs` of quiet. Deliberately not done: embedding calls
are invisible, since `IEmbeddingProvider` reports neither latency nor cost;
the latency total is wall-clock, not the sum of its addends, since a
parallel fan-out that summed would claim more time than the turn took.

**Reflection colours Impulse (slow colouring).** Reflection's batch call now
also returns a `mood|<label>` line from a closed five-label vocabulary,
parsed separately from candidates so it survives a batch with no ideas, and
riding the `Reflected` control envelope — no new message type. It lives on
Reflection, not Archivist: Archivist is a dumb per-turn fact writer with no
batch view and no business forming an opinion about mood. **Impulse owns
every number**: `SlowColoring` maps label → delta, and an unmapped label is
a no-op. Deltas are ~0.01–0.03 against instant nudges' 0.05–0.15, once per
batch rather than per turn, and the test asserts the gap against the instant
nudges themselves rather than a pinned literal.

**Drive-state history as grounded interiority.** `memory.jsonl` was
append-only and every read asked for the newest line per path, so it grew
forever to hold lines nothing could return; it had also accumulated ~135
archive-shaped rows from the pre-Parquet design, 97% fossil, since purged.
The obvious fix was one line per path — recommended, then withdrawn, because
it is the worse bug: the superseded vectors are the only record of how the
persona has been over time. A scalar is a gauge; the series is a history.
So the store keeps a **window** per path, `Reflection:DriveHistory` deep,
and `DriveTrend.Describe` turns it into words on the axes `Expression()`
already uses. **Words, never numbers**, with a test asserting no decimal
reaches the prompt: `Curiosity: 0.83` invites the persona to quote its own
telemetry back, which is the register of a status page. **Still open:**
whether the trend colours the note at all is unmeasured. Ignored is an
acceptable outcome; the persona announcing its own engagement is rising is
the failure, and would mean tightening the instruction rather than removing
the input.

**Degraded-substrate notice.** A dropped connection used to produce a
fluent, confident, entirely ungrounded answer with no signal that the
persona had been thinking with half its faculties missing. `SubstrateHealth`
holds the vocabulary — a meta key, three causes, `Classify` and `Mark` — and
every caller stamps its advisory. Governance, the only agent seeing the
whole fan-out, emits **deterministic native text**: a degraded Intent's
notice *replaces* the reply (its fallback sentence is not an answer, and
dressing it up as one is the lie); a degraded advisor appends a
parenthetical; a Red verdict gets neither. Native is the crux, not a style
preference: an LLM-authored apology cannot be produced by the LLM that isn't
answering. `UseSubstrate: false` is deliberately not a degradation.
Reflection retains a failed batch at the head of `_pending` (an outage used
to cost the turns it would have thought about, not just the thinking);
Archivist gets no equivalent, since the facts were never extracted and a
retained turn is just a second guess at the same prompt. `TimeoutMs` and
`CircuitOpenMs` are per provider, so five agents don't each re-discover the
same dead endpoint at full timeout cost; the first call after the window is
a live probe. **Decided against a startup reachability probe** — it only
catches "network down at boot", gives false confidence when it passes, and
makes startup depend on the internet.

*Still open from it:* `LibrarianAgent` overrides `HandleAsync` and
reimplements the base try/catch/log/publish nearly line for line, so the
marking had to be written twice. Folding it back is bigger than the marking
was — `ParseResult` gets no access to the archive index `ParsePairs` needs,
and Librarian's empty-index early return fires before a prompt is built. The
likely shape is the base class handing subclasses a failure classification.
And an asymmetry worth naming: manifest drift fails loud before the bus
starts, but a `Tier` pointing at live providers never verifies anything
about them — the most strictly validated config is the one that silently
degrades at runtime.

**Skipping the selection call — since reversed.** Librarian used to
short-circuit when the whole index fit under `MaxSelectedPairs`, removing a
round-trip from every turn on a young archive. `a0b43c9` removed that fast
path: the selector's judgment was never exercised until the archive was too
big to check by eye, and near-duplicate pairs — the thing selection exists
to tell apart — appear long before that. Recall keeps its equivalent skip,
because the budget it guards is per-worker rather than per-turn: an
under-budget chunk is genuinely nothing to choose from.

**Normalize archive writes to English.** Writers used to record in whatever
language the turn was in, so a user switching languages produced separate
entries for the same fact. Solved as a prompt constraint rather than a
translation pass, with **proper nouns carved out explicitly** — translating
a name would corrupt the record itself, which is worse than the duplication
being prevented.

**Writes dedup by address.** Normalizing gets a restated fact onto the same
address, but the store appended anyway, so an archive grew with every
restatement. `AppendAsync` now replaces a row at an existing
subtopic/subject/key outright: the latest statement is the true one — "lives
in Oslo" then "lives in Bergen" must not leave both for the picking model to
choose between. Deliberately *not* a field merge: one rule, explainable in a
sentence, and a wrong overwrite is fixed by stating the fact again.

**Archivist's path reuse is load-bearing by omission.** Reusing an existing
`category/topic/subtopic` is what keeps a restated fact landing on one
address, and Archivist gets there by being shown Librarian's selected pairs
as bare path labels — *only* the labels. The bundle also carries
`recall.facts`, the actual rows Recall read, and Archivist never reads that
key, so recalled values can't be echoed back as freshly stated ones. "Give
Archivist more context" is a one-line change that closes the loop, and the
write-time merge would hide it. There is now a test.

### The four-point plan — lean bus, instructions in config

Daniel's, stated as constraints: (1) the bus carries as little as possible,
definitely no instruction text; (2) every substrate agent's instructions
live in config; (3) one block per agent, never shared; (4) Daniel revises
them all by hand — the deliverable the rest exists to enable.

**Stage 0 — `intent.prompt` was a confirmed bug.** `BuildPrompt` returned
instruction plus contract plus content, and the whole string was published,
rode the proposal into Reflection, and was rendered through a 240-character
cap. The standing instruction alone was 840 characters. **So Reflection had
never seen a turn** — not the message, not the advisories, not the facts —
just byte-identical boilerplate, ten times a batch, then the reply. Every
note in the corpus was written from replies alone. The key's doc comment
claimed it was "Reflection's window into what Intent actually had to work
with"; the code sent boilerplate. Fixed by publishing the assembled
*context* and never the standing rules. It went first because until it
landed, no instruction rewrite could be evaluated.

**Stage 1 — audit the bus.** Twenty-five meta keys, each traced to publisher
and readers; the table is now in architecture.md. Two keys had no reader and
are gone: `control.epoch_id` (Identity invalidates on `control.kind` alone)
and `perception.source_type` (set on the same line as
`perception.triggered_by`, one fact published twice with only the second
read). Neither could ever have failed a test, which is how both survived —
**a key nobody reads cannot be observed to be wrong**, and that is the
argument for auditing rather than waiting for a symptom. Three
`governance.*` keys have no in-process reader and stay: `SseBroadcaster`
fans whole envelopes, so they are the display layer's contract, recorded as
such so the next person to run this sweep does not cut them. No payload was
larger than its purpose once Stage 0 landed. What the table did expose is
the cost of the fresh-bag rule — four keys re-published by agents that did
not originate them, paid visibly rather than by a bag that accumulates
forever.

**Stage 2 — instructions to config.** Plain text files, not JSON strings:
point 4 is hand revision, and multi-paragraph prose in a JSON string means
escaped newlines and a syntax error one stray quote away. Assembly stays in
C#; only the text identical on every call moved.

*The cost of point 3, named.* `ArchiveWriteStyle` was one fragment shared by
Archivist and Reflection, and the sharing was real — a rule that drifts puts
one fact under two spellings. Splitting it makes drift possible, accepted
deliberately, because a shared fragment cannot be hand-revised for one agent
without silently revising the other. The mitigation is a test asserting both
files still state the rule — not that they match, which would rebuild the
coupling in the assertion.

*The second coupling: the path convention.* `system/` was load-bearing for
three agents and named by none consistently. Intent stated the reading rule,
Recall depended on exactly that distinction without naming it, and
Archivist — which *mints* the paths — did not contain the word. The writer
was never told the convention the reader depended on. **The category itself
was wrong.** `system` held 60 rows of CAS architecture and 12 of persona
identity; Intent's rule ("describes YOU, the assistant") was true of the 12
and false of the 60, and a `systems/agent architecture/` drift variant had
already appeared. Renamed to **`assistant`**, keeping both topics: both
instruction texts already said "the assistant" in prose and then translated,
the architecture rows *are* self-description (`.../emergence = interplay of
narrowly specialized roles` was an architecture fact already filed under
identity, because it is both), and no reader needs the distinction. Keeping
one category removes a boundary judgment Archivist would drift on; topic
does the separating, which is what topic is for. `self` went the same way —
an earlier draft dropped it as unused, which was wrong (`FixedCategory`
filed pushed ideas under `self/reflection`, and dropping the category alone
would have sent the persona's own ideas into whichever profile was
speaking); settled by moving the data instead. The cost, named: `assistant`
is the role token, so every recalled row carries the helpful-assistant prior
into Intent's prompt. Judged weak next to Identity's persona instruction.

*Cut first, and let the prototype find the flaws.* The rename removed the
reason three clauses existed, and all three were deleted rather than kept
behind a fixture — a rule kept in case it is load-bearing is never tested
and so can never be removed later, which is how `ResponseContract` grew back
after `407e5f1` trimmed it. Overly terse is the diagnostic. The one
asymmetry: **reads are free to break, writes leave residue.** A bad reply is
one visible turn; a bad Archivist write is a row Recall serves back and
Hindsight eventually wakes.

*A validator may reject a row, never edit one.* `ArchivistAgent` wrote
`PromptCap.Apply(value)`, truncating every archived value at 240 characters
mid-word with an ellipsis. `PromptCap` exists to stop one hop's text
compounding across generations, which is sound on the way *in*; on the way
*out* into an append-only store a too-long value is not rejected, it is
stored corrupt and served back forever. Removed from both write paths.
Rejection stays for genuinely malformed output, because a row that never
lands leaves no residue. Length is asked for in the model's own terms, not
enforced — and deliberately loosened to "1-5 keywords, or one terse sentence
with no filler", since some facts do not fit keywords.

*The rest of Archivist became a grammar.* Sorted by whether the instruction
states a *format* or coaches *behaviour*: the six-field line, per-field word
counts, known-pairs list and worked examples stay; the meta-commentary
paragraph, "do not infer, guess, or embellish", "a turn with an obvious
stated fact must never come back empty" and the duplicated empty-case line
were all anti-symptom patches and all went. 2065 characters to roughly 450.

**Stage 3 — closed, no changes.** Reviewed on the 2026-09-03 commute; the
five files were read one by one and none was revised, the cut-first method
having been applied during authoring rather than saved for this stage. The
four observed symptoms survive as things to watch, not as a revision
backlog — a symptom that persists now needs a fixture, not a rewording:
**Intent is theatric** (suspected: the "spokesperson for a collective"
framing and the one-or-two-sentence clamp); **advisories arrive unweighted**
(bare brackets with nothing saying how to weigh them, which matters more
since a woken note is the persona's own opinion arriving in one);
**Librarian and Recall select too narrowly** ("name the people you know
about" came back empty — `MaxSelectedPairs` was raised across every tier,
with `MaxConcurrentRecalls` and `MaxPickedPerWorker` moved to per-tier
config alongside, but the relevance rule is instruction text and wants a
fixture asserting an enumeration question returns more than one topic); and
**Archivist needs handholding** — the longest instruction in the codebase,
and its category choices are what Hindsight's `pairs` field is checked
against, so its failures are not local.

**Stage 4 — the agents that never call a substrate.** Stage 2 moved the five
*prompt* files on the reasonable reading that instructions are what you send
a model. That missed Identity's persona, Impulse's reflex reply and
Governance's three notices — all C# constants, so none was in the folder
Daniel reviewed, and the persona's own self-description had gone unreviewed
for months because changing it meant a rebuild. The test is not "does a
model see it" but **"is this a writing decision"**.
`ArchiveWriteStyle.EnglishFields` went the other way and was deleted
outright: its whole job was deciding whether a sentence appeared in a
prompt, which the instruction file now does directly. `TerseValue` survives,
because the drift risk is real. **Identity is a seed, not a setting** — the
file writes to `assistant/persona` only when empty and the store wins
thereafter, because a persona meant to grow should not be silently replaced
by a `git pull`, and "file always wins" makes the store pointless.

**Stale references and milestone tags** (`556bc43`). Nine `plan §X`
cross-references pointed at a document that does not exist, repointed at
architecture.md; milestone tags described shipped work as pending and were
dropped; `ArchivistAgent`'s class comment claimed extraction was grounded in
"Recall's own lookup results" when the key it reads is Librarian's selected
pairs. One survivor: `Archive:Path` resolves to `memory.jsonl` and feeds the
*agent state* store, not the archive — `AgentState:Path` would say what it
is, but that is a rename with a config migration attached.

**What the SSE stream ships.** `EnvelopeDto.From` serialised the whole
MetaBag, so the largest value on the bus went down `/api/stream` three times
a turn and was read by nothing. On the bus it was free (an in-process object
reference, never serialised); the waste was purely at the HTTP edge.
`Sse:ExcludedMetaKeys` denies it there — a deny-list rather than an
allow-list, since an allow-list needs editing every time an agent adds a key
the UI wants, and the failure mode of forgetting is a silently missing
feature rather than visible bloat.

---

# Parked

Real gaps against the Python prototype's `current-spec.md`, deliberately not
being worked. Revisit when the named condition holds, not before.

**§6.1 Watchdog.** No liveness ladder, no idle-musing timer. Parked until
the destination platform is known, or until the running system proves flaky
in practice. Designing a liveness ladder before knowing what it runs on is
guesswork.

**§6.2 Recovery bootstrap.** No IaC-style sequencer. `Program.cs` plus the
manifest validators already cover config-drift detection, a partial
differently-shaped analog. When revived it should be scoped wider than the
original: one sequencer that doubles as an **installer**, provisioning a
missing local LLM and missing agents rather than only restarting dead ones.
That makes it platform-dependent, so it waits on the same decision.

# Out of scope

Not gaps. Listed so they don't get re-raised as oversights without a fresh
decision.

**Messaging-plumbing differences.** Python's synchronous recursive
`publish()` versus per-agent queues; Governance-as-orchestrator versus
Governance-as-listener; Librarian calling Knowledge directly versus
selecting pairs for Recall. The port targets business logic, not
architecture.

**§7.2 Budget Mode auto-latch.** Only per-event cost logging exists, not the
spend-cap auto-latch to deterministic fallbacks. Revisit if real spend
becomes worth automating around.

**§4.2 `is_parroting()`.** Structurally moot: the Python check stops Intent
echoing *Analytics'* advisory prose, and `LibrarianAgent` is a pure selector
emitting no advisory text at all. The refusal-lead-in constraint is moot for
the same kind of reason — Governance appends the Blocked text
deterministically, so Intent never gets the chance to soften a block.

**Two arrays into Intent.** The merged, importance-sorted result set
replaces it on purpose; see the knowledge-swarm record.

# Open design questions

**Swappable personas.** Switching which persona is active. Recall should
stay shared across personas (it's "what happened," not character); Identity
should not — each needs its own trait bank that only develops while active.
Open: does a swap create a new Intent instance or re-hydrate the same one
from a different store? Wants its own design doc — the largest single piece
of unscoped work in the project.

**Match input to output, not just retrieve.** Identity and Recall answer
"what does the archive say that's relevant to this event" — a retrieval
question. The sharper version is "given this event, what do I already know
that changes how I should read it" — an inference question. Tension:
archive-lookup's own principle is "report what the records say, never invent
one", and pushing toward inference risks turning Recall into a second
Librarian.
