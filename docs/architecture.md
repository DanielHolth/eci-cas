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
| Reasoning | `events.perception` | `events.advisories`, `events.lookup-paths` | cognitive |
| Recall | `events.lookup-paths` | `events.advisories` | deterministic |
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

`IArchiveStore` owns the archive file, schema, and all concurrency —
including running parallel lookups across N paths internally
(`LookupAsync(paths, maxPerPath)`). Nothing outside it touches the file
directly. Recall is a thin bus adapter in front of it: it receives
proposed lookup paths, applies budget-tier policy (how many, how deep),
calls the store, and publishes one aggregated advisory. This keeps
policy in the agent and mechanics in the store, and keeps adding a new
query shape a matter of "new thin agent + new store method" rather than
an edit to an existing one.

Consolidator writes archive records under the same significant-word
paths (`EciCas.Core.SignificantWords`) that Reasoning proposes when
querying, plus a fixed `"turn"` anchor — so a later lookup actually
intersects what got written, and every turn stays recoverable by
category even when it has no long words in it.

## Recall's fan-out

Reasoning always publishes a lookup-paths message (even an empty list on
fallback), so Recall always replies exactly once and Governance's bundle
roster stays static regardless of how many paths a given turn produces.
Findings go straight to Governance, never back through Reasoning —
Reasoning and Recall are different sources of truth (parametric model
knowledge vs. stored record) and neither should become stateful across
the other's response.

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

## Project layout

```
src/EciCas.Core/        Envelope, MetaBag, Severity, Verdict, Topics, SignificantWords,
                         IAgent, IMessageBus, IArchiveStore, ISubstrateProvider
src/EciCas.Bus/          ChannelBus, AgentBase, BusActivityTracker
src/EciCas.Agents/       one folder per agent
src/EciCas.Substrates/   SubstrateRegistry, MockSubstrateProvider, OpenAiCompatibleSubstrateProvider
src/EciCas.Host/         Generic Host wiring, ConsoleSubscriber, ArchiveLogger, routing manifest, SSE endpoint
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
