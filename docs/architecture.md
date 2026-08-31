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
| Reasoning | `events.perception` | `events.advisories`, `events.selected-pairs` | cognitive |
| Recall | `events.selected-pairs` | `events.advisories` | deterministic |
| Self | `events.perception` | `events.advisories` | deterministic (archive read) |
| Governance | `events.advisories`, `events.verdict` | `events.bundle`, `events.action`, `events.conclusion` | deterministic |
| Intent | `events.bundle` | `events.proposal` | cognitive |
| Security | `events.proposal` | `events.verdict` | deterministic |
| Action | `events.action` | — | deterministic |
| Consolidator | `events.bundle` | `system.control` (`Written`) | cognitive |
| Reflection | `events.conclusion` | `events.perception` (ideas), `system.control` | cognitive |
| ArchiveLogger | `Topics.All` | — | deterministic |
| ConsoleSubscriber | `Topics.All` | — | display |

`IArchiveStore` is a **library**, not a bus citizen — see below.

Topics are named by purpose, never by recipient, so no agent ever names
another agent.

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

## Governance: decision-only

Governance has exactly three jobs, all genuine decisions over held state:
bundling the advisory fan-out, gating on Security's verdict before
Action, and revision passes. It does not route messages for other
agents and does not need to know every message type in the system — that
would make it the file every change edits. The roster it bundles against
and its timeout come from `IOptions<GovernanceOptions>`, so adding or
removing an advisory-producing agent is a config change, not a Governance
edit.

## Storage: a library, not an agent

`IArchiveStore` owns the archive files, schema, and all concurrency,
including per-pair lookups (`LookupAsync(pair)`). Nothing outside it touches
a file directly.

**One file per `(Category, Topic)` pair, and the file name is the index.**
Files are named `{esc(category)}~{esc(topic)}.parquet`, so the set of known
pairs is recovered by listing the directory and decoding names — there is no
`index.parquet`. Two things follow. A write never rewrites a companion index
file, which takes a full-index Parquet rewrite off every Consolidator and
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
each other, and a Consolidator or Reflection write only blocks readers of the
one pair it touches — so the two slow agents can take as long as they need
without sitting on the next turn's critical path.

Records are addressed by a five-part LLM-extracted schema —
`category/topic/subtopic/subject/key=value` (`ArchiveRecord`, `ArchivePair`)
— not deterministic keyword-derived paths. **Subtopic is data, not an
address**: every record still carries it and the picking model still reads
it, but nothing looks up by it. That is what lets one subtopic be discussed
at great length without earning its own index entry.

Both Consolidator (turn facts) and Reflection (self-generated ideas) extract
records in this shape via substrate call. Rules that must hold for *every*
write live once, as prompt fragments on `ArchiveWriteStyle`, interpolated
into both writers' prompts so they can't drift apart: `TerseValue` (terse,
noise-free values) and `EnglishFields` (structural fields normalized to
English, proper nouns never translated — lookup is by pair, so the same
fact in two languages would otherwise never dedup). Both exist so a later
lookup actually intersects what got written.

## The Reasoning to Recall knowledge swarm

Reasoning is a **selector**, not a reader: given the current turn's text
and the full in-memory pair index, it picks up to
`ReasoningOptions.MaxSelectedPairs` `ArchivePair`s it judges relevant and
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

Findings go straight to Governance, never back through Reasoning — Reasoning
and Recall are different sources of truth (parametric model knowledge vs.
stored record) and neither should become stateful across the other's
response.

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
  for this because it already reasons across a batch; Consolidator stays a
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
`ReasoningAgent.BuildPrompt`, and `ConsolidatorAgent.ExtractFactsAsync` —
so the per-hop ceiling is fixed no matter how deep a loop runs, rather
than trying to track or trim history.

## Console output

`ConsoleSubscriber` subscribes to `Topics.All` but does not print one line
per envelope. It defaults to six lines per turn — substrate cost, what
Recall read, what Consolidator/Reflection wrote, what Intent said, and
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
                         IAgent, IMessageBus, IArchiveStore, ISubstrateProvider
src/EciCas.Bus/          ChannelBus, AgentBase, BusActivityTracker
src/EciCas.Agents/       one folder per agent
src/EciCas.Substrates/   SubstrateRegistry, MockSubstrateProvider, OpenAiCompatibleSubstrateProvider
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
