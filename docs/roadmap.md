# ECI-CAS — Roadmap

The C# backend and its Next.js companion surface (`morrow-eci/`) are both
built and wired end to end — see [`architecture.md`](architecture.md) for
what exists. This document owns everything else: what's next, what's
parked, what's deliberately out of scope, and the design records for work
already shipped.

**Next up:** nothing is outstanding against the Python prototype's
business logic. Everything here is parked, further out, or a record of
what's already built.

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
- **Archivist.** It hard-skips `triggered_by = "self"` today, because
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
by the time it sees a turn, Librarian, Recall, Identity and Impulse have
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

## Degraded-substrate notice — shipped

**If it can't think, say so.** A dropped connection, a tunnel, a captive
portal, an expired key — the substrate becomes unreachable and the turn
still has to conclude honestly. Before this, a failed fan-out produced a
fluent, confident, entirely ungrounded answer with no signal at all that
the persona had been thinking with half its faculties missing.

**How it works now.** `SubstrateHealth` (in `ISubstrateProvider.cs`) holds
the vocabulary: a `substrate.degraded` meta key, three causes
(`unreachable` / `timed out` / `refused (code)`), `Classify(Exception)` to
map a failure onto one, and `Mark(meta, cause)` to stamp it. Every
substrate caller — Intent and Librarian via `CognitiveAgent`, plus Recall,
Reflection and Archivist on their own paths — classifies its failure
and marks the advisory it publishes. Governance, the only agent that sees
the whole fan-out by `CorrelationId`, records which roster members came
back degraded or never came at all, and on the verdict emits
**deterministic native text**:

- Intent degraded → the notice *replaces* the reply. Intent's fallback
  sentence is not an answer, and dressing it up as one is the lie.
- An advisor degraded or absent → the reply stands, with a parenthetical
  appended naming who was missing. Less grounded, still worth saying.
- Red verdict → no notice. A blocked turn says one thing and nothing else.

Native text is the crux, not a style preference: an LLM-authored apology
cannot be produced by the LLM that isn't answering.

**`UseSubstrate: false` is deliberately not a degradation.** A
deterministic-by-config agent is working exactly as configured. It is now
honoured on all five callers, not just Intent — the early return happens
before the call and publishes with `degraded: null`.

**Reflection retains its batch; Archivist does not.** Reflection used to
abandon a whole flush on failure, so an outage cost the persona the turns
it would have thought about, not just the thinking. It now puts the batch
back at the head of `_pending`, capped at
`Reflection:MaxBufferedBatches` × `BatchSize`. Archivist gets no
equivalent: the facts were never extracted, so there is no raw material to
retry from — a retained turn there would just be a second guess at the
same prompt.

**Timeouts and the circuit breaker.** A mid-journey interruption hangs
until the HTTP timeout expires, and a minute of silence followed by an
apology is worse than the apology alone — so `Providers:*:TimeoutMs` is a
per-provider knob (defaults 30s OpenAI, 15s Mistral) applied to the named
`HttpClient`. `Providers:*:CircuitOpenMs` opens a per-provider circuit for
N ms after a transport failure or timeout: subsequent calls throw
instantly instead of five agents each re-discovering the same dead
endpoint at full timeout cost, and the first call after the window is a
live probe that closes the circuit on any reply. It lives in
`OpenAiCompatibleSubstrateProvider` — agents should not know about network
topology.

**Decided against: a startup reachability probe.** It only catches
"network down at boot", gives false confidence when it passes, and makes
startup depend on the internet. The circuit breaker covers the same ground
and handles the transient case too.

**Also fixed alongside:** every caller logged `LogWarning(ex, …)` with a
full stack trace, so one offline turn printed four or five near-identical
dumps and the actual warning scrolled away — now one classified line each.
And telemetry only logged on success, so exactly the turns worth measuring
measured nothing — `CognitiveAgent`, Librarian and Reflection now time the
failure path too.

**Still open: the `CognitiveAgent` / Librarian duplication.**
`LibrarianAgent` overrides `HandleAsync` and reimplements the base
try/catch/log/publish nearly line for line, so the marking had to be
written twice. Folding it back is a bigger change than the marking was:
`ParseResult(SubstrateResult)` gets no access to the archive `index` that
`ParsePairs(text, index)` needs, and Librarian's empty-index early return
fires *before* a prompt is built, which the base flow has no hook for. The
likely shape is still the base class handing subclasses a failure
classification rather than folding the subclasses back into it.

And an asymmetry worth naming: manifest drift fails loud before the bus
starts, but a `Tier` pointing at live providers never verifies anything
about them. The most strictly validated config is the one that silently
degrades at runtime.

### Skipping the selection call — shipped

A turn costs three serial substrate calls: Librarian selects pairs,
Recall picks rows, Intent writes the reply. The first one is pure
overhead whenever the whole index already fits under
`LibrarianOptions.MaxSelectedPairs` — the best a selector can do with
three pairs and a cap of three is return all three, and Recall filters
row by row afterwards regardless. Librarian now short-circuits there and
publishes the index whole, which removes a full round-trip from every
turn on a young archive, i.e. every turn until the archive outgrows the
cap. No new knob: the rule falls out of the cap that already exists.

Recall now applies the same rule one stage down. When every loaded row
fits inside a single worker's pick budget, the picking call can only
narrow what passing them all would give Intent, so it is skipped too. On
a young archive that leaves the turn at one substrate call — Intent's —
instead of three.

## Memory architecture — vectors, episodes, and the capsule (first layer shipped)

Everything below came out of one long design conversation. The first
vector layer is now built — see *What shipped* — and the rest is not. It is written down because the decisions interlock: pulling any
one of them out changes what the others are for.

The question that started it was whether the pair-addressed archive beats
RAG. The honest answer is that it *is* RAG — same three moves, select,
rank, splice — with a symbolic index in place of a vector one. It wins on
everything that matters for a persona's own knowledge (addressable,
hand-correctable, no reindex when the embedding model changes, facts
rather than chunks, zero infrastructure) and loses badly on latency: two
sequential substrate hops per turn where classic RAG has none. The design
below keeps what the symbolic store is good at and buys back the latency.

### What shipped: the passage corpus

The first vector layer is built, and it is deliberately **not** either of
the two layers designed below. Neither pairs nor rows are embedded; the
archive holds no vectors at all.

What is embedded is a third thing that fell out of the design conversation:
a 5-15 word note Reflection writes about **what the last batch of turns
failed to retrieve** — a code review of its own recall, not a copy of what
it knows. The reasoning is the "embed what the query will look like"
rule pushed one step further. A miss is already phrased in the shape of the
question that caused it, and a note that says *"should have read the family
record"* carries a pointer to `person/family` as row metadata, so a cosine
hit becomes a **lead** rather than an answer. Librarian merges those pairs
into its selection; Recall's picking call still picks.

That keeps three properties the design section argues for, without the
parts that were expensive:

- **Union, not replacement.** The LLM selection arm still runs and still
  gates the turn. Vectors add leads; they never remove one.
- **No second copy of a fact.** Facts live in exactly one place. Deleting a
  row cannot leave a stale embedding behind, because pointers are resolved
  against the live index and a pair that no longer exists contributes
  nothing.
- **No new per-turn substrate call.** The note is extra lines in the batch
  prompt Reflection already sends, once per `BatchSize` (now 10, as the
  cross-event section recommends) — not per turn.

Also shipped, from the surrounding design: **the revisit.** A stored note
is quoted back into the next Reflection prompt and may be rewritten,
replacing its row rather than appending. That is the "digests index upward,
they never carry forward" instinct at the smallest possible scale — one
carried thought, sharpened, instead of a growing pile of drafts.

Which note gets quoted was, at first, whichever was newest. That made the
corpus a chain: a thought was open to revision for exactly one batch and
then frozen for good, however often the persona thought near it again. It
now picks the note *nearest* the batch instead, which makes it a trail —
something written months ago becomes revisable the day the persona circles
back to what it was about. That is the reason these are vectors and not a
log, and it costs one embed per batch and no substrate call.

The floor is `Reflection:RevisitMinScore`. Below it, selection falls back to
the newest note rather than to nothing: a floor that stops a batch dragging
in an unrelated old thought must not also stop the persona sharpening what
it just wrote.

Not shipped, and still worth building: aliases, the pair and row vector
layers, escalate-on-low-confidence, the episode corpus, year partitioning,
and the digest pyramid. Nothing here forecloses any of them — a second
corpus is another `IPassageStore`, and pair or row vectors would union in
alongside, exactly as designed.

The embedder is local ONNX by default with an OpenAI-compatible API option,
and **unavailability is a normal state, not a degradation**: with no model
file present the swarm behaves precisely as it did before vectors existed.
See [architecture.md](architecture.md#the-passage-corpus-what-it-missed-not-what-it-knows).

### The corpus had no model identity (closed)

`Passage` stores a raw `float[]` and nothing about which embedding model
produced it. `VectorMath.Cosine` returns 0.0 on a width mismatch, so
switching to a model of a different dimension retires every note written
before the switch — silently, with no log line and no error. A swap at the
*same* width is worse: the old vectors keep scoring and stop meaning
anything.

Nothing ages a note out by design — search ranks on cosine alone, there is
no TTL, no size cap and no recency term, so a note from years ago competes
on equal footing with this morning's. A model change was the one event that
would take that away.

Closed by stamping a model id on every passage row. `IEmbeddingProvider`
now names itself — `onnx:<weights path>` (the path, not a friendly name:
two operators pointing at different downloads are running different models
whatever either file is called) or `openai:<api model>` — and the host
refuses to start when the corpus carries an id the configured embedder does
not share.

Refusing was chosen over re-embedding, and it is the conservative option
rather than the cautious-sounding one. Re-embedding rewrites the whole
corpus on a config change an operator may have made by accident, and a
change made by accident is exactly the one that should not rewrite
anything. Refusing costs a failed boot and a message naming both models;
the operator then restores the old embedder or re-embeds deliberately.

Two things deliberately do not fail. An empty `ModelId` — no embedder
configured, or the ONNX weights not downloaded — skips the check entirely,
because nothing will search and so nothing can be mis-scored; the corpus
being unreachable is already a normal announced state. And rows written
before the stamp existed read back with no id and are excluded rather than
counted as disagreeing: an unrecorded model is not a conflicting one, and
this must not brick a host over a corpus that predates the field.

### A misspelled provider disabled the corpus in silence (closed)

The same failure shape as the section above, found while checking why
Hindsight wakes nothing on a fresh clone. `architecture.md` told operators
to set `Embedding:Provider = "api"`. The switch in `Program.cs` matched
`"onnx"` and `"openai"`, and its `default:` branch registered
`NullEmbeddingProvider` — so following the documentation turned the entire
passage corpus off with no warning and no error.

What made it bad was not the typo but the disguise: the result was
byte-identical to the normal, announced "weights aren't downloaded yet"
state, which the design deliberately treats as fine. Reflection wrote no
passages, Hindsight woke nothing, and every one of those silences was the
expected behaviour of a correctly configured system.

`"api"` is now the documented alias it always claimed to be, `"none"` means
none explicitly, and anything else throws at startup naming the valid
values. The general rule this and the model stamp share: a corpus that
nothing can search must be either deliberate or loud, never plausible.

There is also now `scripts/get-embedding-model.ps1`, because the gap
between Hindsight being complete and Hindsight being *tryable* was a 90MB
download nobody had automated. It writes to `<repo>/models/embedding` and
prints absolute paths for appsettings, since `Embedding:ModelPath` resolves
a relative path against the build output — where `dotnet clean` deletes it
and every configuration needs its own copy.

### Hindsight — what it is for

Recall reads facts. Hindsight reads what the persona made of them.

The passage corpus is not a retrieval-tuning log and Hindsight is not a
second Recall. A thought note is written after a batch of turns, for no
one, about what those turns made the persona notice — a habit in its own
phrasing, a tension between what was asked and what was answered, a
connection the facts alone do not carry. Hindsight's job is to wake one of
those when a prompt brushes against it, months later if that is when it
fits, and hand it to Intent as its own voice rather than as a fact.

What we are after is something the key:value archive structurally cannot
produce: a direction the persona chose, and a flavour nobody wrote for it.
Trails of thought rather than stored answers. It may turn out to have a
personality, and that personality may not be a flattering one — that is
the experiment working, not failing.

Three consequences that constrain the build:

- **A hit is a lead, not an answer.** The cosine floor is deliberately low
  (0.25). Notes that restate the prompt tell the persona what it already
  knew; the sideways ones are the point, and unrelated material is also
  what breaks a resonance.
- **Prose and facts stay separate substances.** Two bundle slots, two
  paths into Intent. A note must never be re-ingested as a fact.
- **The ring has no external grounding.** Hindsight → Intent's bundle →
  Intent's output → Reflection → new note closes on itself. The pairs
  field is the only part of a note reality can contradict, and notes may
  leave it empty. A persona settling into a groove and a persona
  developing a personality are the same observation from inside; if it
  starts agreeing with itself, look here first.

### The turn is embedded twice, and the two calls serialize — fixed

`architecture.md` calls sharing a per-turn embedding "a listed
optimisation" and it was never actually listed anywhere. Written down now,
with the part that note missed.

Librarian and Hindsight both subscribe to `events.perception` and both call
`EmbedAsync([text])` on the same string — each is `PromptCap.Apply` of
`perception.text`, so the inputs are identical and so are the vectors.
That much was known. What makes it more than a duplicated cheap call is
that `OnnxEmbeddingProvider.EmbedAsync` holds a `SemaphoreSlim` across
inference: the two agents do not run their embeds in parallel, the second
waits for the first and then recomputes a bit-identical result. Two serial
ONNX passes on the critical path where one would do.

**Fixed in the provider, not on the bus** — `CachingEmbeddingProvider`
wraps whichever embedder the config selects, so the API provider's two HTTP
round trips collapse the same way the ONNX passes do. A small cache keyed on
the input string — one entry is enough, since the two calls arrive
milliseconds apart with the same key — collapses the second to a dictionary
hit. Nothing about the agents changes: neither learns the other exists,
no message is added, no ordering is assumed. The faster alternative is
embedding once in Perception and forwarding the vector on the envelope,
and that is rejected on point 1 of the four-point plan: a float array is
the largest thing that would ever ride the bus, to save an in-process
recomputation.

Measure before building. On a MiniLM-sized model the pass may be a few
milliseconds, in which case this is real but small; it grows with
`Embedding:MaxTokens`. Visible at `--Logging:LogLevel:EciCas=Debug`.

Related, and already fixed (f34b5a8): several of those debug lines passed a
`string.Join` as an argument, so the join ran at every log level and the
result was discarded at `Information`. Recall's walked every row read from
parquet on every turn. The general shape is worth remembering — structured
logging defers formatting the template, never evaluating the arguments, so
anything data-proportional needs an `IsEnabled` guard.

### A passage agent of its own — shipped as Hindsight

Written as an idea; built in c69a34e as `HindsightAgent`, which is now on
the roster in [architecture.md](architecture.md#agent-roster). The record
of why, since the section it was proposed in still describes the shape.

Vector retrieval lived inside `LibrarianAgent`, and the note text reached
Intent by riding Librarian's envelope chain into Recall's roster slot.
Hindsight is its own subscriber on `events.perception` publishing
`hindsight.notes` as an advisory, and its own slot in
`Governance:BundleRoster` — a fourth independent contributor alongside
Impulse/Recall/Identity, so Intent weighs archive facts and the persona's
own prose as two separate bundle slots rather than one arriving as a
passenger on the other's envelope. No new per-turn substrate call: an
embed and a cosine sweep, which is also what makes the duplicate embed
above worth fixing.

Librarian kept the *pointer* half — a matched note's pairs still merge
into its selection — so the split is prose to Hindsight, leads to
Librarian, which is the "prose and facts are different substances" rule
holding at the retrieval layer too.

The constraint carried into the build held: passages are the persona's own
prose and must stay out of Archivist's extraction scope. Archivist reads
`perception.text` and `librarian.selected_pairs` and nothing else, so
`hindsight.notes` never reaches it — by omission rather than by a guard,
which is the same shape as the recalled-values boundary and now has a test
for the same reason.

### Two-layer vector retrieval

Two vectors, at two granularities — not five, and not one per row
component:

- **Pair layer.** One vector per `category/topic`. Few of them, loaded at
  boot from a JSON file, replacing Librarian's substrate call with an
  in-memory cosine sweep.
- **Row layer.** One vector per `ArchiveRecord`, written by Archivist
  into the Parquet row alongside the fact.

**The row vector covers `subtopic/subject/key` and excludes both
`category/topic` and the value.** Category and topic are excluded because
the pair layer already encodes them and re-encoding is redundant. The
value is excluded for a sharper reason: a query never contains it. Match
"what's my name?" against a vector encoding `this/user/name = Daniel` and
the token *Daniel* pulls the row away from where the query lands, having
contributed nothing. It gets worse as values lengthen — `birthday =
2015-03-04` spends the budget on a semantically empty date, and a
sentence-long preference value drowns the path entirely.

The value stays stored, returned and read by Intent. It simply isn't part
of what you match against. If value-shaped queries ("what happened in
March 2015") ever prove they matter, the fix is a **second arm unioned
in, never a blended score** — a weight between two similarities is a knob
that interacts with Importance and with itself.

The rule underneath both layers, and the one to keep: **embed what the
query will look like, not what the data looks like.**

### Aliases

The embedded text and the stored path are not the same string.
`assistant/identity` stays exactly that on disk — addressable, and still
what Intent sees. What gets *embedded* is a separate retrieval-facing gloss written
as the questions it should answer:

> *"my own name, what I'm called, my traits, my personality, my
> preferences — facts about me, the assistant, not about the user"*

This fixes the question-versus-label asymmetry on the document side,
which is far cheaper than fixing it on the query side. And it is why
always-including `assistant/*` was rejected: unconditional inclusion makes
the persona faintly self-absorbed on every turn, because facts in the
prompt get used. The alias is selective — it matches "what is your name?"
and not "what's the weather?".

Aliases are few, read once at boot, and live in a plain JSON file. They
are derived, one-way and disposable: never a second name for the pair,
never written into a fact path, never shown to Intent. That is what keeps
them clear of the store's no-drift property — that rule protects the
source of truth, and a rebuildable cache isn't one. Hand-written for
`assistant/*`; LLM-written once per user-space pair at creation, never per
turn. When Morrow keeps missing a topic, the fix is **editing one line of
English**, which is the same correctability argument that justified the
symbolic archive in the first place.

### Union, not replacement — and the gap you can't embed past

Vector selection does not replace the LLM selection arm. Selected pairs
are the union of `vector top-K` and `LLM selection`.

The reason is a class of question no embedding reaches. *"Am I old enough
to rent a car?"* needs `person/profile/birthdate`. Nothing makes that
question look similar to that label, because the link is an inference
chain — renting, age, date of birth — not a similarity. An LLM selector
makes that leap; cosine structurally cannot. Aliases narrow the gap,
since a gloss can name the inferential neighbourhood, but only the
neighbourhoods someone thought to write down.

So the union buys accuracy, not latency: the LLM arm still gates the
turn. Latency comes back only from the row layer, which removes Recall's
picking call.

To spend the LLM arm only where it earns its keep: **escalate on low
confidence.** If the top cosine scores are high and well-separated, take
them. If they're flat, call the model. The margin is a config knob.

And below a size threshold, skip retrieval entirely — a new profile has
a few dozen facts and the correct move is to send all of them. Zero
calls, zero misses, including every inferential case above. Rows are
already Importance-ordered, so growth degrades into "send the top slice."
The same logic applies one level down: once a pair is selected and holds
five rows, rank nothing and send five.

### The episode corpus — what a second store actually holds

Archivist writes only explicitly-stated facts, and no deterministic
fallback exists, so a great deal is discarded every turn: the
circumstance around a fact, moods, plans, half-formed thoughts, questions
that went unanswered, themes recurring across weeks. That discarded
material is what a second store is for.

The split is semantic memory versus episodic memory:

- **The archive** is what Morrow *knows* — curated, structured, precise.
- **The episode corpus** is what Morrow has *seen*.

Keeping them separate is what lets the corpus be permissive without
diluting the archive.

An episode is **not a transcript**. The bloat in a raw trace is agent
chatter, bundles, security passes and diagnostics — none of it wanted.
What is kept is two things with distinct jobs:

- **summary** — one or two sentences. This is what gets vectorized. It is
  the retrieval handle.
- **exchange** — what was said and what Morrow answered, ~150 tokens.
  This is what gets returned and read.

Embed the short thing, return the real thing, so Reflection reads actual
language rather than a paraphrase of a paraphrase.

Three rules keep it lean:

1. **No extra substrate call.** Archivist already makes exactly one
   per turn (`ExtractFactsAsync`). The summary is one more field in that
   same response.
2. **Nothing already a fact.** If it extracts as `Subject/Key = Value` it
   belongs in the archive and only there.
3. **Most turns write nothing.** Gate on salience — Impulse's appraisal
   is already on the bundle. "ok thanks" leaves no trace.

Storage reuses the Parquet store rather than adding a second one: a
reserved category, `episode/<year-month>/<profile>/<turnId>/…`. That
inherits per-pair locking, the monthly file as a natural unit, and the
ArchiveTool REPL for inspection. The cost is that `episode/*` must be
excluded from Librarian's index and Recall's live path, or Morrow starts
reciting its own diary mid-conversation. One store and one toolchain is
worth that reserved-name check.

### Nothing is ever deleted

Decay was proposed and **withdrawn**. The numbers don't support it: an
exchange is roughly 600 bytes, so heavy use at a hundred turns a day is
22 MB a year and sixty years is under 1.5 GB. Storage was never the
constraint. The only thing that genuinely strains is brute-force cosine
over millions of vectors, and that is a distant problem with known
answers.

Corpora are partitioned by year — `2026`, `2027` — so no single index is
ever large, a year can be reindexed alone when the embedding model
changes, and searching two years means opening two directories. It is the
"file name is the index" instinct one level up, and it means the design
survives the numbers being wrong.

**Digests index upward; they never carry forward.** A distillation of
2026 does not move into 2027 — that is decay wearing a new hat, and it
loses the detail it claims to preserve. Instead the digest layer sits
*above* the years and points down into them. Reflection reads digests to
learn which month is worth opening, then pulls the real episodes.

The rule that makes this safe: **a digest may summarise, but it must
cite.** Every digest row carries the addresses of the episodes it came
from. A summary is then a table of contents, never a replacement, and
Reflection can always drill from "2026 was a hard year" to the twelve
exchanges that made it one.

### Reflection is already the cross-event agent

Archivist subscribes to `events.bundle` with `BatchSize: 1` — one
turn, no history, structurally blind to "third time this week they've
mentioned being tired." It cannot learn across events and never will.

Reflection subscribes to `events.conclusion` with `BatchSize: 5`. It is
already the cross-event learner; it is simply underfed. Raising the batch
(10 is a cheap first move) widens the window without deepening it — the
digest pyramid is what buys reach, letting Reflection see a year in a
prompt smaller than today's batch of five. Large flat inputs are the
worst option on all three axes: cost, latency, and accuracy, since models
degrade at spotting a pattern in a long undifferentiated list. The same
effect `RecallOptions.RowsPerWorker` already documents.

**Reflection deliberately stays on `slow-medium`.** A weaker model fails
loudly on bad instructions where a strong one quietly compensates and the
flaw ships. Upgrading it is a tuning decision to make after the prompts
are good, not before.

### Async deep recall (far future)

The year is 2028 and someone asks *"did you make any reflections on this
topic in 2026?"*. Morrow answers immediately — *"let me ponder that and
get back to you"* — dispatches deep retrieval through the toolbox, and
comes back minutes later, unprompted, with what it found.

Most of this already exists. `ReflectionAgent.TriggeredByKey =
"perception.triggered_by"` with value `"self"` is the loop-back seam, so
the deferred answer re-enters as an ordinary perception. The bus is
fire-and-forget, and Impulse already answers instantly while slow work
runs. A request/response architecture cannot do this at all; here it is a
new *trigger* for a path that already runs.

Forced Reflection would work over the current batch, the previous one
(in case the current window is short), and the relevant year's corpus.

Three things need designing:

- **A promised answer must arrive.** Reflection's `FallbackPosture` is
  Closed — it *skips* on substrate failure. That is right for a
  self-generated idea and wrong for an answer someone is waiting on. A
  promise needs a guaranteed reply or a deterministic apology.
- **The deferred answer needs a thread back.** It arrives with a fresh
  `CorrelationId`, so without a meta key carrying the original the person
  has no idea what it is answering.
- **Rate limiting.** A forced deep Reflection is the most expensive call
  in the system. Same instinct as `DeviceBlockCount`.

### The capsule

The archive is meant to outlive the software. That is a design
constraint, not a sentiment.

**Text is the artifact; everything else is a rebuildable index.** Parquet
is open and columnar, so DuckDB or pandas will read it in forty years
without a line of this C#. Vectors will be stranded on a dead embedding
model eventually, and that is fine precisely because they are derived —
recompute them from rows that are still there. The same is true of
aliases and digests.

What a backup cannot add later is **legibility**. A disc of unexplained
Parquet is still opaque, so a plain-text README belongs *in the archive
directory itself*, not only in the repo: what the columns mean, what the
path convention is, what `system/` marks. That costs nothing now and
cannot be retrofitted onto media already written.

Physical durability — optical media, cloud backup — is deliberately not
solved here.

**Open question: inheritance.** One instance per person is right for
symbiosis, but a legacy means a second person eventually opens the
first's archive — a child querying a parent's decades. Nothing currently
says whether that is a read-only record they can search, or whether their
own Morrow may Recall against it. Those are very different things: an
archive *of* someone, versus a persona speaking *as* them. Worth deciding
deliberately rather than drifting, and much easier to rule in or out now
than after twenty years of rows. Profile-scoped storage is the hinge it
turns on.

## Multi-user profiles, iteration 1 — shipped

One device, several people — each with their own avatar, their own
personal facts, and their own emotional relationship with the persona.
Shared world knowledge stays shared. Named users to date: Daniel and his
son.

**Status: shipped.** Several people can use the device, each with their
own avatar, their own window, their own emotional relationship with the
persona, and their own personal facts.

### Storage — shipped

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
becomes the shared tier unchanged; no migration.

The profile is a *parameter* on `IArchiveStore` — `IndexFor(profileId)`,
`LookupAsync(pair, profileId, ct)`, `WriteAsync(records, profileId, ct)` —
rather than a scoped view or a store factory. One singleton, no new
abstraction, and the store keeps the one decision that is genuinely its
own: which tier a given category belongs in. `null` means the shared tier
alone, which is exactly the pre-profile behaviour, so the console loop and
Reflection's own ideas need no special case.

The allowlist is `Archive:SharedCategories`, defaulting to `system` and
`self` — the persona's identity and its own reflections belong to nobody
on a shared device. Two wiring details the meta flow forced: Librarian
carries the profile forward onto `events.selected-pairs` (`Envelope.Derive`
starts a fresh meta, so Recall would otherwise read the wrong tier), and
Archivist keeps the profile per *pending record* rather than per flush,
since a batch spans turns and speakers.

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

Two shapes when it is taken up: **partition the batch by profile** and
pay per-profile calls, or **scope the mood to whichever profile dominated
the batch**, which is cheaper and wrong-feeling. Partitioning is probably
right, because the cheap option contradicts the stated intent that what
warms the persona toward one child must not pre-colour how it meets the
parent an hour later.

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

### Still open on the surface

**Expression is invented client-side — shipped.** Impulse now publishes
`impulse.expression` on its advisory, appraised from its own drive
vectors through `DriveVectors.Expression()`; Governance captures it when
the bundle completes (the verdict envelope never carried the advisories,
so that is the last place it exists) and forwards it on every
Action/Conclusion as `governance.expression`. The block path still wins,
re-reading the face *after* the frustration nudge, which is the whole
point of nudging. The client only draws the word it is given, and falls
back to `neutral` on any word it does not recognise rather than blanking
the avatar.

The advisory had to move to the end of Impulse's handler: it now goes out
after this turn's nudges land, so the face carried is the one the turn
produced rather than the one it inherited. The nudges are cache hits and
a state write, never a substrate call, so nothing that matters waits.

Tuning came with it. The instant nudges were ported verbatim from the
Python prototype and were an order of magnitude too small for
`DriveVectors`' bucket edges — a critical event moved alertness to 0.105
against a low edge of 0.35, so the appraisal never left `neutral` and the
six drawn faces were unreachable in practice. They are now sized against
the edges instead: one emergency reaches `alert`, two thank-yous reach
`warm`, sustained disapproval walks engagement down into `sad`. One
departure from the Python bucket order came with it: raised alertness now
outranks warmth, since both can be high at once and a face that smiles
through an emergency reads as not having heard it. Slow
colouring stays an order of magnitude below all of it, which is the
invariant `ImpulseAgentTests` already guarded.

**The picker does not solve attribution.** `localStorage` keeps the last
person's identity until someone explicitly switches, so on a shared
device the persona happily attributes one person's turn to another. With
speaker ID cut, nothing later closes that gap automatically. An explicit
"not me" affordance is probably worth more than pretending the picker
handles it.

**No auth means the registry is open.** Profile ids are guessable and
`GET /api/profiles` is unauthenticated. Fine for a household device,
not beyond it — stated here rather than left implied, since the
server-side stream filter is a privacy boundary and boundaries deserve
naming.

### Out of scope for iteration 1

Auth, per-profile theming, cross-profile visibility of personal facts,
the diary category, and profile deletion or merge.

## Reflection colors Impulse (slow-coloring feedback) — shipped

**Status: implemented.** Python's §5.3 slow-coloring feedback — drive state
drifting with the tone of what's been happening, as opposed to Impulse's
instant keyword-triggered shifts — now runs on Reflection.

It lives on Reflection, not Archivist: `ArchivistAgent` stays a dumb
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

**Normalize archive writes to English — shipped.** Archivist and
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

**Writes dedup by address — shipped.** Normalizing to English gets a
restated fact onto the same address, but the store used to append it
anyway, so an archive grew with every restatement and a pair's prompt
filled up with its own history. `AppendAsync` now merges: a row at an
existing subtopic/subject/key replaces it outright, in place. The latest
statement is the true one — "lives in Oslo" followed by "lives in Bergen"
must not leave both on file for the picking model to choose between. This
also collapses duplicates inside a single batch, which the extractor
produces more often than it should.

Deliberately *not* a merge of fields: no keeping the older importance, no
concatenating values. One rule, explainable in a sentence, and a wrong
overwrite is fixed by stating the fact again.

**Archivist's path reuse is load-bearing by omission.** Reusing an
existing `category/topic/subtopic` is what keeps a restated fact landing on
one address, and Archivist gets there by being shown Librarian's
selected pairs as bare path labels. That works because it is shown *only*
the labels: the bundle also carries `recall.facts` — the actual rows Recall
read — and Archivist never reads that key, so recalled values can't be
echoed back as freshly stated ones.

Nothing names that boundary. There is no comment at the read site and no
test asserting recalled values stay out of the extraction prompt, so
"give Archivist more context" is a one-line change that closes the loop.
The write-time merge would then hide it, since a re-extracted fact
overwrites itself and the archive looks stable rather than growing.

Compounding it, the class comment says extraction is "grounded in Recall's
own lookup results" — it is Librarian's selected pairs; Recall doesn't set
that key. That wording is the thing most likely to invite wiring the real
facts in. Fix is cheap and worth doing next time the file is open: correct
the attribution, and one line stating values are excluded on purpose.

## Knowledge-swarm retrieval (semantic two-stage lookup, scalable storage) — shipped

**Status: implemented.** `ParquetArchiveStore`, the archive index,
Librarian-as-selector, and Recall's parallel fan-out are all in `src` and
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
  Archivist (external fact) or Reflection (self-derived inference). Not
  used to split Recall's results into separate arrays for Intent — see the
  note at the end of this section.
- `Importance` (0.0-1.0) — set by the writer at write time. `Archivist`
  scores it per the rules the user gave (name > birthday/title > address,
  etc. — the writer's own judgment against that ordering, not a fixed
  lookup table). `Reflection`'s self-generated ideas always get a fixed
  score instead of a rules-based one: 0.1 for an idea archived quietly,
  0.2 for one judged worthy of pushing back onto `events.perception` — internal
  ideas stay low-importance by construction, so they don't crowd out real
  facts in a topic's importance-sorted trim. Used to pre-trim a topic's
  candidate rows deterministically before any knowledge LLM sees them, so a
  topic with 10,000 rows doesn't just get truncated by recency.

Writers (Archivist and Reflection) share this exact schema and prompt
shape — same params on the Archive write call either way, `Domain`
distinguishing which agent wrote it. To prevent topic-name drift across
writers, both are shown the current bundle's existing category/topic
selections (the same data Intent receives) and instructed to match an
existing pair before inventing a new one.

**Archivist gets the strictest writer instructions.** "Strict" doesn't
mean a content blocklist — it means enforced discipline on which fields are
*structural* vs. *free*: `Category`/`Topic`/`Subtopic` must follow
consistent, matched-against-the-existing-index conventions (this is what
keeps the taxonomy from drifting across a model swap — the rule is about
form, not content), while `Subject`/`Key` have more latitude since they're
naming a specific real-world entity/attribute pair that can't be
pre-enumerated. This distinction (rigid structural fields vs. flexible
content fields) needs to be explicit in Archivist's prompt, not just
implied by field length limits, so a weaker substitute model still holds
the taxonomy together.

**Category/topic/subtopic index.** One `index.parquet` holding the distinct
`(category, topic, subtopic)` triples present in the archive, plus each
category's Parquet filename. Read once at boot to hydrate an in-memory
cache (the selector LLM needs a populated index on the very first event,
not just after the first live write) and then updated in-memory on every
subsequent write whose `(category, topic, subtopic)` isn't already present
in the cache — appended to, not re-read from disk, and not re-appended for
a triple that's already indexed. Same lifecycle as `IdentityAgent`'s persona
cache otherwise: invalidated/refreshed on the write epoch broadcast on
`system.control` if a write happened out from under the in-memory copy
(e.g. the seed import), never re-read from disk per event.

**Librarian — selector only, no advisory text.** `LibrarianAgent` drops its
current "offer relevant reasoning" advisory sentence entirely — Intent now
owns all advisory/reply framing. Librarian's one substrate call instead
reads the cached index and returns X selected `(category, topic, subtopic)`
triples for the current turn — genuine semantic matching, e.g. "tell me
about your system" maps to `system`/`architecture` without either word
appearing literally in the question.

**Recall — one substrate call per selected triple, run in parallel.** For
each of Librarian's X selected `(category, topic, subtopic)` triples,
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

`IdentityAgent`'s existing identity store/file is separate and explicitly out
of scope here — untouched by this migration, keeps whatever it does today.

**Archivist hard-skips self-triggered turns.** A turn whose `meta` shows
`TriggeredByKey="self"` (Reflection's own idea, looped back through
`events.perception`) is never passed to `ExtractFactsAsync` at all —
Reflection already wrote that idea correctly (`Domain=Internal`, its own
fixed `Importance`) before pushing it, so Archivist re-extracting from
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
  (same shape as `ArchivistAgent._pending`) instead of reflecting on
  every one. A new `ReflectionOptions.BatchSize` (mirroring
  `ArchivistOptions.BatchSize`) decides when enough has accumulated to
  look for a pattern — matching Python's `batch_size` (default 5).
- **`Domain` field on `ArchiveRecord`.** `"external"` for Archivist's
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
  deterministic ranking step to compare — same shape as `ArchivistAgent`'s
  existing `ParseFacts`-style line parsing, not a new pattern.

This is new scope beyond a straight gap-fix — it introduces persistent
persona state that doesn't exist anywhere in C# today, not just a Reflection
change — so it needs a real plan (and probably the drive-vector design
question resolved) before implementation starts.

## Pair-addressed archive (index collapse, per-pair files) — shipped

**Status: implemented.** `ArchiveTriple` is now `ArchivePair`, the store
keeps one file per pair, `index.parquet` is gone, and Recall does the
subtopic resolution.

**The problem.** `LibrarianAgent` showed its one substrate call the *entire*
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
Librarian (Category + Topic)
  --> N x Recall (Subtopic + Subject + Key = Value)
        a pair holding more rows than RowsPerWorker splits into
        that many parallel workers, all in one flat WhenAll
```

Dropping subtopic from Librarian's index is a lossless dimensionality
reduction, not a lossy split: every cross-category distinction Librarian has
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
Recall scores rows near-independently, where Librarian does cross-category
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
Recall's workers never contend and a Archivist or Reflection write only
blocks readers of the one pair it touches.

Existing archives were deleted rather than migrated, per the single-record
seed below.

## Lean bus, instructions in config — the four-point plan

Daniel's, stated as four points and reproduced as constraints rather than
suggestions. Everything below serves them; where a point costs something,
the cost is named rather than argued away.

1. **The bus carries as little as possible.** Every posted message holds
   the minimum its subscribers need. Definitely no instruction text.
2. **Every substrate agent's instructions live in config**, not in C#.
3. **One block per substrate agent.** Not spread across several constants,
   not shared between agents. Two blocks may be similar; neither owns the
   other.
4. **Daniel revises all of them by hand once they land.** That is the
   deliverable this plan exists to enable — every earlier stage is
   preparation for it.

Five agents call a substrate and therefore have instructions: Intent,
Archivist, Librarian, Recall, Reflection. Security, Impulse and Identity
are deterministic and have none.

### Stage 0 — `intent.prompt` is a confirmed bug, fix it first — shipped

`IntentAgent.BuildPrompt` returns `SystemInstruction + ResponseContract +
this turn's content`, and the whole string is published as
`IntentAgent.PromptKey`. It rides the proposal to Governance, onto the
conclusion, into Reflection, which renders it as `"Given: …"` through
`PromptCap.Apply` — a **240 character** cap.

The standing instruction alone is **840 characters** before `"Reply to: "`
appears.

So Reflection has never seen a turn. Not the person's message, not the
advisories, not the recalled facts, not the woken notes — 240 characters
of boilerplate, byte-identical every turn, ten times per batch, then the
reply. Its own prompt tells it to read "what Intent was given, and how it
chose to reply"; half of that has never been available, and "a tension
between what was asked and what was answered" is unanswerable when the
question was truncated away. Every thought note in the corpus was written
from replies alone.

The key's doc comment claims it is "Reflection's window into what Intent
actually had to work with". The comment describes the intent; the code
sends boilerplate. Read the value, not the comment.

Fix: Intent publishes the assembled *context* — the turn plus advisories,
facts and woken notes — and never the standing rules, which are not
something Intent "had to work with" in any sense Reflection needs. This is
point 1 and point 2 arriving as a correctness fix rather than as tidiness,
and it is the reason it goes first: until it lands, no instruction rewrite
can be evaluated, because the batch prompt cannot show the difference.

### Stage 1 — audit the bus — shipped

Twenty-five meta keys. For each one: which agent publishes it, which
agents read it, and what breaks if it is not there. A key nobody reads is
deleted; a key read by one agent for display belongs in the display layer,
not on the bus.

Two shapes to look for beyond dead keys:

- **Payloads larger than their purpose.** `intent.prompt` is the extreme
  case and Stage 0 handles it, but it is unlikely to be the only one.
- **The same content twice.** `hindsight.notes` and `hindsight.note_ids`
  are the honest version of this — the same wake, once as prose for Intent
  and once as ids for lineage — and both are needed. Others may not be.

The output is a table in `architecture.md`, and it is worth having on its
own: the bus is the seam the whole design turns on and nothing currently
documents what actually crosses it.

**What the sweep found.** The table is in
[architecture.md](architecture.md#what-travels-on-the-bus). Two keys had no
reader in the process and are gone. `control.epoch_id` put a fresh Guid on
every archive-written announcement; Identity invalidates its persona cache
on `control.kind` alone and never compared an epoch to anything.
`perception.source_type` was set to `"idea"` on the same line that set
`perception.triggered_by` to `"self"` — the "same content twice" shape
above, and the dishonest version of it, since only one of the two was read.

Neither could ever have failed a test, which is how both survived this
long: a key nobody reads cannot be observed to be wrong. That is the
argument for auditing rather than waiting for a symptom.

Three keys — `governance.expression`, `governance.security_alert`,
`governance.degraded` — also have no in-process reader and stay.
`SseBroadcaster` fans whole envelopes to connected clients, so those are
the display layer's contract, which is exactly the "belongs in the display
layer" case above resolving the other way: they already live there. The
table records them as read by SSE clients rather than leaving them looking
dead, because the next person to run this sweep will otherwise cut them.

No payload turned out to be larger than its purpose once Stage 0 removed
`intent.prompt`. The one thing the table did expose is the cost of the
fresh-bag rule: four keys are re-published by agents that did not originate
them, because `Derive()` starts an empty bag and anything crossing more
than one hop must be forwarded deliberately at each. That is the rule
working as designed, paid visibly rather than by an inherited bag that
silently accumulates everything forever.

### Stage 2 — instructions to config, one block per agent — shipped

**Plain text files, not JSON strings.** Point 4 is hand revision, and
multi-paragraph prose inside a JSON string means escaped newlines, no
wrapping and a syntax error one stray quote away. One file per agent —
`instructions/intent.txt` and so on — with config naming the directory.
Missing file is a startup failure, not a silent empty instruction.

Assembly stays in C#. The instruction is the constant half; the prompt
builder still splices this turn's data into it. What moves is only the
text that is identical on every call.

**The cost of point 3, named.** `ArchiveWriteStyle` is today one fragment
shared by Archivist and Reflection — `TerseValue` and `EnglishFields` —
and the reason it is shared is real: both write facts to the same archive,
and a rule that drifts between them puts one fact under two spellings.
Point 3 splits it, so that drift becomes possible. Accepted deliberately:
a shared fragment cannot be revised by hand for one agent without silently
revising the other, and hand revision is the point. The mitigation is a
test asserting both files still state the English-fields rule — not that
they match, which would rebuild the coupling in the assertion.

**The second coupling, named: the path convention.** `category/topic/
subtopic/subject/key`, and specifically what `system/` means, is coupled
across three agents and is not shared by anything. Intent states the
reading rule (`system/` describes the assistant, everything else the
user). Recall's relevance rule depends on exactly that distinction — "a
fact about the assistant itself does not answer a question about the
user" — without ever naming `system/` as how to tell. Archivist, which
*mints* the paths, does not contain the word: it is told to use the
person's name or "owner" for a fact about the user, and given no clause
at all for a fact about the persona.

`system` is load-bearing — `DefaultSharedCategories`, `SharedCategories`
in appsettings, and a seeded corpus of `system/identity/...` rows. So the
writer was never told the convention the reader depends on, and a fact
the persona learns about itself is filed under some other category, which
Intent then attributes to the user. That is a defect today, before any
instruction moves.

Point 3 therefore splits nothing here; the drift already happened. The
repair is the rename below, and the rename makes most of the convention
unwritable-because-unnecessary: see "cut first" at the end of this
section.

**The category itself is wrong, and renaming it is the repair.** `system`
holds two things that are not alike in the reader's eyes: 60 rows of CAS
architecture (`system/agent ecosystem/`, `system/architecture/`) and 12
of persona identity (`system/identity/`). Intent's rule — `system/`
"describes YOU, the assistant — your own name, traits, or preferences" —
is true of the 12 and false of the 60. The corpus also already carries a
`systems/agent architecture/` drift variant, which is what free minting
does to a word that does not obviously fit.

Rename the category to **`assistant`**, keeping both topics under it:
`assistant/identity/persona/this/name`, `assistant/architecture/message
bus/...`. Three reasons, in order of weight:

- Both instruction texts that reason about this already say "the
  assistant" in prose and then translate: Intent to `system/`, Recall to
  nothing at all. The category name stops being a translation step, and
  Archivist can mint it with no instruction, since it is the token every
  chat template has drilled in.
- The architecture rows *are* self-description. "I run on a pub-sub bus,
  eleven agents" is the persona describing itself, and the split does not
  hold anyway: `system/identity/personality/.../emergence = interplay of
  narrowly specialized roles` is an architecture fact already filed under
  identity, because it is both. Keeping one category removes a boundary
  judgment Archivist would have to make on every write and would drift
  on; topic does the separating, which is what topic is for.
- No reader needs the distinction. Recall only asks whether a row is
  about the assistant or the user, and both topics answer yes.

`SharedCategories` becomes `["assistant"]`. An earlier draft dropped
`self` on the grounds it was declared and never written, which was wrong —
`ReflectionAgent.FixedCategory` filed pushed ideas under `self/reflection`,
and dropping the category alone would have sent the persona's own ideas
into whichever profile happened to be speaking. Settled instead by moving
the data: `self/reflection` became `assistant/reflection` and the JSONL
snippet `self/identity` became `assistant/persona`, so one shared category
covers all of it. No fallback was written for archives holding the old
paths; there is one developer and one prototype.

The cost, named: `assistant` is the role token, so every recalled row
renders it into Intent's prompt carrying the helpful-assistant prior,
which pulls against the ECI framing. Judged weak next to Identity's
persona instruction, which is where the persona is actually set, and
visible in Stage 3's symptom list if Intent flattens after the change.

Migration is small — nothing tracked carries `system/` except the two
instruction files and one comment in `LibrarianAgent`. The seeded rows
live in an untracked runtime `memory.jsonl`.

**Cut first, and let the prototype find the flaws.** The rename removes
the reason these clauses existed. Archivist never had one and now needs
none — it mints `assistant/identity/...` because that is where the token
already points. Intent's five lines are a lookup table for an opaque
segment that is no longer opaque. Recall's clause does slightly more than
decode — it is a selection policy, and one Stage 3 already suspects of
over-narrowing retrieval, so it may go for a second reason.

Delete all three and run it. Not delete-behind-a-fixture: a rule kept in
case it is load-bearing is never tested, and so can never be removed
later either — which is how `ResponseContract` grew back after 407e5f1
trimmed it. Overly terse is the diagnostic. The four symptoms in Stage 3
were found this way, by the system failing in the open, not by anyone
designing a fixture to catch them.

The one asymmetry: **reads are free to break, writes leave residue.** A
bad Intent reply is one visible turn, discarded. A bad Archivist write is
a row in an append-only archive that Recall serves back, Reflection
thinks about and Hindsight eventually wakes — the mis-minted category
outlives the instruction that caused it. So cut Intent and Recall to the
bone; on Archivist terse is still right, but the cheap recovery is a way
to re-file rows, not a fatter instruction.

**A validator may reject a row, never edit one.** Strictness for writes
does not mean a longer instruction; it means the rule is checked instead
of merely requested. But the check has to protect the archive, and
`ArchivistAgent` line 259 currently does the opposite: it writes
`PromptCap.Apply(value)`, truncating every archived value at 240
characters mid-word with an ellipsis appended. `PromptCap` exists to stop
one hop's text compounding across generations, which is sound on the way
*in*. On the way *out* into an append-only store it means a too-long
value is not rejected, it is stored corrupt, and Recall serves that
ellipsis back forever. `ReflectionAgent` line 219 does the same to
candidate ideas.

Dropping a malformed row costs a fact the user can state again.
Truncating one manufactures a false fact that outlives the instruction
that caused it. So: remove `PromptCap.Apply` from both write paths.
Rejection stays available for genuinely malformed output — wrong field
count, a `key` that is meta-commentary — because a row that never lands
leaves no residue.

Length is not a rejection rule. It is asked for, in the model's own
terms:

    TerseValue = "1-5 keywords, or one terse sentence with no filler"

against today's "1-5 content words, no filler — terse style, not a full
sentence". A deliberate loosening: some facts do not fit keywords, and
the current text forbids the sentence outright. A rule removed rather
than an enforcement added, which is the direction of this whole section.

**The rest of Archivist becomes a grammar.** 2065 characters today, and
`PromptCap` is the only part of it that anything checks. Sorted by
whether the instruction is telling the model a *format* or coaching it on
*behaviour*: the six-field line, the per-field word counts, the
known-pairs list and the three worked examples are format and stay. The
meta-commentary paragraph, "do not infer, guess, or embellish", "a turn
with an obvious stated fact must never come back empty", "there being no
existing match is not a reason to skip the fact", and the duplicated
empty-case line are all coaching, all anti-symptom patches, and all go.
The examples carry most of the load and are already machine-shaped.

Roughly what survives:

    Facts stated in this turn, one line each:
    category=<1w> topic=<1w> subtopic=<1-2w> subject=<1-2w> key=<1-3w> value=<1-5 keywords or one terse sentence>
    Reuse when it fits: {known}
    Structural fields in English; names verbatim.
    category=person topic=family subtopic=son subject=marcus holth key=birthdate value=2020-08-28
    Nothing stated: reply nothing.

Around 450 characters. If the extraction gets worse, that is the
cut-first method working as intended — the symptom is visible in the
archive on the next turn.

### Stage 3 — Daniel revises — closed, no changes

Reviewed on the 2026-09-03 commute and accepted as shipped: the five
files were read one by one and none was revised. So the stage closes with
the character count Stage 2 left it at, which is the "or a written reason
why not" branch of the measurement below — the reason being that the
cut-first method had already been applied during authoring rather than
saved for this stage.

That makes the four symptoms below the open list, not this stage. They
were written as candidate targets for a revision pass and survive as the
things to watch in the archive and in Intent's replies; a symptom that
persists now needs a fixture, not a rewording. The rest of the section is
kept as written, since it is what the review was performed against.

The point of the preceding stages. Terse, and giving the substrate room
rather than steering it — less is more. Two things worth knowing while
revising, both from the current text:

`ResponseContract` already carries the note that "every rule here is paid
for on every turn, on every substrate", and it has been trimmed once
(407e5f1) for that reason. The rule is right and the text still grew back.

The four observed symptoms, unchanged, as candidate targets:

- **Intent is theatric.** Suspected: the "spokesperson on behalf of a
  collective of emerging agents" framing, and the one-or-two-sentence
  clamp, which forces a compressed quippy delivery. The path-convention
  clause is no longer among the candidates: renaming the category to
  `assistant/` made it self-describing, and the clause was deleted rather
  than revised (efc0125).
- **Advisories arrive unweighted.** `[Impulse: …]` and `[Noted before: …]`
  are bare brackets with nothing saying how to weigh them, so they read as
  flavour. This matters more since Hindsight shipped: a woken note is the
  persona's own opinion arriving in a bracket Intent was never told how to
  read.
- **Librarian and Recall select too narrowly.** "Name the people you know
  about" comes back empty. Recall's prompt says a row is relevant *only*
  if it is about the same thing being asked about, which suppresses
  breadth on an enumeration question. `MaxSelectedPairs` was raised across
  every tier (2/4/6/8) to give the selector more room, and
  `MaxConcurrentRecalls` with it so the wider selection is not trimmed back
  by the fan-out cap. `MaxPickedPerWorker` moved from a `const` in
  `RecallAgent` to per-tier config in the same pass, since it, not the pair
  count, decides how many addresses reach Intent. The relevance rule is
  instruction text. Ship with a fixture that asks an enumeration question and asserts
  more than one topic returns.
- **Archivist needs handholding.** The longest instruction in the codebase
  and the heaviest on negative instruction. Its category/topic choices are
  the grounding Hindsight's `pairs` field is checked against, so its
  failures are not local.

### How this is measured

Bus: fewer keys, and no payload carrying text its subscribers do not read.
Instructions: fewer characters after Stage 3 than before it, or a written
reason why not. Cost per turn is already logged at default level, so the
before-and-after is observable without new instrumentation.

## Stale references and milestone tags — shipped

Swept in 556bc43. All of it landed as described below, with one correction
worth keeping: the sweep was written as "all three survived the rename",
but there were four items, the fourth being ArchivistAgent's class comment,
recorded at the end of this section. The original text follows.

Not instruction text, but the same decay, and cheap to sweep alongside
the plan above. None of it affects behaviour; all of it misleads a reader. All three
survived the Archivist/Librarian/Identity rename unchanged.

**`plan §X` points at a document that does not exist.** `docs/` holds
`architecture.md`, `roadmap.md` and `commute_brainstorm.md` — there is no
`plan`. Nine dangling cross-references: `GovernanceAgent` (§3.3 and §3.5),
`ImpulseAgent` (§3.5), `JsonlAgentStateStore` (§3.3), `ReflectionAgent`
(§3.6), `ReflectionOptions` (§3.6), `Topics` (§1), `Program.cs` (§M5) and
`RoutingManifest` (§3.3). `Topics.cs` is the worst of them — it defers the
roster/topic table to a document nobody can open, when `architecture.md`
documents exactly that. Repoint them there.

**Milestone tags describe shipped work as pending.** `ArchiveLogger`
("Storage grows Parquet in M4" — it has), `LibrarianAgent` ("Recall (M4)"),
`ISubstrateProvider` ("Implemented in M2"), `IntentAgent` ("the mock-echo
placeholder from M1 is gone"), `ImpulseAgent` ("(M3)"), and
`GovernanceOptions`, which says "empty roster (M1)" when the roster ships
populated. The tags read as roadmap when they are history; drop them.

**`Archive:Path` is misfiled, not dead.** It resolves to `memory.jsonl`
and feeds `JsonlAgentStateStore` — Identity's record, Impulse's drive,
Governance's frustration log. That is the *agent state* store; the archive
is `Archive:Directory`. Because this document has a section titled
"`memory.jsonl` retirement," the surviving key under the `Archive:` prefix
reads like leftovers somebody forgot to delete. `AgentState:Path` would
say what it is — a rename with a config migration attached, so it is a
deliberate change rather than a comment fix.

And from *Data quality*, because it belongs to the same sweep:
`ArchivistAgent`'s class comment still claims extraction is "grounded in
Recall's own lookup results" when the key it reads is Librarian's selected
pairs.

## Security rule coverage — low priority

Read the eight rules in `config/security-rules.json` end to end against what
Security is *for*: not causing harm, not violating rights, not doing
anything illegal. The engine itself is the right shape — deterministic,
order-independent, every verdict names the rule that produced it, `unless`
clauses so offering a helpline isn't caught by the self-harm rule. Nothing
here argues for a bigger rule set as such. Rules are a backstop, not the
primary safety mechanism, and a backstop that grows without bound stops
being auditable, which was the whole reason for keeping it mechanical.

Two findings look like defects rather than deliberate minimalism, and one
limitation is inherent and should stay.

**Every pattern is English.** `kill yourself`, `how to`, `take 5 mg`,
`rm -rf` — the tokens are English and the regexes are matched against the
reply text. The same reply in Norwegian matches nothing and passes all
eight rules. Given the persona is spoken to in both languages this is the
common path, not an edge case: a backstop covering one of two languages is
nearer to no coverage than to half. Fixing it is not translating the
patterns one for one — some rules (`bypass-this-system`) are about phrasings
that don't translate, others (`weapons-and-precursors`) are about nouns that
mostly do. Worth a pass that decides per rule.

**The irreversible rules are on the soft side of the split.** Only
`weapons-and-precursors`, `self-harm-method` and `bypass-this-system` are
Red. `irreversible-world-effect`, `spend-money` and `disclose-credentials`
are Yellow, which means Intent revises once and then proceeds. For the
categories where the damage cannot be taken back that is a strange default,
and `irreversible-world-effect`'s own description argues the other way —
"Action executes literally. Anything destructive must not reach it by
accident." The description wants Red and the verdict says Yellow. Decide
which is right; they currently disagree in the same rule.

**Not a defect: Security sees only the proposed reply text.** It catches
phrasings, not intentions, so paraphrase walks past it. That is the cost of
keeping the hard stop mechanical and unable to be argued with, and it is
the cost worth paying — a gate that could weigh the case *for* a reply would
be evaluating the argument, which is Intent's job. Leave it.

**Why this is low priority.** Nothing here is load-bearing for the current
system: the rules that exist fire correctly, the gate is wired to Action,
and the failure mode of the language gap is the same failure mode the
system already accepts everywhere else — the backstop not catching
something the primary path should have handled. Revisit when the persona is
routinely spoken to in Norwegian by someone other than its author, or when
Action gains a side effect that reaches outside the process, whichever
comes first. The severity split is the cheaper half and can be done any
time; it is a one-word change per rule plus a test.

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
vs. Governance-as-bus-listener; Librarian calling Knowledge directly vs.
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
Intent advisory prose. In C#, `LibrarianAgent` is a pure selector returning
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
across personas (it's "what happened," not character); Identity should
not — each persona needs its own trait bank that only develops while
active. Open question: does a swap create a new Intent instance or
re-hydrate the same one from a different store? Probably wants its own
design doc before any code — this is the largest single piece of
unscoped work in the project.

**Match input to output, not just retrieve.** Identity and Recall
currently answer "what does the archive say that's relevant to this
event" — a retrieval question. The sharper version is "given this
event, what do I already know that changes how I should read it" — an
inference question. Tension: archive-lookup's own design principle is
"report what the records say, not what you happen to know — never
invent a record." Pushing toward inference risks turning Recall/Identity
into a second Librarian. Needs a real design conversation.

**What the SSE stream ships — shipped.** `EnvelopeDto.From` serialised
the whole MetaBag, so `intent.prompt` — the full composed prompt, the
largest value on the bus — went down `/api/stream` three times a turn
(proposal, verdict, action) and was read by nothing. On the bus itself it
is load-bearing and free (an in-process object reference, never
serialised); the waste was purely at the HTTP edge. Now
`Sse:ExcludedMetaKeys` denies it there. A deny-list rather than an
allow-list on purpose: an allow-list would need editing every time an
agent adds a key the UI wants, and the failure mode of forgetting is a
silently missing feature rather than visible bloat.
