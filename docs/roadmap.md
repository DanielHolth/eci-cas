# ECI-CAS — Roadmap

The C# backend and its Next.js companion surface (`morrow-eci/`) are both
built and wired end to end — see [`architecture.md`](architecture.md) for
what exists. This document owns everything else: what's next, what's
parked, what's deliberately out of scope, and the design records for work
already shipped.

**Next up:** the degraded-substrate notice (a partial thought currently
reads as a confident one), and profile-scoped archive storage, the one
piece of multi-user profiles iteration 1 still outstanding. Nothing else is
outstanding against the Python prototype's business logic; everything
else here is parked, further out, or a record of what's already built.

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

Four capabilities for input, device-sharing and persistent knowledge,
none built yet:

**Multi-user profiles.** Planned in detail below — iteration 1 is
specified and is the next thing to build. Later increments: a new name in
conversation offering to create a profile, and profile deletion/merge.

**Speech-to-text input.** Dictation only — a push-to-talk button that
fills the existing composer, so what gets sent stays reviewable text and
`sendPerceive(text, profileId)` is unchanged. Purely a surface feature:
no new bus topic, no audio on Perception's meta, no agent contract
change. Speaker identification is **cut** — see "One instance per
person" below; the mic answers *what was said*, never *who said it*.

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
With speaker ID cut, the "who is this" pipeline collapses to biometric
unlock feeding profile context, which diary-aware archiving then reads:

```
biometric unlock → profile context → diary-aware knowledge archiving
```

Profiles and auth are Morrow-ECI surface features; diary is a
Recall-agent feature that can be prototyped independently.

## One instance per person (symbiosis)

**The intended shape is one Morrow-ECI per person, not one shared
persona that keeps track of who it's talking to.** The relationship is
symbiotic: the persona develops against a single person over a long
time, and that only works if its drive state, its self-derived ideas and
its personal archive all belong to that one relationship. A family of
four is four instances, not one instance with four hats.

This is why **speaker identification is cut**. It only ever existed to
answer "which user is this" on a device with one shared persona — a
question that doesn't arise when the instance already belongs to
someone. Voice input stays, as dictation; the identity half of it is
gone, and with it the baseline-voice-sample capture, the "who is this"
gate ahead of Impulse, and the camera fallback for ambiguous speakers.

**Accessibility is a primary driver, not a side benefit.** A companion
that knows one person deeply — their routine, their vocabulary, what
they can and can't do unaided — is most valuable to someone who needs
it, and that value comes from depth against one person rather than
breadth across several. This is also what makes speech input worth
building on its own merits, independent of identity.

Multi-user profiles keep their place as the **shared-device path**, not
as the primary design: a phone or tablet passed around a household still
needs the separation profiles give it, and per-profile Impulse is
already the right mechanism either way. Nothing shipped in iteration 1
is invalidated — what changes is that profile-scoped archive storage is
the shared-device accommodation, while a dedicated instance gets the
whole archive to itself by construction.

## Toolbox agent — IoT actions (not started)

Action today only produces speech. A symbiotic companion that matters to
someone with a disability has to be able to *do* things in the home:
lights, locks, thermostat, blinds, appliances. The sketch is a **toolbox
agent** owning a registry of callable device capabilities, sitting on the
action side of Governance so every device call passes the same verdict
gate a reply does — an IoT action is exactly the class of thing that must
never fire on a Red verdict.

**A device response comes back in as perception.** Not a return value, not
a callback — the toolbox publishes what the device said onto
`events.perception` and it runs as an ordinary turn. That mechanism
already exists: `ReflectionAgent` loops its own ideas back the same way,
tagged `perception.triggered_by = "self"`, so device feedback is the same
seam with a different tag (`"device"`). No new topic, no new agent
contract. The payoff is that Impulse colours on it for free — a lock that
refuses to close is something the persona should *feel*, and a
return-value design would have made that a special case.

The same seam gives unsolicited state for free: a doorbell or a motion
sensor is a perception with no preceding action, and nothing has to know
the difference.

Two hazards fall out of it, both to settle in the design pass:

- **The loop.** Action → perception → action is a cycle, and a device
  turn firing another device call is how a house starts flapping. Wants
  an explicit rule — probably that a `triggered_by = "device"` turn may
  speak but may not act, which is stricter than a depth cap and easier to
  reason about. **That rule is Governance's**, not the toolbox's: it is a
  verdict on an action, the same gate Security's matrix already runs.
- **Consolidator.** It hard-skips `triggered_by = "self"` today, because
  Reflection already wrote that record correctly before pushing. Device
  turns need the same decision made deliberately: most acks are noise
  ("light on"), a few are facts worth keeping ("front door locked at
  23:10"). Likeliest shape is the same one — skip by default, and let the
  toolbox write the rows that matter itself.

**Flood guard — `DeviceBlockCount`.** A faulty device is the failure mode
this design invites: a flapping sensor publishing perceptions in a loop
drives a full agent turn each time, which is real substrate spend and a
console the person can't see past. The toolbox counts events per device
over a window and stops admitting that device past the threshold. Per
device, never global — one broken sensor must not deafen the persona to
the rest of the house.

**The count belongs in the toolbox, not Governance.** Governance
subscribes to Perception/Advisories/Verdict and bundles the fan-out, so
by the time it sees a turn, Reasoning, Recall, Self and Impulse have
already made their substrate calls — filtering there pays for every
flapping event and only then declines to act, which is the exact cost the
guard exists to prevent. Admission control has to sit at the boundary,
before publish. Governance also stays decision-only by design, and its
only state is turn-scoped `_bundles`; a per-device counter over a time
window is long-lived cross-turn state of a different kind.

**A trip is spoken, not silent.** Suppression the person can't see is
indistinguishable from a device that simply stopped working, so the block
enters as one perception of its own and Intent voices it in the persona's
own words: *the hallway sensor is misbehaving, I've stopped listening to
it.* Exactly once, on the transition — a message per suppressed event
would be the flood wearing a different hat.

Two details worth fixing early:

- **The count is what's suppressed, not the drive nudge.** A flood must
  not colour Impulse per event, or a broken device rewrites the
  relationship overnight. The trip itself is worth feeling; the thousand
  events behind it are not.
- **Recovery is an open question, not a decided default.** Automatic
  decay is wrong when the device is genuinely broken: it resumes,
  re-floods, re-trips, and the cycle hides a fault that needs a person
  with a screwdriver. Manual-only is wrong for the accessibility case:
  someone who cannot reach or reset the device loses a sensor
  permanently to what may have been a thirty-second blip. The shape that
  escapes both is neither — the persona *raises* it after a quiet
  window (*the hallway sensor has been quiet a while, want me to listen
  to it again?*) and stays blocked until a person answers. A real fault
  can't silently re-flood, and nobody has to remember the block, because
  the persona carries it. Mechanically that is the drive-gated push
  Reflection already does, not new machinery.

Open questions beyond that: whether the toolbox is one agent with a tool
registry or one agent per protocol; which integration surface it speaks
(Matter, Home Assistant, MQTT, vendor APIs); and how a tool call is
represented on the bus without giving Intent a second output vocabulary.
Wants its own design pass before code.

## Degraded-substrate notice (planned)

**If it can't think, say so.** A dropped connection, a tunnel, a captive
portal, an expired key — the substrate becomes unreachable and the turn
still has to conclude honestly.

Today it half-does. `CognitiveAgent.HandleAsync` catches the failure,
logs a warning, and on `FallbackPosture.Open` publishes
`FallbackResult(envelope)` **marked in no way at all**. When Intent's own
call is the one that failed, the person sees Intent's fallback sentence
and the system looks honest by luck. When Reasoning and Recall fail but
Intent succeeds, the person gets a fluent, confident, entirely
ungrounded answer and no signal whatsoever that the persona was thinking
with half its faculties missing. That second case is the dangerous one,
and it is silent.

**Governance owns the notice**, for the same reason it already appends
Blocked text deterministically in native code rather than letting Intent
phrase a refusal:

- It is the only agent that *can* know. Governance bundles the fan-out by
  `CorrelationId`, so it alone sees which advisories arrived, which
  arrived degraded, and which never came at all. Every other agent knows
  only its own fate.
- Deterministic native text survives a dead substrate. An
  LLM-authored apology cannot be produced by an LLM that isn't
  answering — this is the whole crux, not a style preference.
- It keeps the honesty rule in one place, next to the block text it
  already owns.

What has to change to enable it: `CognitiveAgent` must mark the fallback
it publishes rather than substituting invisibly — degraded, plus a cause
(unreachable / timed out / substrate disabled). Governance then counts
degraded and absent advisories in the bundle and picks between a
partial-thinking notice and an "I can't think right now" — with a
`UseSubstrate: false` agent deliberately *not* counting as degraded,
since a deterministic-by-config agent is working exactly as configured.

**Timeouts are the harder half.** The DNS failure that prompted this
(`SocketException 11001`, host not resolvable) failed instantly. A real
mid-journey interruption instead hangs until the HTTP timeout expires,
and a minute of silence followed by an apology is worse than the apology
alone. The substrate timeout has to be short enough that the notice is
prompt, which makes it a tier-tunable knob rather than a constant.

Related papercut worth fixing alongside: Consolidator logs a tidy
one-line warning on substrate failure while Reasoning dumps a full stack
trace, so an offline run floods the console unevenly.

## Multi-user profiles, iteration 1 — mostly shipped

One device, several people — each with their own avatar, their own
personal facts, and their own emotional relationship with the persona.
Shared world knowledge stays shared. Named users to date: Daniel and his
son.

**Status: the surface, the registry and per-profile Impulse are
implemented; profile-scoped archive storage is not.** What that means in
practice: several people can use the device, each with their own avatar,
their own window, and their own emotional relationship with the persona —
but the facts they teach it still all land in the shared archive. The
storage section below is the remaining work.

### Storage — not yet built

Personal knowledge is scoped by *directory*, not by filename or a new
column:

```
archive/                              shared pairs (world facts, system~identity, …)
archive/profiles/{id}/                same {esc(cat)}~{esc(topic)}.parquet convention, personal facts only
archive/profiles/{id}/profile.json    displayName, avatar, createdAt
```

Reads union shared + active profile, profile winning on key collision.
Writes go to the profile directory unless the category is on a shared
allowlist. This keeps `ParquetArchiveStore`'s defining property — the
file name *is* the index — intact inside each directory, and needs no
schema change and no rewrite of existing files. Today's flat `archive/`
becomes the shared tier unchanged; no migration. `ProfileStore` already
creates and owns `archive/profiles/{id}/`, so the directories the
personal pairs belong in exist; what's missing is Recall and Consolidator
reading and writing through a profile-scoped view of `IArchiveStore`.

### Impulse is per profile — shipped

Drive state is per profile, not per device. The persona holds a separate
emotional relationship with each person: what warms it toward one child
does not pre-color how it meets the parent an hour later.

Mechanically this is a keying change, not a redesign. `ImpulseAgent`
already persists `DriveVectors` as a single `IAgentStateStore` record at
`impulse/drive`; that becomes `impulse/drive/{profileId}`, resolved from
the profile on Perception's meta. `ReflectionAgent` and `GovernanceAgent`
read the same path and must be keyed the same way — Reflection's
slow-coloring pass then drifts each profile's drive state independently,
from that profile's turns only. Absent a profile, the path falls back to
today's `impulse/drive`, so single-user runs and existing state keep
working. Governance carries the profile onto its frustration signal for
the same reason, taking it off the bundled perception, since `Derive()`
replaces meta rather than inheriting it.

**One part is still device-wide: Reflection's slow colouring.** Reflection
scores a whole batch of concluded turns in a single substrate call, and
that batch can span profiles, so the mood it reports colours whatever
profile the control envelope names — nobody, today. Splitting it means
grouping the buffer by profile and paying one substrate call per profile
per flush, which is a Reflection-side change with a real cost attached and
is deliberately not in iteration 1. The instant nudges — the ones a person
actually feels within a turn — are per profile.

### Frontend requirements — shipped

**R1 · Profile registry.** `GET /api/profiles` returns
`[{ id, displayName, avatar }]`; `POST /api/profiles` creates one.
Client-side `lib/profiles.ts` wraps both.

**R2 · Picker on cold start.** With no active profile, Morrow-ECI shows a
full-screen picker: existing profiles plus "New profile". The active
choice persists in `localStorage`; a compact switcher chip sits in the
header. Switching resets the turn feed.

**R3 · Profile context on every request.** `sendPerceive(text, profileId)`
posts `{ text, profileId }`; `PerceiveRequest` gains the field and
Perception puts it on meta — the "user-context field" the auth work above
also wants. The stream subscribes as `/api/stream?profileId=…` so one
person's turns don't render in another's window.

**R4 · Avatar selection.** Each profile picks from a fixed set of preset
avatars, stored on the profile and rendered as an identity ring *around*
the Impulse-colored circle. Impulse keeps sole ownership of expression
colour; avatar choice must not touch that mapping.

**R5 · Creation flow.** Name and avatar, two fields, no auth. Voice and
camera detection stay out of this iteration — the profile field on meta
is the seam they plug into later.

Two things surfaced while building these. Switching profiles is a
*remount*, not a state reset: `Conversation` is keyed by profile id, so a
person's accumulated turns go with the component instead of being cleared
in place. And `/api/stream` now writes an SSE comment immediately on
connect — browsers hold `onopen` until the first body byte, and a
profile-scoped client can wait a long time for its first real envelope,
long enough to sit there reading "Disconnected" while perfectly
connected.

### Out of scope for iteration 1

Auth, per-profile theming, cross-profile visibility of personal facts,
the diary category, and profile deletion or merge.

## Reflection colors Impulse (slow-coloring feedback) — shipped

**Status: implemented.** Python's §5.3 slow-coloring feedback — drive state
drifting with the tone of what's been happening, as opposed to Impulse's
instant keyword-triggered shifts — now runs on Reflection.

It lives on Reflection, not Consolidator: `ConsolidatorAgent` stays a dumb
per-turn fact writer with no batch-level view and no business forming an
opinion about mood, while `ReflectionAgent` already buffers a batch, makes
one substrate call across it, and reads drive state to gate push-vs-write.

- Reflection's existing batch call now also returns a final `mood|<label>`
  line from a closed five-label vocabulary (`warm`, `tense`, `dull`,
  `curious`, `neutral`), parsed separately from candidates so it survives a
  batch that produced no ideas.
- The label rides on the `Reflected` control envelope Reflection already
  published (`ReflectionAgent.MoodKey`) — no new message type, and Impulse
  was already subscribed to `system.control` for
  `GovernanceAgent.FrustrationKind`.
- **Impulse owns every number.** `ImpulseAgent.SlowColoring` maps label →
  `DriveVectors` delta, the same discipline `FrustrationNudge` follows: an
  agent may request a shift, but the magnitude that lands is written in
  Impulse. An unmapped or missing label is a no-op.
- Deltas are ~0.01-0.03 against instant nudges' 0.05-0.15, and fire once
  per `ReflectionOptions.BatchSize` turns rather than per turn. That gap is
  the distinction between slow colouring and the somatic shortcut, and
  `ImpulseAgentTests` asserts it against the instant nudges themselves
  rather than a pinned literal, so either side stays tunable.

## Data quality

**Normalize archive writes to English — shipped.** Consolidator and
Reflection previously wrote `ArchiveRecord`s in whatever language the turn
(or the substrate's own reply) happened to be in, so a user switching
languages mid-conversation produced separate entries for the same fact —
lookup is by triple, and nothing dedups across languages.

Solved as a prompt constraint rather than a translation pass: one shared
const, `ArchiveWriteStyle.EnglishFields`, interpolated into both writers'
prompts next to `TerseValue`, so the rule can't drift between them. It
normalizes `category`/`topic`/`subtopic`/`key` only — **proper nouns are
carved out explicitly**, since translating a name or a place would corrupt
the record itself, which is worse than the duplication being prevented.

Costs no extra substrate call. Mock-tier tests can only assert the
instruction is present; real confirmation is a `Default`-tier smoke test
stating one fact in Norwegian and again in English and checking both land
on the same triple.

## Knowledge-swarm retrieval (semantic two-stage lookup, scalable storage) — shipped

**Status: implemented.** `ParquetArchiveStore`, the archive index,
Reasoning-as-selector, and Recall's parallel fan-out are all in `src` and
covered by tests. The rest of this section is kept as the design record for
what was built, not as outstanding work.

**Partly superseded** by the pair-addressed archive below, which shipped
after it: the index is now `(category, topic)` rather than a full triple,
files are per-pair rather than per-category, `index.parquet` no longer
exists, and `MaxPerTopic` was replaced by `RowsPerWorker` /
`MaxConcurrentRecalls`. The paragraphs below are left as written — they
record the reasoning at the time, not the current shape. Where the two
disagree, the later section wins.

What this replaced: the old `RecallAgent`/`JsonlArchiveStore` pair did
purely deterministic retrieval — literal ≥5-letter word extraction from the
raw turn text proposing lookup paths, exact-string matching against a flat
`Path`, newest-N-per-path truncation, no relevance ranking. That diverged
from the Python prototype's design, which is semantic at both stages.

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
- `Domain` (`Internal`/`External`) — marks whether a row was written by
  Consolidator (external fact) or Reflection (self-derived inference). Not
  used to split Recall's results into separate arrays for Intent — see the
  note at the end of this section.
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
subsequent write whose `(category, topic, subtopic)` isn't already present
in the cache — appended to, not re-read from disk, and not re-appended for
a triple that's already indexed. Same lifecycle as `SelfAgent`'s persona
cache otherwise: invalidated/refreshed on the write epoch broadcast on
`system.control` if a write happened out from under the in-memory copy
(e.g. the seed import), never re-read from disk per event.

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

**`memory.jsonl` retirement — seed with one record, not a data migration.**
The current live JSONL store is retired outright under the new schema, no
conversion script, no re-import of the prototype's `knowledge.parquet`/
`identity.parquet` rows (34 + 3 rows — dropped entirely, not carried
forward). The archive boots with exactly one file, `system.parquet`, one
row:

```
domain=external  category=system  topic=identity  subtopic=persona
subject=this  key=name  value=morrow  importance=0.5  timestamp=now()
```

`SelfAgent`'s existing identity store/file is separate and explicitly out
of scope here — untouched by this migration, keeps whatever it does today.

**Consolidator hard-skips self-triggered turns.** A turn whose `meta` shows
`TriggeredByKey="self"` (Reflection's own idea, looped back through
`events.perception`) is never passed to `ExtractFactsAsync` at all —
Reflection already wrote that idea correctly (`Domain=Internal`, its own
fixed `Importance`) before pushing it, so Consolidator re-extracting from
it would either duplicate the record or, worse, mis-tag it `External` as
today's bug does. This closes the self-referential-pollution root cause
identified earlier this session.

One deliberate divergence from the design above: **Recall does not split
its results into External and Internal arrays for Intent.** An earlier draft
had `RecallAgent` returning two `Domain`-gated result sets that
`IntentAgent.BuildPrompt` would present as fact-vs-tentative-inference
sections. The shipped logic replaces that — `Category=self` on Reflection's
own writes already marks a row as internally-derived, and merging both into
one `Importance`-sorted list means a genuinely important self-derived
insight can outrank a trivial external fact instead of being quarantined in
a second-class section. Not outstanding work; cut on purpose.

## Reflection Agent redesign (drive-gated, batched) — shipped

**Status: implemented.** Batching, ranking, drive-gated push-vs-write, and
the `Domain` field all exist. Kept below as the design record.

The `ReflectionAgent` this replaced fired on every single conclusion and
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
  (slow-coloring feedback) and §5.4 (somatic shortcut) describe, absent from
  the C# port at the time — **this redesign depended on that drive-vector
  state existing first**, and it now lives on `ImpulseAgent`, where Python
  kept it. Needs its own design pass: how the state is
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

## Pair-addressed archive (index collapse, per-pair files) — shipped

**Status: implemented.** `ArchiveTriple` is now `ArchivePair`, the store
keeps one file per pair, `index.parquet` is gone, and Recall does the
subtopic resolution.

**The problem.** `ReasoningAgent` showed its one substrate call the *entire*
in-memory index — every distinct `(category, topic, subtopic)` triple, one
line each — and `MaxSelectedTriples` capped only how many it could pick, not
how many it was shown. Fine at small scale; at 1000+ triples an unbounded,
ever-growing prompt on every turn.

**Rejected alternatives.** Sharding the index into buckets with parallel
selector calls: bucketing is lossy — it risks separating triples that need
weighing against each other (`system/identity` vs. `person/identity`
disambiguates "system name" from "person name" only if both are visible to
the same call), and there's no principled bucketing that guarantees related
triples land together. A hierarchical selector (a second LLM stage over
subtopics) fixes that but adds a whole new selector kind just to resolve
subtopic.

**What shipped instead** — reuse Recall's existing fan-out rather than
inventing a stage:

```
Reasoning (Category + Topic)
  --> N x Recall (Subtopic + Subject + Key = Value)
        a pair holding more rows than RowsPerWorker splits into
        that many parallel workers, all in one flat WhenAll
```

Dropping subtopic from Reasoning's index is a lossless dimensionality
reduction, not a lossy split: every cross-category distinction Reasoning has
to make is still fully visible in one call. Subtopic moves down into Recall,
which now reads it off the rows themselves.

**No per-pair row cap.** The original sketch capped a pair's combined pool
and split only past the cap. That was dropped — a scientist may discuss one
subtopic at enormous length, and truncating them is exactly the wrong
failure. `RowsPerWorker` chunks instead of trimming, and the only ceiling is
`MaxConcurrentRecalls`, per turn rather than per pair. `RowsPerWorker` is a
quality knob, not a context one: a candidate row is under 20 tokens, but a
small non-reasoning model's list-scanning accuracy degrades around 30-50
items, well before its context window does.

Splitting rows across workers carries the same "might separate things that
needed comparing" risk bucketing had upstream, but at far lower stakes:
Recall scores rows near-independently, where Reasoning does cross-category
disambiguation. The trim to `MaxConcurrentRecalls` is breadth-first so every
selected pair keeps its most-important chunk.

**Every worker starts at once.** The complete worker list is built across
all pairs before any substrate call, then a single `Task.WhenAll`. The trap
avoided is nesting — a per-pair `WhenAll` inside a loop over pairs would
serialize pairs behind the slowest, so a pair that split into N workers
would stall every pair after it. Reads are likewise one parallel phase, not
one-per-worker.

**Storage changed shape with it.** One Parquet file per `(category, topic)`
pair, named `{esc(category)}~{esc(topic)}.parquet`, so the file name *is*
the index: `index.parquet` is deleted, the pair set is recovered by listing
the directory, and a write no longer rewrites a full index file. The index
can't drift from the data, so ArchiveTool's `rebuild-index` was removed
rather than reimplemented. `~` was chosen over `|` because `|` is an illegal
filename character on Windows; both halves are percent-escaped to
`[A-Za-z0-9._-]`, which also covers `~` itself and makes the single-char
separator unambiguous. The global store lock became one lock per file, so
Recall's workers never contend and a Consolidator or Reflection write only
blocks readers of the one pair it touches.

Existing archives were deleted rather than migrated, per the single-record
seed below.

## Parked

Real gaps against the Python prototype's `current-spec.md`, deliberately
not being worked. Not cut — revisit when the named condition holds, not
before.

**§6.1 Watchdog.** Absent — nothing in `src` matches `Watchdog`, liveness,
or heartbeat. No 5-level escalation ladder, no idle-musing timer. Parked
until the destination platform is known, or until the running system
actually proves flaky in practice, whichever comes first. Designing a
liveness ladder before knowing what it runs on is guesswork.

**§6.2 Recovery bootstrap.** No 7-step IaC-style sequencer, no `BootCheck`
liveness step. `Program.cs` + `AgentSubstrateManifestValidator` +
routing-manifest validation already cover config-drift detection (fail
loud on startup), a partial differently-shaped analog. When revived, it
should be scoped wider than the Python original: one sequencer that
doubles as an **installer**, provisioning a missing local LLM and any
missing agents rather than only restarting dead ones. That makes it
heavily platform-dependent, so it waits on the same platform decision the
Watchdog does.

## Out of scope

Not gaps. Listed so they don't get re-raised as oversights without a fresh
decision.

**Messaging-plumbing differences.** Python's synchronous recursive
`publish()` vs. C#'s decoupled per-agent queues; Governance-as-orchestrator
vs. Governance-as-bus-listener; Reasoning calling Knowledge directly vs.
selecting archive triples for Recall to fan out on. Per
`csharp-rebuild-spec.md`'s framing, the port targets business logic, not
architecture — these are by-design divergences, not things to reconcile.

**§7.2 Budget Mode auto-latch.** Only per-event cost logging exists
(`ISubstrateProvider` results log estimated cost at default log level), not
the spend-cap/manual/terminal/transient auto-latch to deterministic
fallbacks. Revisit only if real substrate spend becomes worth automating
around.

**§4.2 `is_parroting()`.** Never a requirement on this project, and now
structurally moot. The Python check stops Intent echoing *Analytics'* raw
recommendation back to the user — a real risk there, since Analytics handed
Intent advisory prose. In C#, `ReasoningAgent` is a pure selector returning
`(category, topic, subtopic)` triples and emitting no advisory text at all,
so there is no analytical sentence to parrot. The related refusal-lead-in
constraint is moot for the same kind of reason: Governance appends the
Blocked text deterministically in native code, so Intent never gets the
chance to soften a block.

**Two arrays into Intent.** See the note at the end of the knowledge-swarm
section — the merged, `Importance`-sorted result set replaces it on purpose.

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
