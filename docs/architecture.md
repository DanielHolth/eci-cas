# Architecture

ECI-CAS is a set of independent agents — "faculties of a mind" — talking
over an in-process message bus. No agent calls another agent directly or
knows another agent exists; everything is topic + envelope. The one hard
rule: **`Publish()` never blocks on a subscriber.** A slow or failing
agent can never stall another agent's turn.

## Agent roster

| Agent | Subscribes | Publishes | Tier |
|---|---|---|---|
| Perception | (external input) | `events.perception` | deterministic |
| Impulse | `events.perception` | `events.advisories`, `events.proposal` (Critical reflex) | deterministic |
| Librarian | `events.perception` | `events.advisories`, `events.selected-pairs` | cognitive |
| Recall | `events.selected-pairs` | `events.advisories` | deterministic |
| Identity | `events.perception` | `events.advisories` | deterministic (archive read) |
| Hindsight | `events.perception` | `events.advisories` | deterministic (embed + cosine) |
| Governance | `events.advisories`, `events.verdict` | `events.bundle`, `events.action`, `events.conclusion` | deterministic |
| Intent | `events.bundle` | `events.proposal` | cognitive |
| Security | `events.proposal` | `events.verdict` | deterministic |
| Action | `events.action` | — | deterministic |
| Archivist | `events.bundle` | `system.control` (`Written`) | cognitive |
| Reflection | `events.conclusion` | `events.perception` (ideas), `system.control` | cognitive |
| ArchiveLogger | `Topics.All` | — | deterministic |
| ConsoleSubscriber | `Topics.All` | — | display |

`IArchiveStore` and `IPassageStore` are **libraries**, not bus citizens —
see below.

Topics are named by purpose, never by recipient, so no agent ever names
another agent.

### On the roster's names

Each agent is named for the job it actually does, and the test is narrow: a
name may not claim work that belongs to a different agent, and it may not be
broader than what the agent is allowed to do. Names that fail the test invite
work to accrete under them.

Two were renamed after an audit against that test. **Librarian** was
Reasoning, which both overclaimed (it reasons about nothing) and misdirected
(a reader expects conclusions to be formed there, but that is Intent).
Retrieval was rejected for the opposite reason: retrieving is Recall's job,
so the name would have collided with the agent that does it. Librarian
narrows correctly — it knows the catalogue, it points at a shelf, and it
never opens the book. **Identity** was Personality in the Python prototype
and then Self; the name shrank toward the code, which is a thin cached
lookup rather than anything deserving the word "self". **Archivist** was
Consolidator, which passed the test on its own — memory consolidation is
the literal term — but named a process where its neighbours name roles, and
the archive now has a Librarian reading from it. Archivist writes what
Librarian later catalogues, and the pair explains itself.

Two names are known to be imperfect and are deliberately kept.
**Governance** is a fossil: it was accurate when the design had it present at
every handshake between agents, and that pattern was dropped. It now decides
and it bundles, and renaming toward the smaller job would bet that it stays
small. **Intent** names the role being aimed at rather than what is
demonstrably achieved, which is a reasonable thing for a name to do.

The persisted `self/identity` archive path and the shared `self` category
did not move with the Identity rename. Those are data, not names — see
`IdentityAgent.IdentityPath`.

## Bus mechanics

One topic, per-subscriber queues: `ChannelBus` maps `topic → List<ChannelWriter<Envelope>>`,
each agent (`AgentBase`) owns one `Channel<Envelope>` and one dedicated
consumer loop. `Publish` writes to every subscriber's channel and returns
immediately — unbounded channels, so a slow consumer never backs up the
publisher. Queue depth is exported as a metric so pressure is visible
before it becomes a problem.

`Topics.All` is a wildcard subscription — `ArchiveLogger` and
`ConsoleSubscriber` are plain subscribers on it, giving a complete audit
trail with zero coupling and no relay hops.

Governance's bundle timeout is itself a message: a `PeriodicTimer`
publishes a `BundleTimeout` envelope onto Governance's own topic, handled
by the same single consumer loop — so per-bundle state needs no lock,
since one queue only ever hands one message to one consumer at a time.

`BusActivityTracker` counts in-flight envelopes and exposes
`WhenIdleAsync(timeout)`, used by tests and the REPL to know when a turn
has finished propagating without polling or sleeping.

Cross-agent message order is never guaranteed and must never be assumed —
tests assert on outcome (what got published, what the archive ended up
with), not interleaving, except where an ordering is a genuine invariant
(e.g. Action never fires before Security clears it), which gets its own
explicit test.

### What travels on the bus

Every meta key in the system, who publishes it and who reads it. Envelopes
carry a `MetaBag`; `Derive()` starts a fresh bag rather than inheriting, so
anything a downstream agent needs is forwarded deliberately by whoever
publishes the next envelope. That makes this table the whole contract.

| Key | Published by | Read by |
| --- | --- | --- |
| `perception.text` | Perception, Librarian (re-published), Reflection (an idea) | Impulse, Librarian, Recall, Intent, Archivist, Hindsight |
| `perception.profile` | Perception, Librarian, Governance (on control) | Librarian, Recall, Archivist, Impulse, Governance, SseBroadcaster |
| `perception.triggered_by` | Reflection | Archivist |
| `impulse.advice` | Impulse | Intent |
| `impulse.expression` | Impulse | Governance |
| `impulse.reflex` | Impulse | Governance |
| `identity.advice` | Identity | Intent |
| `librarian.selected_pairs` | Librarian | Recall, Archivist |
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
| `control.kind` | Archivist, Governance, Reflection | Identity, Impulse |
| `reflection.mood` | Reflection | Impulse |

Three of these are read by nobody in this process. That is not a defect:
`SseBroadcaster` fans whole envelopes to connected clients, so the
`governance.*` trio on the Action envelope is the display layer's contract
and belongs to it. They are listed as read by SSE clients rather than left
looking dead.

Two keys were genuinely dead and are gone. `control.epoch_id` carried a
fresh Guid alongside every `control.kind` announcement; Identity invalidates
its persona cache on the kind alone and never compared an epoch to anything.
`perception.source_type` was set to `"idea"` on the same line that set
`perception.triggered_by` to `"self"` — one fact, published twice, with only
the second read. A key nobody reads cannot be wrong in a way anyone notices,
which is exactly why it survives audits; both come back the day something
reads them.

Note what the table shows about forwarding. Four keys — `intent.context`,
`hindsight.note_ids`, `hindsight.echo_depth`, `intent.reply` — appear under
"published by" for agents that did not originate them. `Derive()`'s fresh bag
means a key crossing more than one hop must be re-published at each one, and
Governance carries several purely so Reflection can see them after the turn
concludes. That is the cost of the fresh-bag rule, paid visibly rather than
by an inherited bag that quietly accumulates everything forever.

## Governance: decision-only

Governance has exactly three jobs, all genuine decisions over held state:
bundling the advisory fan-out, gating on Security's verdict before
Action, and revision passes. It does not route messages for other
agents and does not need to know every message type in the system — that
would make it the file every change edits. The roster it bundles against
and its timeout come from `IOptions<GovernanceOptions>`, so adding or
removing an advisory-producing agent is a config change, not a Governance
edit.

It owns one more thing that follows from bundling: **the honesty notice.**
Every substrate caller stamps `substrate.degraded` on the advisory it
publishes when its call failed (see `SubstrateHealth`), and Governance —
the only agent that sees the whole fan-out by `CorrelationId` — turns
"who was degraded or absent" into deterministic native text. Native,
because an LLM-authored apology cannot be produced by the LLM that isn't
answering. A degraded Intent replaces the reply; a degraded advisor
appends a parenthetical; a Red verdict gets neither.

## Storage: a library, not an agent

`IArchiveStore` owns the archive files, schema, and all concurrency,
including per-pair lookups (`LookupAsync(pair, profileId)`). Nothing outside
it touches a file directly.

**One file per `(Category, Topic)` pair, and the file name is the index.**
Files are named `{esc(category)}~{esc(topic)}.parquet`, so the set of known
pairs is recovered by listing the directory and decoding names — there is no
`index.parquet`. Two things follow. A write never rewrites a companion index
file, which takes a full-index Parquet rewrite off every Archivist and
Reflection write. And the index cannot drift from the data, so there is
nothing to rebuild after a manual edit; deleting a pair's last row deletes
its file, which is also how the pair leaves the index.

Names are percent-escaped down to `[A-Za-z0-9._-]` over UTF-8 bytes. Topics
are LLM-written free text, so a slash, colon or space is a matter of time;
escaping `~` inside each half is what makes the single-character separator
unambiguous. The encoding is reversible because decoding it is how the index
is read back.

Concurrency is per file: a `SemaphoreSlim` per path, not one global lock.
Recall's parallel workers touch disjoint pair files and never queue behind
each other, and a Archivist or Reflection write only blocks readers of the
one pair it touches — so the two slow agents can take as long as they need
without sitting on the next turn's critical path.

**Personal knowledge is scoped by directory.** `archive/` is the shared
tier and `archive/profiles/{id}/` is one person's own, under exactly the
same naming convention — so "the name is the index" holds unchanged inside
each directory, and today's flat archive simply *becomes* the shared tier
with no schema change and no migration. Every member of `IArchiveStore`
takes the profile whose turn it is: reads union the two tiers with the
profile winning on a `subtopic/subject/key` collision, and writes land in
the profile's directory unless the category is on the operator's
`Archive:SharedCategories` allowlist (`system` and `self` by default — the
persona's identity and its own reflections belong to nobody in particular).
`null` addresses the shared tier alone, which is the pre-profile behaviour
exactly, so the console loop and Reflection's own writes need no special
case.

Records are addressed by a five-part LLM-extracted schema —
`category/topic/subtopic/subject/key=value` (`ArchiveRecord`, `ArchivePair`)
— not deterministic keyword-derived paths. **Subtopic is data, not an
address**: every record still carries it and the picking model still reads
it, but nothing looks up by it. That is what lets one subtopic be discussed
at great length without earning its own index entry.

Both Archivist (turn facts) and Reflection (self-generated ideas) extract
records in this shape via substrate call. Rules that must hold for *every*
write live once, as prompt fragments on `ArchiveWriteStyle`, interpolated
into both writers' prompts so they can't drift apart: `TerseValue` (terse,
noise-free values) and `EnglishFields` (structural fields normalized to
English, proper nouns never translated — lookup is by pair, so the same
fact in two languages would otherwise never dedup). Both exist so a later
lookup actually intersects what got written.

## The Librarian to Recall knowledge swarm

Librarian is a **selector**, not a reader: given the current turn's text
and the full in-memory pair index, it picks up to
`LibrarianOptions.MaxSelectedPairs` `ArchivePair`s it judges relevant and
publishes them on `events.selected-pairs` — even an empty list on fallback,
so Recall always replies exactly once and Governance's bundle roster stays
static regardless of how many pairs a given turn produces. Selection is
LLM-driven rather than a deterministic keyword/bucket match, because
disambiguating something like "name of system" vs. "name of person" needs
semantic judgment, not string matching. It sees pairs rather than full
triples so the selection prompt stays short as the archive deepens.

Recall then does the reading, in two parallel phases inside one
`HandleAsync`:

1. **Read** every selected pair at once. Distinct pairs are distinct files,
   so these don't contend.
2. **Pick** — each pair's rows are split into chunks of
   `RecallOptions.RowsPerWorker`, and every chunk across every pair becomes
   one substrate call in a single flat `Task.WhenAll`.

The whole worker list is built before any substrate call starts, so a deep
pair never produces a *second wave* discovered only after the first returns.
Turn latency is one file read plus one substrate call, not N of either.

A pair is never truncated — a subtopic someone discusses at length simply
produces more chunks. `RowsPerWorker` is a *quality* limit, not a
context-window one: a candidate row costs well under 20 tokens, but a small
non-reasoning model's ability to spot the relevant entry in a flat list falls
off well before its context does. The per-turn ceiling is
`RecallOptions.MaxConcurrentRecalls` instead, and the trim to it is
breadth-first across pairs — rows are importance-ordered, so each pair's
first chunk is its most valuable, and one deep pair can't spend the whole
budget and starve the others.

Findings go straight to Governance, never back through Librarian — Librarian
and Recall are different sources of truth (parametric model knowledge vs.
stored record) and neither should become stateful across the other's
response.

## The passage corpus: what it missed, not what it knows

The archive answers *what is true*. The passage corpus answers a different
question — *what should I have looked up* — and it is the only thing in the
system that carries vectors.

Nothing in `archive/` is ever embedded. Reflection already reads a whole
batch of concluded turns; at the same time it extracts ideas, it writes a
5-15 word note on the context that batch missed, in the register of a code
review of its own retrieval ("should have read the family record before
answering"). That note, and only that note, gets an embedding. Facts stay
in exactly one place, and the vectors index the persona's judgement about
retrieval rather than a second lossy copy of the knowledge itself.

A note names the `category/topic` pairs it wishes had been read, stored as
row metadata. That is what makes a vector hit actionable: Librarian embeds
the incoming turn, cosine-matches the corpus, and merges the matched notes'
pairs into the selection alongside whatever its own selection call picked.
Pointers are resolved against the *live* index, so a pair whose last row
was deleted contributes nothing rather than sending Recall to read a file
that no longer exists. The vectors narrow; Recall's row-picking call still
does the picking — the retrieval shape is unchanged, it just starts from a
better set of leads.

The note's *text* takes a different path. `HindsightAgent` is its own
bundle-roster slot: it subscribes to `events.perception`, sweeps the same
corpus, and publishes `hindsight.notes` as an advisory that Intent reads
directly as `[Noted before: …]`, capped by `PromptCap` like every other
folded-in text. Each note arrives with its age on the front — "3 months
ago", "earlier today" — because a thought the persona has been carrying
and an echo of the last turn should not read the same.

The split is the point. Prose and facts are different substances, and
having the prose ride Librarian's envelope into Recall's slot laundered
one through the other. Intent now weighs "what the archive held" and "what
I once thought about this" as two contributions and can disagree with
either. The cost is one extra embed per turn — a local ONNX call — since
Librarian and Hindsight sweep the same corpus for different halves of it;
sharing a per-turn embedding is a listed optimisation.

**The revisit.** The previous batch's note is quoted back at the top of the
next Reflection prompt, which may rewrite it in light of what happened
since. The rewrite *replaces* the row, keeping its id and its original
timestamp — so the corpus stays roughly one row per batch instead of
accumulating drafts, and "latest" still means the newest event-series
rather than the newest edit. Writes are once per `ReflectionOptions.BatchSize`
concluded turns (10), never per turn.

Storage is a single `passages.parquet` in the archive root. One file, not
one-per-pair, because unlike the fact archive there is no address to shard
on — every query is a cosine sweep over the whole corpus, and at a row per
ten turns brute force over the in-memory cache needs no ANN index. Shared
tier only: what the persona failed to retrieve is about the persona, not
about a profile.

### The embedder

`IEmbeddingProvider` has a `bool Available`, because **not having an
embedder is a normal state, not a failure.** The default is a local ONNX
sentence-transformer (`Embedding:Provider = "onnx"`), whose ~90MB model
file is deliberately not committed. If it isn't there, the provider logs
one warning at construction, reports `Available == false`, and the swarm
runs exactly as it did before vectors existed — Reflection writes no
passages, Librarian matches none. It never marks `substrate.degraded` and
never breaks a turn, because nothing is actually degraded.

Download a model to `models/embedding/` — any BERT-family ONNX export with
its `vocab.txt` (e.g. `all-MiniLM-L6-v2`) — and it starts working with no
code change. Embeddings are mean-pooled over `last_hidden_state` and L2
normalized at write time, which is what lets cosine similarity be a plain
dot product. Setting `Embedding:Provider = "openai"` (or `"api"`, the same thing)
borrows the substrate registry's named `HttpClient` and calls an
OpenAI-compatible `embeddings` endpoint instead; a failed call returns no
vectors rather than throwing, which lands in the same "no embedder" path.
`"none"` turns the corpus off deliberately. Any other value is a startup
error rather than a silent fall back to no embedder — a typo there used to
be indistinguishable from weights that hadn't been downloaded.

Whichever provider is configured, it stamps its identity on every passage
it writes and the host refuses to start if the stored corpus disagrees;
see the roadmap's "The corpus had no model identity".

## Impulse's Critical reflex

Impulse and Intent are two independent publishers on `events.proposal`;
Security gates every proposal the same way regardless of source, and
Governance's green/yellow/red matrix needs no reflex-specific branch.
`events.action` has exactly one publisher — Governance, downstream of a
verdict — so the reflex path is a second producer, not a bypass of the
gate. The one stateful wrinkle (a reflex doesn't conclude a Critical
event, since Intent's considered reply still follows) lives in
Governance's per-event state, not as a fourth Governance job.

## Reflection's ideas and the generation guard

Reflection can publish a follow-up idea back onto `events.perception`
with `triggered_by: "self"` — downstream nothing distinguishes it from
external input. To stop an idea → conclusion → idea chain from looping
forever while paying for substrate calls, every envelope carries a
`Generation` int, incremented whenever an agent spawns a new arc from an
existing one; Reflection refuses to spawn past `ReflectionOptions.MaxIdeaGeneration`
(default 1) and skips the substrate call entirely once at the cap.

## Drive vectors: who may move them

`DriveVectors` (curiosity, fatigue, urgency, social drive, temperature) is
the persona's appraisal state, persisted as JSON at
`ImpulseAgent.DrivePath` and read by Reflection (eagerness gating) and
Governance (the `Expression()` face on a blocked reply).

Impulse also appraises that face itself on every turn — `Expression()`
on its own state, published as `impulse.expression` once the turn's
nudges have landed. Governance captures it when the bundle completes and
forwards it on the Action/Conclusion as `governance.expression`, so a
surface can draw the persona's mood without owning the vocabulary or
reading drive state directly. A block overwrites it with the face read
after the frustration nudge.

**Impulse owns every number that lands on it.** Other agents may *request*
a shift, never quantify one: Governance publishes a `Frustration` control
message on a Red verdict, Reflection attaches a mood label to its
`Reflected` control message, and Impulse maps each to a delta written in
its own source. Both arrive over `system.control`, so no agent holds a
reference to Impulse.

Two speeds, deliberately far apart:

- **Instant** (±0.05-0.15, per turn) — Impulse's own keyword triggers and
  Governance's block nudge. Python's §5.4 somatic shortcut.
- **Slow colouring** (±0.01-0.03, once per Reflection batch) — the tone of
  a whole batch of concluded turns. Python's §5.3. Reflection is the agent
  for this because it already reasons across a batch; Archivist stays a
  dumb per-turn fact writer.

The magnitude gap *is* the distinction between the two mechanisms — a test
asserts every slow delta stays under every instant one, comparing the
tables rather than pinned literals so both stay tunable.

## Prompt growth cap

Every `CognitiveAgent<T>` prompt folds in upstream agents' advisory text,
and a Reflection→Perception→Intent→Reflection loop would otherwise
re-embed the full text of every prior hop, growing the prompt generation
over generation. [`PromptCap`](../src/EciCas.Core/PromptCap.cs) caps each
piece of upstream text (240 chars, `…`-truncated) at the point it's folded
in — applied in `IntentAgent.BuildPrompt`/`AppendAdvice`,
`LibrarianAgent.BuildPrompt`, and `ArchivistAgent.ExtractFactsAsync` —
so the per-hop ceiling is fixed no matter how deep a loop runs, rather
than trying to track or trim history.

## Console output

`ConsoleSubscriber` subscribes to `Topics.All` but does not print one line
per envelope. It defaults to six lines per turn — substrate cost, what
Recall read, what Archivist/Reflection wrote, what Intent said, and
what Security blocked — via the `Console:Verbose` option; `--Verbose=true`
restores the exhaustive per-envelope trace. See `appsettings.json`'s
`Console` and `Logging:LogLevel` sections.

## Archive tool

`EciCas.ArchiveTool` is a console REPL for inspecting and manually editing
the Parquet archive directly — for testing and prototyping, when a record
needs correcting or removing without running the full agent swarm. It
reuses `ParquetArchiveStore`'s static read/write helpers rather than
duplicating Parquet I/O, so its notion of a record's shape never drifts
from `IArchiveStore`'s.

```bash
dotnet run --project src/EciCas.ArchiveTool -- <archive-directory>
```

Directory defaults to `archive` (relative to cwd) if omitted. On Windows,
prefer PowerShell or forward slashes — Git Bash/MSYS mangles a
backslash-prefixed argument (`\D`, `\E`, … read as escape sequences).

| Command | Effect |
|---|---|
| `list` | Known `category/topic` pairs, decoded from file names |
| `show <category> [topic] [subtopic]` | `[i] Topic/Subtopic/Subject/Key = Value` — same shape RecallAgent logs for its picked facts; spans every matching pair |
| `showall <category> [topic] [subtopic]` | Full field dump per row, including Importance/Domain/Timestamp |
| `del <category> <topic> <index[,index...]>` | Delete specific rows by the index `show`/`showall` printed |
| `del <category> <topic> [subtopic]` | Delete every row in the pair whose Subtopic contains the text, case-insensitive substring match |
| `help` / `exit` | — |

`del` always names one pair, since a row index is only meaningful within one
file; its second form is picked automatically when the third token isn't a
comma-separated list of integers — no separate flag needed. Deleting a
pair's last row deletes the file, which is how that pair leaves the index —
there is no `rebuild-index`, because the directory listing *is* the index.
Caveats:
arguments split on plain whitespace with no quote-awareness (fall back to
index-based `del` for a value containing a space); filter delete is a
substring match, not exact; and only one instance should point at a given
archive directory at a time, the same single-writer constraint
`ParquetArchiveStore` has for the live Host.

## Parity with the Python prototype

The C# rebuild ports `eci-cas-python-prototype`'s `current-spec.md` as
**business logic, not architecture** — messaging-plumbing differences are
by design, not drift. Every decision-shaped behavior in that spec is
implemented here except the items [`roadmap.md`](roadmap.md) lists under
Parked and Out of scope; the roadmap owns that ledger, along with the
design records for what shipped.

## Project layout

```
src/EciCas.Core/        Envelope, MetaBag, Severity, Verdict, Topics, PromptCap,
                         IAgent, IMessageBus, IArchiveStore, IPassageStore,
                         ISubstrateProvider
src/EciCas.Bus/          ChannelBus, AgentBase, BusActivityTracker
src/EciCas.Agents/       one folder per agent
src/EciCas.Substrates/   SubstrateRegistry, MockSubstrateProvider, OpenAiCompatibleSubstrateProvider
                         OnnxEmbeddingProvider, NullEmbeddingProvider
src/EciCas.Host/         Generic Host wiring, ConsoleSubscriber, ArchiveLogger, routing manifest, SSE endpoint
src/EciCas.ArchiveTool/  console REPL for inspecting/editing the Parquet archive
tests/EciCas.Tests/      xUnit
morrow-eci/              Next.js companion UI, consumes the SSE stream
```

`SubstrateProvider` config names an environment variable (`OPENAI_API_KEY`
by default) for the live provider's key — never a literal key in config.
Every `Budget:Tiers` entry defaults to `"mock"`, so running the host needs
no key.

## Verification

Tests assert outcome, not interleaving. The friction test enforced by
review: adding an agent should touch one new class, one DI line, and one
config block — if a PR adding an agent edits another agent, the
abstraction is wrong.
