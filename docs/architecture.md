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
| Reasoning | `events.perception` | `events.advisories`, `events.selected-triples` | cognitive |
| Recall | `events.selected-triples` | `events.advisories` | deterministic |
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

`IArchiveStore` owns the archive file, schema, and all concurrency,
including per-triple lookups (`LookupAsync(triple, maxRows)`). Nothing
outside it touches the file directly. An in-memory `Index` — a
`HashSet<ArchiveTriple>` of every distinct `(category, topic, subtopic)`
ever written — is hydrated once at boot from the archive and updated as
new records land, so Reasoning can select against it with zero
live-Archive reads.

Records are addressed by a five-part LLM-extracted schema —
`category/topic/subtopic/subject/key=value` (`ArchiveRecord`,
`ArchiveTriple`) — not deterministic keyword-derived paths. Both
Consolidator (turn facts) and Reflection (self-generated ideas) extract
records in this shape via substrate call, each with its own harness
prompt (`ArchiveWriteStyle.TerseValue`) instructing terse, noise-free
output, so a later lookup by triple actually intersects what got
written.

## The Reasoning → Recall knowledge swarm

Reasoning is a **selector**, not a reader: given the current turn's text
and the full in-memory triple index, it picks up to
`ReasoningOptions.MaxSelectedTriples` `ArchiveTriple`s it judges
relevant and publishes them on `events.selected-triples` — even an empty
list on fallback, so Recall always replies exactly once and Governance's
bundle roster stays static regardless of how many triples a given turn
produces. Selection is LLM-driven rather than a deterministic
keyword/bucket match, because disambiguating something like "name of
system" vs. "name of person" needs semantic judgment, not string
matching.

Recall then fans out **one parallel substrate call per selected
triple**: each call pulls up to `RecallOptions.MaxPerTopic`
importance-sorted candidate rows for that exact triple from the store,
and the model picks the handful actually relevant to the turn. Findings
go straight to Governance, never back through Reasoning — Reasoning and
Recall are different sources of truth (parametric model knowledge vs.
stored record) and neither should become stateful across the other's
response. See [`roadmap.md`](roadmap.md) for the planned next step
(collapsing what Reasoning is shown to `category/topic` pairs, pushing
subtopic resolution and row-count scaling down into Recall) once the
triple index grows past what fits comfortably in one selection prompt.

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
| `list` | Category names (one per `.parquet` file, minus `index`) |
| `show <category> [topic] [subtopic]` | `[i] Topic/Subtopic/Subject/Key = Value` — same shape RecallAgent logs for its picked facts |
| `showall <category> [topic] [subtopic]` | Full field dump per row, including Importance/Domain/Timestamp |
| `del <category> <index[,index...]>` | Delete specific rows by the index `show`/`showall` printed |
| `del <category> <topic> [subtopic]` | Delete every row whose Topic (and Subtopic, if given) contains the text, case-insensitive substring match |
| `rebuild-index` | Rescans every category file and rewrites `index.parquet` from scratch |
| `help` / `exit` | — |

`del`'s second form is picked automatically when its third token isn't a
comma-separated list of integers — no separate flag needed. Caveats:
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
