# Architecture

ECI-CAS is a set of independent agents — faculties of a mind — talking over an
in-process message bus. No agent calls another or knows another exists;
everything is topic + envelope. The one hard rule: **`Publish()` never blocks
on a subscriber.** A slow or failing agent cannot stall another's turn.

## Agent roster

| Agent | Subscribes | Publishes | Tier |
|---|---|---|---|
| Perception | (external input) | `events.perception` | deterministic |
| Impulse | `events.perception` | `events.advisories`, `events.proposal` (Critical reflex) | deterministic |
| Librarian | `events.perception` | `events.advisories`, `events.selected-pairs` | cognitive |
| Recall | `events.selected-pairs` | `events.advisories` | cognitive |
| Identity | `events.perception` | `events.advisories` | deterministic (state read) |
| Hindsight | `events.perception` | `events.advisories` | deterministic (embed + cosine) |
| Governance | `events.advisories`, `events.verdict` | `events.bundle`, `events.action`, `events.conclusion` | deterministic |
| Intent | `events.bundle` | `events.proposal` | cognitive |
| Security | `events.proposal` | `events.verdict` | deterministic |
| Action | `events.action` | — | deterministic |
| Archivist | `events.bundle` | `events.facts` | cognitive |
| Cataloger | `events.facts` | `system.control` (`Written`) | cognitive |
| Reflection | `events.conclusion` | `events.perception` (ideas), `system.control` | cognitive |
| ArchiveLogger | `Topics.All` | — | deterministic |
| ConsoleSubscriber | `Topics.All` | — | display |
| TurnLog | `Topics.All` | — | display |

With `Utterances:Enabled` (default false) the roster inverts: Librarian,
Archivist and Cataloger are removed, and two deterministic agents take
`events.perception` in their place.

| Agent | Subscribes | Publishes | Tier |
|---|---|---|---|
| Scribe | `events.perception` | — | deterministic (regex + cosine) |
| Recall (Consult) | `events.perception` | `events.advisories` | deterministic (cosine) |

Both manifests are inverted in code (`AgentRegistration.InvertManifest`),
not by a second appsettings topology, so the roster follows one boolean and
there is nothing to drift. The utterance log is a **library** on the same
footing as `IArchiveStore`.

`IArchiveStore` and `IPassageStore` are **libraries**, not bus citizens.
Topics are named by purpose, never by recipient, so no agent names another.

An agent is named for the job it does: a name may not claim work belonging to
another agent, and may not be broader than what the agent may do. Librarian
knows the catalogue and points at a shelf; Recall opens it; Archivist extracts
what Cataloger files.

The persona's own records live under one category, `assistant`: the persona
snippet Identity reads at `assistant/persona` in the agent state store, the
identity facts at `assistant/identity` in the archive, and Reflection's filed
ideas at `assistant/reflection`.

## The shape of a turn

Ordered by dependency, not by clock — anything not waiting on the line above
runs at the same time as it.

1. **Perception** turns input into `events.perception`: text, profile, a fresh
   `CorrelationId` every later envelope is derived from.
2. Four agents wake on it at once. **Impulse** appraises, **Identity** reads the
   persona, **Hindsight** sweeps the passage corpus, **Librarian** embeds the
   turn once and names its pairs on `events.selected-pairs`.
3. **Recall** reads those pairs and the recency lane, ranks both by cosine
   against Librarian's vector, and publishes `events.advisories`.
4. **Governance** bundles the roster — Impulse, Recall, Identity, Hindsight,
   from `Governance:BundleRoster` — by `CorrelationId`, or gives up after
   `BundleTimeoutMs`, and publishes `events.bundle`.
5. Two agents read that bundle. **Intent** writes the reply on
   `events.proposal`; **Archivist** extracts facts on `events.facts`, which
   **Cataloger** addresses and writes, announcing `Written` on `system.control`.
6. **Security** judges every proposal on `events.verdict`. Yellow buys up to
   `MaxRevisionPasses` passes back through Intent; Red replaces the reply.
7. **Governance** gates on that verdict: `events.action`, which **Action**
   delivers, and `events.conclusion`.
8. **Reflection** counts conclusions and fires every `BatchSize` of them — one
   passage, a mood label on `system.control`, and possibly an idea published
   back onto `events.perception` as `triggered_by: self`.

Two paths skip the middle. Impulse's Critical reflex publishes its own
`events.proposal` straight from step 2, gated by Security like any other. A
control message (`system.control`) reaches Identity and Impulse outside a turn
entirely.

`TurnLog` folds every envelope sharing a `CorrelationId` back into one record,
which is why steps 5–8 landing after the person has been answered still belong
to the same event.

## Bus mechanics

One topic, per-subscriber queues: `ChannelBus` maps
`topic → List<ChannelWriter<Envelope>>`, each `AgentBase` owns one
`Channel<Envelope>` and one consumer loop. `Publish` writes to every
subscriber's channel and returns immediately — unbounded, so a slow consumer
never backs up the publisher. Queue depth is exported as a metric.

`Topics.All` is a wildcard subscription; `ArchiveLogger` and
`ConsoleSubscriber` are plain subscribers on it, giving a complete audit trail
with no coupling and no relay hops.

Governance's bundle timeout is itself a message: a `PeriodicTimer` publishes a
`BundleTimeout` envelope onto Governance's own topic, handled by the same
consumer loop — so per-bundle state needs no lock, since one queue hands one
message to one consumer at a time.

`BusActivityTracker` counts in-flight envelopes and exposes
`WhenIdleAsync(timeout)`, so tests and the REPL know a turn has finished
propagating without polling.

Cross-agent order is never guaranteed and must never be assumed. Tests assert
outcome — what got published, what the archive holds — not interleaving,
except where an ordering is a genuine invariant (Action never fires before
Security clears it), which gets its own test.

### What travels on the bus

Envelopes carry a `MetaBag`; `Derive()` starts a fresh bag rather than
inheriting, so anything downstream is forwarded deliberately by whoever
publishes next. This table is the whole contract.

| Key | Published by | Read by |
| --- | --- | --- |
| `perception.text` | Perception, Librarian (re-published), Reflection (an idea) | Impulse, Librarian, Recall, Intent, Archivist, Hindsight |
| `perception.profile` | Perception, Librarian, Governance (on control) | Librarian, Recall, Archivist, Impulse, Governance, SseBroadcaster |
| `perception.triggered_by` | Reflection | Archivist |
| `impulse.advice` | Impulse | Intent |
| `impulse.expression` | Impulse | Governance |
| `impulse.reflex` | Impulse | Governance |
| `identity.advice` | Identity | Intent |
| `librarian.selected_pairs` | Librarian | Recall, Archivist, TurnLog |
| `recall.facts` | Recall | Intent |
| `hindsight.notes` | Hindsight | Intent |
| `hindsight.note_ids` | Hindsight, Intent, Governance (both forward) | Reflection |
| `hindsight.echo_depth` | Hindsight, Intent, Governance (both forward) | Reflection |
| `intent.reply` | Intent, Impulse (reflex), Security, Governance (forwards) | Governance, Security, Reflection, ConsoleSubscriber |
| `intent.context` | Intent, Governance (forwards) | Reflection |
| `security.verdict` | Security, Governance (forwards) | Governance, ConsoleSubscriber |
| `security.concern` | Security | Governance |
| `substrate.degraded` | any cognitive agent, via `SubstrateHealth.Mark` | Governance |
| `governance.revision_concern` | Governance | Intent |
| `governance.expression` | Governance | SSE clients |
| `governance.security_alert` | Governance | SSE clients |
| `governance.degraded` | Governance | SSE clients |
| `control.kind` | Cataloger, Governance, Reflection | Identity, Impulse |
| `reflection.mood` | Reflection | Impulse |
| `archivist.facts` | Archivist | Cataloger |
| `archivist.written` | Cataloger (on `Written`) | TurnLog |
| `reflection.passages` | Reflection (on `Reflected`) | TurnLog |
| `reflection.idea` | Reflection (on `Reflected`) | TurnLog |
| `substrate.agent` / `.class` / `.latency_ms` / `.tokens` / `.cost` | every substrate caller, via `SubstrateTrace` | TurnLog |
| `substrate.label` | Recall (the pair it picked for) | TurnLog |

The `governance.*` trio is read by nobody in this process: `SseBroadcaster`
fans whole envelopes to clients, so it is the display layer's contract.

Four keys appear under "published by" for agents that did not originate them.
The fresh-bag rule means a key crossing more than one hop is re-published at
each, and Governance carries several so Reflection can see them after the turn
concludes.

## Telemetry and the turn log

`SubstrateTrace` publishes what a call cost as one envelope per call on
`system.telemetry`, derived from the triggering envelope so `CorrelationId`
files it under the turn. A topic rather than keys on the caller's envelope,
because the two are not one-to-one: Recall fans out a call per chunk behind a
single advisory, Reflection's call spans a batch, Cataloger makes two calls per
fact and writes only on flush. An agent running `UseSubstrate: false` produces
no trace — a configured deterministic answer is not a call.

`TurnLog` is a wildcard subscriber folding every envelope of a turn into one
`TurnRecord` — perception, impulse, what Librarian and Recall read, what
Cataloger wrote, the reply, a non-green verdict, Reflection's passages and
idea, and every substrate call. The reduction is `TurnProjection`, a pure
function over `(record, envelope)`, so display code holds none of it and
arrival order does not matter: envelopes fill named slots rather than
appending.

Three consumers read the same records: SSE clients on `/api/log/stream`,
`/api/log` for what a client missed, and any `ITurnLogSink` (one ships —
`JsonlTurnLogSink`, off unless `TurnLog:Path` is set). A record reaches the
sinks once, after `TurnLog:SettleMs` of quiet, because Archivist, Cataloger and
Reflection land behind the reply and an event is not over when the person has
been answered.

Profile scoping mirrors `SseBroadcaster`: a client naming a profile sees its
own turns plus the ones nobody owns.

`CostLedger` keeps the two totals a per-event figure cannot answer: what this
run has spent, and what every run has. Accumulation happens on the telemetry
envelope as it arrives, never by re-summing the record — a record is rebuilt on
every envelope of its turn. Both figures are stamped onto the record rather
than derived at render time, so reopening an old event shows what was true
then. Only the lifetime total persists (`TurnLog:CostPath`).

## Runtime knobs

`RuntimeKnobs` holds the numbers the companion's Debug panel exposes as
sliders — reply length, reflection cadence, recall threads and depth, and a
five-step mood enum — overriding the corresponding static options live. Beside them sits the
Tier dropdown, which is not a knob but the whole configuration: `TierCatalog`
binds every `appsettings.<Tier>.json` at boot, and a switch replaces
`Substrates:Classes` and `AgentSubstrates` by reference, which is safe because
nothing in the substrate path caches its config. Switching re-seeds recall
threads and depth from the new tier. In-memory only apart from those two: a
restart resets each to a default matching the tier's config, so an untouched
slider changes nothing, and Save is what makes a dragged one survive.

## Tier knobs: how wide and how deep a turn reads

Two numbers decide a turn's read cost, and both are sliders in the Debug
panel seeded from the tier file: **threads** (how many lanes open) and
**depth** (how many rows each lane hands back). Everything else about
retrieval is derived from them, so there is one place to turn and no way for
the surface to claim a fan-out the agents are not running.

### Threads: `Recall:Threads`, seeded into `RuntimeKnobs.RecallThreads`

Lane 1 is the recency lane, which is not a pair and is always read. Every
lane after that alternates: a vector-found pair, then a selector-named pair,
then a vector pair again.

| Threads | Opens |
|---|---|
| 1 | recent only |
| 2 | recent + 1 vector pair |
| 3 | recent + 1 vector + 1 selected |
| 4 | recent + 2 vector + 1 selected |
| *n* | recent + `n / 2` vector + `(n - 1) / 2` selected |

Those two quotients are `RuntimeKnobs.VectorLanePairs` and
`SelectorLanePairs`, and Librarian reads them directly — and they are the
only configuration of how many pairs a turn opens. The cut
is upstream: a thread the slider does not allow is a pair Librarian never
names, not a pair Recall opens and discards. Passage leads share the vector
lane's budget rather than adding to it.

### Depth: `Recall:MaxPickedPerWorker`, seeded into `RuntimeKnobs.RecallDepth`

Depth is how many rows one lane may hand Intent — capped at 10, because the
prompt is the scarce thing and past that the reply is quoting a file. It caps
the recency lane, it is what a picking call may return, and it is the size
below which a lane passes whole with no substrate call at all.

It also *is* the row-vector cut: `RuntimeKnobs.VectorCandidates` is the depth
itself, so cosine keeps exactly as many rows as the lane may hand back.

Depth governs every cosine cut a turn makes, not only the ones over archive
rows: Hindsight's wake and Librarian's passage sweep both take their top-K
from it, in place of `PassageOptions.TopK`. A turn has one retrieval budget,
and a knob that covered rows but not notes would only half answer "how much
does this read".

Over the passage corpus it is discounted — `RuntimeKnobs.PassageCandidates`
is `1 + depth / 2`, so depth 3 wakes two notes and depth 10 wakes six. The
unit is what earns the discount: a row is a fact and ten facts is a well-read
turn, where a passage is the persona's own thought about a stretch of turns
and ten of those is the prompt spent on second-guessing. The plus one is so
the shallowest setting reads little rather than nothing.

That cut applies to every pair Recall opens, not only the ones the gloss
sweep found — it runs downstream of selection and does not know how a pair
was chosen. And because the cut equals the budget, the picking call has
nothing left to cut: a pair that narrows costs no substrate call at all. The
measured reason to want that is the bench's own nopick rate, 78% against a
lenient bar, which said the picker was the read side's largest loss and was
discarding exactly the cosine-ranked rows.

### What is still a tier file's own

| Key | Means |
|---|---|
| `Recall:RowsPerWorker` | How many candidate rows one picking call is shown when a pair *cannot* be vector-narrowed and falls back to chunk-and-pick. A quality limit, not a context one: a small model's ability to spot the right row in a flat list falls off long before its window does. |
| `Recall:MaxConcurrentRecalls` | Ceiling on picking calls per turn across all lanes — the backstop for an unusually deep pair that splits into many chunks. |
| `Recall:RecentRows` | How far back the recency lane may look before depth cuts it. The lane spans a year; this is what makes it a prompt rather than a dump. |
| `Recall:PickAfterVector` | Whether cosine-narrowed rows still go through a picking call. `true` in code, `false` on every tier: with the cut equal to the depth such a call has nothing to choose from. Raising the cut above the depth is what makes it worth switching on. |
| `Librarian:VectorMinScore` | Cosine floor a gloss must clear to count as a lead. Without it the sweep always returns its full quota, even on a turn about nothing in the archive. `0.75` on every tier. |

### What each tier currently sets

| | Mock | Minimal | Budget | Default | Super |
|---|---|---|---|---|---|
| `Recall:Threads` | 3 | 4 | 4 | 5 | 8 |
| `Recall:MaxPickedPerWorker` | 2 | 5 | 4 | 4 | 8 |
| `Recall:RowsPerWorker` | 10 | 10 | 25 | 50 | 100 |
| `Recall:MaxConcurrentRecalls` | 4 | 4 | 8 | 12 | 16 |
| `Recall:RecentRows` | 10 | 10 | 20 | 30 | 60 |

So Default opens five lanes — recent, two vector pairs, two selected pairs —
and takes at most four rows out of each. Width is the expensive axis and the
one that buys least: doubling the pairs a turn opens costs a substrate call
each and does not measurably improve what the persona remembers.

### Saving a setting that was found by dragging

The Debug panel's Save button writes the two live Recall knobs back into
`appsettings.<ActiveTier>.json` — both the copy in `src/EciCas.Host/` that a
human and git read, and the copy under the binary that the next boot actually
loads. Writing only one of the two is the failure worth avoiding: the source
alone and the next run ignores the save, the build output alone and the next
`dotnet build` reverts it. The button is grey while the live values match the
file, which makes it also the answer to "is what I am running what is written
down".

## Governance: decision-only

Three jobs, all decisions over held state: bundling the advisory fan-out,
gating on Security's verdict before Action, and revision passes. It does not
route messages for other agents. The roster it bundles against and its timeout
come from `IOptions<GovernanceOptions>`, so adding an advisory-producing agent
is a config change.

It owns one more thing that follows from bundling: **the honesty notice.**
Every substrate caller stamps `substrate.degraded` on its advisory when its
call failed, and Governance — the only agent seeing the whole fan-out by
`CorrelationId` — turns "who was degraded or absent" into deterministic native
text. Native, because an LLM-authored apology cannot be produced by the LLM
that is not answering. A degraded Intent replaces the reply; a degraded advisor
appends a parenthetical; a Red verdict gets neither.

## Instructions: the words are not code

Every sentence the persona speaks or is steered by lives in
`src/EciCas.Host/instructions/*.txt`, one file per agent, loaded once at
startup by `FileInstructionStore`. This covers more than prompts: Identity's
persona, Impulse's reflex reply and Governance's honesty notices never reach a
model, but they colour how the persona sounds exactly as much as a prompt does.

A file splits on `## ` markers; text before the first is `main`.
`{placeholder}` substitution is the only templating, and an unknown
placeholder is left visible rather than blanked, so a mistake reads as one.

Two startup failures, both deliberate. A **missing file** cannot fall back to
an empty instruction — an agent that silently loses its standing text still
answers, just worse. An **unknown placeholder** has the same shape.
`KnownPlaceholders` names what each file may reference; anything else refuses
to boot.

Tests load the *shipped* files, not doubles (`ShippedInstructions`).

**Identity is a seed, not a setting.** The host writes `identity.txt` to
`assistant/persona` only when it finds that path empty, and reads the store
from then on. The boot log says which happened.

**It is a colour, not a brief.** The seed is a handful of keywords — "You are
warm, unhurried, plain-spoken" — because it reaches Intent as one bracketed
aside beside the turn. Written as a paragraph of rules it stops colouring and
starts competing with the sentence the person typed.

**The profiles are sections, and config picks one.** `identity.txt` holds
`grump`, `educator`, `playmate`; `Identity:Profile` names the one that seeds a
new persona, unset takes the unnamed block. A name matching no section stops
the host at boot with the real list.

**It starts nameless.** Nothing seeds a name. A persona that boots knowing what
it is called cannot be introduced to anyone and cannot be renamed: the seeded
fact outranks the conversation. It is told a name like anything else, and keeps
it only if Archivist judges it worth writing down. The archive's one seed
record is `assistant/system/eci/this/version`.

**Its name belongs to whoever is talking.** `PersonaName` reads
`persona/name/this/assistant/name`, per profile, and that address is chosen for
one reason: `assistant` is in `Archive:SharedCategories`, so a name filed there
would be one name for everybody on the device. Until someone renames it,
`Identity:DefaultName` answers — configuration, not a constant, because the
name under the avatar on first boot is a naming decision. There is no record
until a rename writes one. Intent and the surface read through the same object,
so the caption and the self-introduction cannot disagree. The cache clears on
any archive write at all.

**How it hears its own name.** `identity.txt` has a `name` section holding one
sentence with a `{name}` placeholder, appended to whichever profile is in force
so Intent receives character and name as the single aside it already reads.
That sentence ships commented out — a persona reminded of its own name every
turn spends output introducing itself, and what it is called is a row Recall
fetches on the turns that ask. Emptying the section drops the clause entirely.
`archivist.txt` names the address a name goes to and stops there: it says *when
the person names you*, not *always write this*.

## Storage: a library, not an agent

`IArchiveStore` owns the archive files, schema, and all concurrency. Nothing
outside it touches a file directly. It has four members: `IndexFor`,
`LookupAsync`, `RecentAsync`, `WriteAsync` — each taking the profile whose turn
it is.

**One file per `(Category, Topic)` pair, and the file name is the index.** Files
are named `{esc(category)}~{esc(topic)}.parquet`, so the set of known pairs is
recovered by listing the directory; there is no `index.parquet`. A write never
rewrites a companion index, and the index cannot drift from the data. Deleting
a pair's last row deletes its file, which is how the pair leaves the index.

Names are percent-escaped down to `[A-Za-z0-9._-]` over UTF-8 bytes. Turn facts
come from a closed vocabulary, but Reflection's ideas do not, so a slash, colon
or space in a topic is a matter of time; escaping `~` inside each half is what
makes the single-character separator unambiguous. The encoding is reversible,
because decoding it is how the index is read.

Parquet has no in-place append: a write reads the target file, merges the new
rows in memory, and rewrites it as one row group through a temp file and an
atomic move. A write already holds the whole pair in memory, so it keeps it,
and reads cache what they load — a hot pair costs one file read for the
process's lifetime.

Concurrency is per file: a `SemaphoreSlim` per path, not one global lock.
Recall's parallel workers touch disjoint files and never queue behind each
other, and a write blocks readers of the one pair it touches.

**A fact restated is not a second fact.** Within a pair a row is addressed by
`subtopic/subject/key`, lowercased, and a new row at an existing address
replaces it in place. Without that, "lives in Oslo" then "lives in Bergen"
would leave both on file for the picking model to choose between.

**Personal knowledge is scoped by directory.** `archive/` is the shared tier,
`archive/profiles/{id}/` is one person's, under the same naming convention — so
"the name is the index" holds inside each. Reads union the two tiers with the
profile winning on a row-key collision; writes land in the profile's directory
unless the category is on the operator's `Archive:SharedCategories` allowlist
(`assistant` by default). `null` addresses the shared tier alone.

Records are addressed by a five-part LLM-extracted schema —
`category/topic/subtopic/subject/key=value`. Archivist extracts the last three;
the `category/topic` half is Cataloger's, chosen from the closed list in
`instructions/cataloger.txt` and joined into a path in code, because the file
name *is* the index and an invented one is a file nobody opens. **Subtopic is
data, not an address**: every record carries it and the picking model reads it,
but nothing looks up by it, so one subtopic can be discussed at length without
earning an index entry.

Every row also carries `Sentence`, the same fact as one plain sentence, written
by the Archivist call that extracted it. Readers render the address always and
the sentence after it when there is one; empty is normal.

The rule that must hold for every write — keep the value short enough that a
later lookup intersects what got written — is `ArchiveWriteStyle.TerseValue`.
Reflection interpolates it (`ReflectionAgent.cs`); Archivist does not, and
`archivist.txt` states a tighter version in prose, so the two writers can
drift. The companion rule — structural fields in English, proper nouns never
translated — is a sentence in each instruction file rather than a C# constant:
it is a rule addressed to a model, and those live in `instructions/`.

### The recency lane

Beside the pair files in every directory sits `recent.parquet`: a copy of every
row written there, newest first. Its name carries no separator, so the index
decoder rejects it and it can never be mistaken for a pair.

It exists because the shelf is weakest where "lately" is asked: a fact filed an
hour ago into a drawer no question names is unreachable until a question names
the drawer. `WriteAsync` writes it after the pair files — a row is in the
archive once its pair file holds it, and the lane is a view of that.

Rows in the lane are keyed on the full `category/topic/subtopic/subject/key`,
because two drawers may each hold a `renewal / daniel cost` and within the lane
those are different facts. Restating a fact still replaces it.

The window is one year, and `TrimRecentAsync` drops what has aged out at boot,
not on write. Nothing is lost: the pair file that owns a row is the long-term
store.

Recency is how the lane is *built*, not how it is ranked. Once read, its rows
are candidates like any others and go through the same cosine cut the selected
pairs get — `RecentRows` decides how far back the lane looks, and the question
decides which of those rows Intent sees.

### The third store: agent state

`IAgentStateStore` (`JsonlAgentStateStore`, `memory.jsonl`) holds opaque blobs
an agent keeps for itself — Impulse's drive vectors, Identity's persona —
addressed by a flat path with no schema, no pair index and no vector. Those are
not facts about the world and do not belong in an archive Librarian selects
over.

It keeps a **window** per path, not a log and not a single row: every read asks
for the newest, an unbounded file grows to hold lines nothing returns, and one
line per path would lose the superseded drive vectors that are the only record
of how the persona has been over time. Trimmed on write,
`Reflection:DriveHistory` states deep. Lines the trimmer cannot parse are kept
rather than guessed at.

## Who touches the pair files, and how each finds its file

Six callers, one library. None of them opens a file, builds a path, or holds an
index of its own — the address is always a `(Category, Topic)` pair handed to
`IArchiveStore`, which turns it into a path and takes that path's lock.

| Caller | Finds its file by | Reads | Writes |
|---|---|---|---|
| Librarian | listing the index for the turn's profile | pair names only | — |
| Recall | the pairs Librarian selected, plus the lane | rows | — |
| Cataloger | the closed vocabulary, per fact; `assistant/*` by subject | — | rows |
| Reflection | the closed vocabulary, `assistant/reflection` | — | rows |
| PersonaName | a fixed pair, `persona/name` | one row | — |
| ArchiveTool | the directory listing, directly | rows | rows, deletes |

**Librarian** calls `IndexFor(profileId)` and gets the union of the shared and
profile directories, decoded from file names. It sees pairs, never rows, so its
prompt stays short as the archive deepens. It selects up to
`RuntimeKnobs.SelectorLanePairs` and publishes them; it never opens a file.

**Recall** calls `LookupAsync(pair, profileId)` once per selected pair — one
file read each, in parallel, since distinct pairs are distinct files — and
`RecentAsync(profileId, RecentRows)` once for the lane. Rows come back
importance-ordered for a pair and timestamp-ordered for the lane. It writes
nothing.

**Cataloger** is the only writer of turn facts. Archivist extracts
`subtopic/subject/key=value` and publishes them; Cataloger asks the substrate
for a category and then a topic, matching both against `ClosedVocabulary` — an
unmatched topic becomes `{category}/other`, and an unmatched category lands the
fact at `unfiled/unfiled`. It groups the batch by profile and calls
`WriteAsync` once per profile, which fans out to one append per pair.

**Nothing that was stated is dropped.** Two addresses exist so that the closed
vocabulary can stay closed without costing facts. `{category}/other`
absorbs an unlisted topic; `unfiled/unfiled` does the same one level up, for a
fact whose category matches nothing at all. Its topic is not
`other` because Librarian hides every `other` topic from the selector and opens
it in code beside its parent — an `unfiled/other` would be a file nothing ever
selects and nothing ever opens alongside. As a visible pair it can be picked,
and a run of rows accumulating in it is the vocabulary reporting what it is
missing.

**Facts about the persona skip the category call entirely.** If Archivist left
a fact's subject on `assistant`, `you`, or `yourself`, it is a fact about the
persona, and it is filed on the persona's own shelf — category `assistant`,
topic chosen from `AssistantScope.Topics` (`persona`, `reflection`, `system`)
plus `other`. This is the same argument as the read side (see below): whose
fact a row is has to be decided *before* ranking, because a bi-encoder has
nowhere to encode it. No instruction file asks for any of this — the subject
field Archivist already fills is the whole routing rule. Because `assistant` is
on the `Archive:SharedCategories` allowlist, these facts are per device rather
than per profile.

The one address that is taken before the shelf is `persona/name`: a rename is
per profile, so it cannot live in a shared category. `PersonaName.Rename` reads
it off the extracted fact — self subject, key whose last word is `name` — with
no model call at all.

**Reflection** writes its own ideas with `profileId: null` under
`assistant/reflection`, so they land in the shared tier: what the persona
thinks is about the persona.

**PersonaName** does one `LookupAsync` of the fixed pair `persona/name` and
takes the row whose subject is `assistant` and key is `name`, falling back to
`Identity:DefaultName`. It caches the answer and clears the cache on any write.

**ArchiveTool** is the one caller that goes around the interface, reusing
`ParquetArchiveStore`'s static helpers — `PairsIn`, `PairPathFor`,
`ReadRecordsAsync`, `WriteRecordsAsync` — so its notion of a record's shape
cannot drift. Only one process should point at an archive directory at a time.

## The Librarian to Recall knowledge swarm

Librarian is a **selector**, not a reader: given the turn's text and the pair
index, it picks up to `RuntimeKnobs.SelectorLanePairs` pairs and publishes them on
`events.selected-pairs` — even an empty list on fallback, so Recall replies
exactly once and Governance's bundle roster stays static. Selection is
LLM-driven rather than keyword matching, because disambiguating "name of
system" from "name of person" needs semantic judgment.

It runs on every turn with a non-empty index, including one small enough that
selecting everything would have been correct.

Recall does the reading, in two phases inside one `HandleAsync`:

1. **Read** every selected pair at once, plus the recency lane.
2. **Pick** — each pair's rows split into chunks of `RowsPerWorker`, and every
   chunk across every pair becomes one substrate call in a single flat
   `Task.WhenAll`.

The whole worker list is built before any call starts, so a deep pair never
produces a second wave discovered after the first returns. Turn latency is one
file read plus one substrate call, not N of either.

A pair is never truncated — a subtopic discussed at length produces more
chunks. `RowsPerWorker` is a *quality* limit, not a context-window one: a
candidate row costs well under 20 tokens, but a small model's ability to spot
the relevant entry in a flat list falls off well before its context does. The
per-turn ceiling is `MaxConcurrentRecalls`, and the trim to it is breadth-first
across pairs — rows are importance-ordered, so each pair's first chunk is its
most valuable and one deep pair cannot starve the others.

The lane gets one dedicated call, appended after that trim: the cap is a budget
over the pairs Librarian selected, and the lane is not one of them. Recall skips
picking entirely when everything loaded fits `RecallDepth` — an under-budget
chunk is nothing to choose from — and with no picking model configured it hands
over the newest lane rows as they stand. Every publish goes through one
`Distinct` over the full `category/topic/subtopic/subject/key` address, so a
fact reached both from its drawer and from the lane arrives at Intent once.

Findings go straight to Governance, never back through Librarian: the two are
different sources of truth — parametric model knowledge against stored record —
and neither should become stateful across the other's response.

## Two layers of cosine: the pair, then the row

Both layers narrow; neither decides. The pair layer is still the coarse
symbolic cut, because a class of question is inference over facts rather than
similarity to them — "am I old enough to rent a car" names no row it needs.
Cosine is the fine cut inside a pair.

**The row layer.** `ArchiveRecord` carries a nullable `Embedding` beside the
`ModelId` that produced it and a hash of the text that was embedded. The
embedded text is `"{Subtopic} {Subject} {Key} = {Value}"` plus the sentence
when there is one, and excludes category and topic — the pair layer already
encodes those. Measured on the writable arms: the address line alone reaches
88% top-5, the sentence alone 88%, the two together 92%.

Writing is a decorator. `EmbeddingArchiveStore` wraps `IArchiveStore`, so
Cataloger, Reflection, PersonaName and tool imports all get vectors without any
of them acquiring an embedder. It embeds only rows that lack a current vector,
in one batch, and swallows a provider failure — a fact reaching the archive
matters more than a fact reaching it searchable.

Reading is all-or-nothing per set — a selected pair, or the recency lane, which
by the time it is a list of candidate rows is nothing else. Recall narrows a set
by cosine only when
*every* row in it has a vector from the current `ModelId` and a hash matching
its own text; one bare row sends the whole file back to chunk-and-pick.
Sweeping half a file would rank the embedded half against nothing and lose the
rest silently. The hash is what makes a restatement self-invalidate: `Merged`
keeps the address and swaps the value, so an inherited vector would go on
scoring as Oslo after the row said Bergen.

When every selected pair *and the lane* narrowed, Recall publishes the survivors
and skips the picking calls entirely — `PickAfterVector` is false because picking is
the biggest read-side loss measured (batch 12: 78% with `nopick`, 60% with the
lenient bar). How many rows survive per pair is `RuntimeKnobs.VectorCandidates`,
which is the Recall-depth slider itself.

**The pair layer.** `PairVectorIndex` embeds each `category/topic` as
`"{category} {topic}: {gloss}"`, lazily and in one batch, and drops the table
when `ModelId` changes. Librarian unions the top `RuntimeKnobs.VectorLanePairs`
above `VectorMinScore` into its own LLM selection rather than replacing it.

The query is embedded once per turn. Librarian publishes it on the envelope as
`librarian.query_vector`, and Recall uses that rather than re-deriving it. e5
asymmetry lives in the providers: `EmbeddingKind.Query`/`Passage` chooses the
`query: ` / `passage: ` prefix, so the retrieval probe measures what the running
system does.

**Backfilling.** An archive missing vectors is uncovered, not broken. The host
runs `ArchiveBackfill` at boot against the embedder it resolved and invalidates
the store's cache if it wrote anything, so a running deployment never needs the
tool. `ArchiveTool`'s `embed <model.onnx> <vocab.txt>` is the same job for an
archive no host is booting — a copy under test, one restored from backup. There
the paths matter exactly: `ModelId` is `onnx:{ModelPath}`, and the same weights
under a different path produce vectors that are correct and ignored.

## The passage corpus: what it missed, not what it knows

The archive answers *what is true*. The passage corpus answers *what should I
have looked up* — a different substance from either vector layer above.

Reflection reads a batch of concluded turns
and, alongside extracting ideas, writes a 5–15 word note on the context that
batch missed, in the register of a code review of its own retrieval ("should
have read the family record before answering"). That note, and only that note,
gets an embedding.

A note names the `category/topic` pairs it wishes had been read, stored as row
metadata — which is what makes a vector hit actionable. Librarian embeds the
incoming turn, cosine-matches the corpus, and merges the matched notes' pairs
into the selection alongside its own. Pointers resolve against the *live* index,
so a pair whose last row was deleted contributes nothing. The vectors narrow;
Recall's row-picking call still does the picking.

The note's *text* takes a different path. `HindsightAgent` is its own
bundle-roster slot: it subscribes to `events.perception`, sweeps the same
corpus, and publishes `hindsight.notes` for Intent to read as
`[Noted before: …]`, capped by `PromptCap`. Each note arrives with its age on
the front — "3 months ago", "earlier today" — because a thought the persona has
been carrying and an echo of the last turn should not read the same.

Prose and facts are different substances. Intent weighs "what the archive held"
and "what I once thought about this" as two contributions and can disagree with
either. The cost is one extra embed per turn.

**The revisit.** The previous batch's note is quoted back at the top of the next
Reflection prompt, which may rewrite it in light of what happened since. The
rewrite *replaces* the row, keeping its id and original timestamp, so the corpus
stays roughly one row per batch and "latest" still means the newest
event-series. Writes are once per `ReflectionOptions.BatchSize` concluded turns.

Storage is a single `passages.parquet` in the archive root. One file, because
there is no address to shard on — every query is a cosine sweep over the whole
corpus, and at a row per ten turns brute force over the in-memory cache needs no
ANN index. Shared tier only.

### The embedder

`IEmbeddingProvider` has a `bool Available`, because **not having an embedder is
a normal state, not a failure.** The default is a local ONNX
sentence-transformer whose ~90MB model is deliberately uncommitted. If it is not
there, the provider logs one warning, reports `Available == false`, and the
swarm runs without vectors — Reflection writes no passages, Librarian matches
none. It never marks `substrate.degraded`, because nothing is degraded.

Any BERT-family ONNX export with its `vocab.txt` works with no code change;
`scripts/get-embedding-model.ps1` fetches one into `<repo>/models/embedding` and
prints the absolute paths for `appsettings.json`. Outside `bin/` on purpose: a
relative `Embedding:ModelPath` resolves against the build output, where
`dotnet clean` deletes the weights. Embeddings are mean-pooled over
`last_hidden_state` and L2-normalized at write time, so cosine similarity is a
plain dot product.

`Embedding:Provider = "openai"` (or `"api"`) borrows the substrate registry's
`HttpClient` and calls an OpenAI-compatible `embeddings` endpoint; a failed call
returns no vectors rather than throwing. `"none"` turns the corpus off. Any
other value is a startup error. Whichever provider is configured stamps its
identity on every passage it writes, and the host refuses to start if the stored
corpus disagrees.

## Impulse's Critical reflex

Impulse and Intent are two independent publishers on `events.proposal`; Security
gates every proposal the same way regardless of source, and Governance's
green/yellow/red matrix needs no reflex branch. `events.action` has exactly one
publisher — Governance, downstream of a verdict — so the reflex path is a second
producer, not a bypass of the gate. The one stateful wrinkle, that a reflex does
not conclude a Critical event since Intent's considered reply still follows,
lives in Governance's per-event state.

## Reflection's ideas and the generation guard

Reflection can publish a follow-up idea back onto `events.perception` with
`triggered_by: "self"` — downstream nothing distinguishes it from external
input. Every envelope carries a `Generation` int, incremented whenever an agent
spawns a new arc; Reflection refuses to spawn past `MaxIdeaGeneration` (1) and
skips the substrate call once at the cap, so an idea → conclusion → idea chain
cannot loop.

## Drive vectors: who may move them

`DriveVectors` (curiosity, fatigue, urgency, social drive, temperature) is the
persona's appraisal state, persisted as JSON and read by Reflection (eagerness
gating) and Governance (the face on a blocked reply).

Impulse appraises that face on every turn — `Expression()` over its own state,
published as `impulse.expression` once the turn's nudges have landed. Governance
captures it when the bundle completes and forwards it on the Action/Conclusion
as `governance.expression`, so a surface can draw the mood without owning the
vocabulary or reading drive state. A block overwrites it with the face read
after the frustration nudge.

**Impulse owns every number that lands on it.** Other agents may *request* a
shift, never quantify one: Governance publishes a `Frustration` control message
on a Red verdict, Reflection attaches a mood label to its `Reflected` control
message, and Impulse maps each to a delta written in its own source. Both arrive
over `system.control`, so no agent holds a reference to Impulse.

Two speeds, deliberately far apart:

- **Instant** (±0.05–0.15, per turn) — Impulse's own keyword triggers and
  Governance's block nudge.
- **Slow colouring** (±0.01–0.03, once per Reflection batch) — the tone of a
  whole batch of concluded turns. Reflection owns this because it already
  reasons across a batch; Archivist stays a per-turn fact extractor.

A third thing happens on turns that appraise nothing: the state closes a fifth
of its distance to the defaults, snapping home under 0.01 so it settles rather
than halving forever, and writing nothing once it is there. Not a third
magnitude — no one nudged — it is time passing showing up in the state. Without
it urgency, which has no downward trigger, strands a profile at `alert` for good
after one message saying "urgent".

The magnitude gap *is* the distinction between the two mechanisms — a test
asserts every slow delta stays under every instant one, comparing the tables
rather than pinned literals so both stay tunable.

### Trajectory, not level

A single drive state is a gauge; the window of them is a history, and that is
what Reflection reads. `DriveTrend.Describe` collapses it into words the way
`Expression()` collapses five vectors into three axes: *"Across the last 6
recorded states: engagement rising, alertness steady, warmth falling."*

Never numbers. A prompt carrying `Curiosity: 0.83` invites the persona to quote
its own telemetry back, which is the register of a status page rather than of a
mind; a test asserts no decimal reaches the prompt. The instruction receiving it
is explicit that this is where the persona has been, not a subject to write
about, and that it must not perform a feeling it has no grounds for — the same
rule Governance follows when it says *"(Thinking without Recall just now, so
this is less grounded than usual.)"* and stays silent otherwise.

Surface interiority only where something actually happened to cause it. That is
the line between grounded interiority and performed sentience: the failure it
prevents is a persona narrating states it does not have.

## Prompt growth cap

Every `CognitiveAgent<T>` prompt folds in upstream advisory text, and a
Reflection→Perception→Intent→Reflection loop would otherwise re-embed every
prior hop. [`PromptCap`](../src/EciCas.Core/PromptCap.cs) caps each piece of
upstream text (240 chars) at the point it is folded in, so the per-hop ceiling
is fixed no matter how deep a loop runs.

It flattens as well as caps. Every call site folds the result into a slot that
is one line by construction — a bracketed aside, or an entry in a numbered list
the model answers by index — so a value carrying its own newlines would split
that slot and take the prompt's structure with it.

## Console output

`ConsoleSubscriber` subscribes to `Topics.All` but does not print one line per
envelope. It defaults to roughly six lines per turn — substrate cost, what
Recall read, what Archivist and Reflection wrote, what Intent said, what
Security blocked; `--Verbose=true` restores the exhaustive per-envelope trace.

## Archive tool

`EciCas.ArchiveTool` is a console REPL for inspecting and editing the Parquet
archive directly, when a record needs correcting without running the swarm.

```bash
dotnet run --project src/EciCas.ArchiveTool -- src/EciCas.Host/bin/Debug/net10.0/archive
```

The directory argument defaults to `archive` relative to cwd and must already
exist — the tool edits an archive, the host makes one. On Windows prefer
PowerShell or forward slashes; Git Bash mangles a backslash-prefixed argument.

Every address may carry a tier prefix, `profile:category`, because the same
pair name can exist in the shared root and under any `profiles/<id>/`. Without
one, a command spans every tier for reads and refuses to guess for deletes.

| Command | Effect |
|---|---|
| `list` | Known pairs, decoded from file names, prefixed by tier |
| `show <[profile:]category> [topic] [subtopic]` | `[i] Topic/Subtopic/Subject/Key = Value` across every matching pair |
| `showall <[profile:]category> [topic] [subtopic]` | Full field dump per row, including Importance/Domain/Timestamp |
| `recent [[profile:]recent]` | The recency lane, newest first, with timestamps |
| `passages [count]` | Reflection's notes — id, lineage, pairs, first line |
| `passage <id>` | One note in full, id matched on any unambiguous prefix |
| `del <[profile:]category> <topic> <index[,index…]>` | Delete rows by the index `show` printed |
| `del <[profile:]category> <topic> [subtopic]` | Delete every row in the pair whose Subtopic contains the text |
| `del [profile:]recent <index[,index…]>` | Delete lane rows by the index `recent` printed |
| `del passage <id>` | Delete one note |
| `embed <model.onnx> <vocab.txt>` | Backfill vectors in place |
| `reset` | Delete every `*.parquet` and reseed the one version record |
| `help` / `exit` | — |

`del` always names one pair or one lane, since a row index is only meaningful
within one file; the pair form picks between index and subtopic by whether the
third token is a comma-separated list of integers. Deleting a pair's last row
deletes the file, which is how that pair leaves the index — there is no
`rebuild-index`, because the directory listing *is* the index. Deleting from
the lane does not touch the pair file that owns the row. Arguments split on
plain whitespace with no quote-awareness, and filter delete is a substring
match.

## Verification

Tests assert outcome, not interleaving. The friction test enforced by review:
adding an agent should touch one new class, one DI line, and one config block —
if a PR adding an agent edits another agent, the abstraction is wrong.
