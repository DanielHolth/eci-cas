# ECI-CAS C# Rebuild — Genuinely Decoupled Agents

Design capture for a new milestone (2026-08-29): a from-scratch C# project,
built in its own chat context, that replaces this repo's Python
implementation's core weakness rather than porting it forward.

## Why this exists

The Python bus (`bus/pubsub.py`) looks like pub-sub — `publish(topic, envelope)`,
agents `subscribe()` to topics — but it is not decoupled. `publish()`
dispatches synchronously and recursively: it calls every subscriber in
turn, on the calling thread, and does not return until each subscriber
(and everything *that* subscriber's handling triggers) has fully
finished. The result is one long call stack wearing a message-bus API.

This was invisible for most of the project's life because every agent's
substrate call was roughly the same speed. Dispatch #5 (see
`docs/roadmap.md`) broke that illusion: giving Consolidator/Reflection a
slower, smarter model (`slow-medium`, ~2sec+ TTFT) exposed that
`agents/governance/agent.py`'s `emit()` publishes to Consolidator
*before* it publishes to Intent — so on the synchronous bus, Consolidator's
now-slow substrate call sits directly in the middle of the live reply
path and blocks the human-facing answer, despite every design doc calling
Consolidator "asynchronous, off the live path." It never was, in the only
sense that matters: wall-clock blocking.

Daniel's own architectural picture (dictated 2026-08-29, captured here
verbatim in spirit — agent names below are as originally dictated;
Sensory→Perception, Analytics→Reasoning, Knowledge→Recall,
Personality→Self were renamed afterward, see the roster table in the
foundation plan):

> Sensory gets an input and pushes one message to a topic. Governance
> picks that up and sends new messages to Impulse, Analytics, and
> Personality — topic/subscriber or individual queues, doesn't matter.
> Impulse and Personality reply to Governance. Analytics may delegate to
> Knowledge itself, or that can go through Governance too. Once
> Governance has gathered the facts, it sends messages to Intent *and*
> Consolidator. All of these have their own individual listeners.
> Console just prints what's on the bus — Governance could even publish
> to it like it's just another agent.
>
> "I hate synchronous, hard-coupled systems."

That is the target. The Python system is not being bent to fit it
retroactively — see `docs/current-spec.md`'s new header note. This is a
clean rebuild, in C#, as its own milestone, in its own chat context.

**Update (2026-08-30): M1 walking skeleton is built and this design is
proven end-to-end.** Perception → Governance (bundle + verdict gate) →
Intent (mock) → Security (stub) → Governance → Action runs a real
prompt-in/reply-out turn with a full `archive.jsonl` audit trail. What
follows is the original design capture; where it says "the new project
should decide," that decision is now recorded — see "Open decisions,
resolved" below.

## Target architecture

**One queue and one worker per agent, no exceptions.** Each agent owns
an inbound queue (`Channel<Envelope>` is the natural .NET fit) and a
single dedicated consumer loop (or a small worker pool, if an agent's
own contract calls for internal parallelism — e.g. Reasoning fanning out
to N Recall workers). `IMessageBus.Publish()` is fire-and-forget: it
enqueues onto every subscriber's channel and returns immediately. No
publish call may ever await a subscriber's handling.

**Governance stops being a call stack.** It becomes an agent like any
other: a listener with its own queue, reacting to whatever arrives
(a worker's report, Security's verdict, Intent's advice) and publishing
whatever that reaction implies. What currently happens as nested
synchronous calls inside `emit()`/`on_event()` becomes: Governance
receives a worker report, updates its own per-event state, and if that
state is now complete, publishes to Intent and to Consolidator as two
independent messages — neither one blocks on the other, and neither
blocks Governance's own loop from picking up its next message.

**Per-event state needs its own concurrency story.** The Python
`BundleBuffer`/`EventState` design (`agents/governance/buffer.py`)
assumes a single lock serializes every read-modify-write, because
multiple worker threads can report for the same `event_id` concurrently
but Governance's own logic is not itself concurrent internally. In the
C# rebuild, Governance's single consumer loop already gives it that
serialization for free (only one message is being handled at a time on
its own queue) — so the lock has no C# equivalent to port; document why,
don't recreate a `lock` out of habit.

**Console is a subscriber, not a display hook.** `tools/console.py`
today has to reach into internals (`eco.consolidator.on_write = ...`,
`bus._on_publish = ...`) because Consolidator/Reflection never publish a
bus message at all — see `agents/consolidator/base.py`'s and
`agents/reflection/base.py`'s docstrings, both explicit design choices
in Python ("no bus message for UI"). The C# rebuild inverts this:
Consolidator and Reflection publish real envelopes (a `Written` /
`Reflected` type on a topic like `system.control`, matching what
`ConsolidationWritten` already does for Morrow-ECI), and the console is
just one more subscriber that prints whatever crosses the topics it
cares about. No agent needs to know the console exists. This also means
Morrow-ECI's frontend and a console session are structurally the same
kind of subscriber, differing only in what they render — no special
casing either needs.

**No implicit publish-order-implies-execution-order.** The current
Python code has at least one comment (`agents/governance/agent.py`,
`emit()`'s BUNDLE branch) that chooses publish order *specifically* to
control what a debug trace looks like, because the synchronous bus makes
order-of-publish equal order-of-execution. That equivalence must not
exist in the C# rebuild — if a trace needs a specific display order,
that is the display layer's job (sort by timestamp/sequence number after
the fact), never something achieved by sequencing `Publish()` calls.

**Ordering guarantees the rebuild does still need, explicitly designed
rather than accidental:**
- Per-event-id causal order within a single agent's own queue (Governance
  must see a worker's report before it can decide the bundle is ready —
  trivial, since one queue only delivers to one consumer in the order
  enqueued).
- Cross-agent ordering is NOT guaranteed and must not be assumed. Tests
  that currently assert a byte-identical trace (Phase 0's stated exit
  criterion) do not port as-is — the C# test suite should assert on
  *outcome* (what each agent published, what state Archive ended up
  with) rather than exact interleaving, unless a specific ordering is a
  real contract (e.g., "Action never fires before Security clears it" —
  that one is a genuine invariant and should have a genuine test for it,
  independent of message-arrival timing).

## What ports as-is vs what doesn't

**Ports as business logic, not architecture:** every agent's actual
decision-making — Impulse's appraisal, Reasoning's keyword reasoning,
Intent's persona contract, Security's rule engine, the severity
OR-upscale-only rule, the green/yellow/red gating semantics, budget
tiers and substrate classes. `docs/current-spec.md` remains the
reference for what each agent is supposed to decide; only *how messages
move between them* is being rebuilt.

**Does not port as-is:** `bus/pubsub.py`'s dispatch model, Governance's
role as a synchronous orchestrator, any code that relies on one agent's
handling finishing before another's starts, and any test that asserts
exact cross-agent interleaving.

## Open decisions, resolved

These were left open here; the foundation plan (see `AGENTS.md` → Docs)
has since settled all three:

- Reasoning's fan-out to Recall routes through the bus (`events.lookup-paths`
  → Recall → `events.advisories`), never agent-to-agent, and Recall
  aggregates its N-way parallel lookup into exactly one report so
  Governance's bundle roster stays static.
- In-process `Channel<T>` + `IHostedService` per agent, one deployable.
  No external broker — not needed yet.
- Governance's per-event bundle state does not need to survive a
  restart; it lives only for the duration of one in-flight turn.
  Durable state (what a turn concluded) is Archive's job, not
  Governance's — consistent with Governance staying decision-only.

## Relationship to existing C# scaffolding

M1 (the walking skeleton) is built: `src/EciCas.Host`, `EciCas.Core`,
`EciCas.Bus`, and `EciCas.Agents/{Perception,Governance,Intent,Security,Action}`
all exist and pass `dotnet test EciCas.slnx`. `.github/copilot-instructions.md`
describes the bus correctly (queue-per-agent, fire-and-forget) and lists
`EciCas.Host` in its project layout.
