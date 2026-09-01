# Commute brainstorm

Sparring notes, not a spec. Two threads: the UX/profiles implications
(R1–R5 in [`roadmap.md`](./roadmap.md)) and how the system behaves when
the substrate layer is unreachable.

**Status.** Written against `78dc92e`, before profiles landed. Part 1 is
now largely retrospective — `4de9b65` shipped the picker, per-profile
streams and per-profile Impulse, and `d598420` plans the
Governance-owned degraded-substrate notice that Part 2 §3 argues for.
Kept as the reasoning behind those choices, not as open work. §2's
question was settled the second way: `SseBroadcaster` resolves a turn's
profile by `CorrelationId`, with a bounded map and eviction. Still open:
§5 (Reflection batches spanning profiles) and Part 2 §1, §4, §5.

Part 2's cost estimates were corrected after review against the call
sites — §1 is five edits and a refactor, not one; §5 is net-new work,
not reuse. The conclusions stand; the pricing didn't.

---

# Part 1 — UX implementation implications

## The one-line version

R1–R5 are filed as "frontend requirements", but R3 is backend-shaped and
everything else silently depends on it. Build the picker first and you
get a UI that looks like it works while every profile shares one drive
state and one turn feed.

## 1 · `profileId` is a system-wide dimension, not a UI setting

R3 adds `profileId` to `PerceiveRequest` and puts it on Perception's
meta. From there it decides:

- which `impulse/drive/{profileId}` record Impulse mutates,
- which directory Consolidator and Reflection write to,
- which pairs Recall unions in.

A field added for a header chip ends up steering the persona's emotional
state. Worth naming it as persona-scoping rather than a UI concern.

## 2 · The SSE stream is an unfiltered firehose

`SseBroadcaster` subscribes `Topics.All` and fans every envelope to every
connected client. `/api/stream?profileId=…` is therefore not a
query-string change. Two candidate shapes:

- **Propagate meta faithfully** across every hop — Perception →
  advisories → bundle → proposal → verdict → action. Correct, but any
  agent that mints a fresh envelope without copying `profileId` silently
  drops that turn out of its owner's window.
- **Resolve in the broadcaster** by `CorrelationId`, from the perception
  envelope it already saw. Localized, but makes a currently stateless
  class stateful — needs eviction/TTL.

This decision gates the rest. Neither option touches agent coupling.

## 3 · Filtering is a privacy boundary, not a convenience

Cross-profile visibility is explicitly out of scope for iteration 1, yet
the firehose means a naive implementation leaks one person's turns into
another's browser. The filter has to be server-side; hiding in React is
not a boundary. Related: no auth in iteration 1 means profile IDs are
guessable and `GET /api/profiles` is open. Acceptable for a shared
household device, not beyond it — worth stating rather than implying.

## 4 · Drive state: one const, three readers

`ImpulseAgent.DrivePath` is read by Impulse, Reflection *and*
Governance. Per-profile keying has to land in all three, resolved the
same way, with fallback to today's `impulse/drive` so single-user runs
keep working. Mechanically small; easy to half-do.

## 5 · Reflection batches can span profiles

Unresolved in the roadmap. Reflection buffers `BatchSize` turns and
emits one `mood|<label>`. If the batch mixes two people's turns, which
profile's drive does it colour? Options: partition the batch by profile
(probably right), or scope the mood to whichever profile dominated
(cheaper, wrong-feeling). Note this cuts against the stated intent that
"what warms it toward one child does not pre-color how it meets the
parent an hour later".

## 6 · Storage change is genuinely cheap

`archive/profiles/{id}/` keeps the defining property — the file name
*is* the index — intact inside each directory. No schema change, no
migration, today's flat `archive/` becomes the shared tier unchanged.
Least risky part of the iteration.

## 7 · R2's feed reset is a client-side illusion (for now)

"Switching resets the turn feed" only means anything once the
server-side filter exists. `localStorage` persistence also means a
shared device keeps the last person's identity until someone explicitly
switches — exactly the gap voice/camera detection later closes. An
explicit "not me" affordance may be worth more than pretending the
picker solves attribution.

## 8 · R4 is a coupling trap, already flagged

The avatar ring renders *around* the Impulse-coloured circle. Impulse
owns expression colour, the same discipline as "an agent may request a
shift, never quantify one". If avatar choice ever tints the circle, the
UI has taken ownership of persona state.

Related, already shipped: `useEciStream.ts`'s `deriveExpression` invents
an expression vocabulary client-side from severity + reflex text, with a
comment admitting it's a mock-era placeholder. Same violation in
miniature. R4 shipped without fixing it, so this is a live
inconsistency rather than a blocker — Impulse should publish an
expression instead of the client guessing one.

## Sequencing that falls out

1. Decide propagation strategy (§2).
2. `profileId` meta seam.
3. Server-side stream filter.
4. Archive profile scoping · drive keying (independent of each other).
5. Reflection batch partitioning.
6. Profile registry API.
7. Picker UI, then avatar ring.

1–3 are the spine. 6 is independently shippable. 7 is cosmetic on top
and misleading without 3.

## Coupling check

Nothing here reintroduces coupling: new HTTP endpoints, one meta field,
no agent-to-agent calls, no blocking `Publish()`. The real risk is
different — a *hidden global* (`profileId`) that several agents read
independently and can quietly disagree about.

---

# Part 2 — Graceful failure when the substrate is unreachable

## What happened

`--Tier=Default --Verbose=true` on a machine that briefly couldn't
resolve DNS. Reasoning, Consolidator and Intent all threw
`HttpRequestException` (`WSAHOST_NOT_FOUND`), each logged a warning and
fell back. The turn then concluded normally — proposal → verdict →
conclusion, all Neutral, verdict green — and the user got
`> I'm having trouble thinking that through right now.`

The network recovered on its own, which is the point: this is the
transient case, and it's the one graceful handling exists for.

## The finding

**Total cognitive-layer failure is indistinguishable from a normal
turn.** The entire cognitive tier was dead and the system produced a
fluent, plausible, in-persona reply.

## What to do about it, in order of argument

**1 · Separate "no substrate" from "substrate said something unusable."**
The root gap. `FallbackPosture.Open` collapses DNS failure, 500, timeout
and unparseable-completion into one identical fallback envelope.
Everything below is blocked on this distinction existing. Put the
outcome on the published envelope's meta — the fallback result is
unchanged, but downstream can finally tell *why*.

**This is not a one-place fix, and a plan that says "`CognitiveAgent`
marks the fallback" is wrong.** There are five substrate callers across
two code paths. Only `Intent` and `Reasoning` extend `CognitiveAgent<T>`,
and `ReasoningAgent` overrides `HandleAsync` and calls
`_substrate.CompleteAsync` itself — reimplementing the base
try/catch/log/publish, so its `Fallback => Open` is read only for the
log message. The base implementation therefore covers **one agent in
five**. `Recall`, `Reflection` and `Consolidator` each hold their own
try/catch. Marking only the base path would look done while three
agents degraded as silently as before.

The dedup isn't free, though `Publish` is not why: Reasoning calls it
with exactly the base signature. The real blockers are that
`ParseResult(SubstrateResult)` gets no access to the archive `index`
that `ParsePairs(text, index)` needs, and that the empty-index early
return fires before a prompt is built, which the base flow has no hook
for. The likely shape is still the base class handing subclasses a
failure classification, rather than folding subclasses back into it.

**2 · Make degradation visible in the envelope, not just the log.**
Today the only trace is an ILogger warning that `--Verbose=true` happens
to reveal. `ArchiveLogger`, `ConsoleSubscriber` and `SseBroadcaster` are
already wildcard subscribers, so a meta field costs nothing to
distribute and every surface gets it free. No new topic, no new agent,
no coupling.

**3 · Let Governance decide what a degraded turn sounds like.**
The real design question. Governance already owns "deterministic notice
instead of an improvised reply" for a Red verdict. A turn where the
whole cognitive tier fell back is the same category: the system should
say *"I can't reach my thinking right now"* as a deterministic notice,
not improvise as though it were a considered reply. It already holds the
bundle and the per-event state to see how many advisories came back
degraded — so this stays in the one agent whose job is decisions.

**4 · Circuit-break per provider, not per agent.**
Not justified by the incident above — `WSAHOST_NOT_FOUND` fails
instantly, so nothing timed out in that run. The case it earns its keep
on is the *hang*: a host that resolves but stalls on TLS or a slow
provider, where each of five agents waits out a full timeout every turn.
Secondary benefit even when failure is fast: during a known outage,
stop making five doomed calls per turn. Belongs in
`OpenAiCompatibleSubstrateProvider`; agents shouldn't know about network
topology.

**5 · Don't let Consolidator and Reflection silently drop the turn.**
Both fall back by skipping, not one: `ExtractFactsAsync` catches and
returns an empty set, and Reflection abandons the entire flush —
"nothing pushed, nothing archived," per its own comment, which cites
Consolidator as the precedent. So an outage stops the persona
remembering *and* stops it keeping its own insights, from two
independent paths, with no distinct signal from either. Recall skips as
well but writes nothing, so it only loses grounding for that turn.

This is net-new work, not reuse. The README claims Consolidator ships
with `UseSubstrate:false` and falls back to "its deterministic keyword
write"; neither is true. `UseSubstrate` appears in no `appsettings`
file (the property exists in code, defaulting true), and no
deterministic writer exists to fall back to. **The README needs fixing
independently of this item.**

**6 · Skip the startup reachability probe.**
Tempting, since manifest drift already fails loud. But it only catches
"network down at boot", gives false confidence when it passes, and adds
a startup dependency on the internet. The circuit breaker covers the
same ground and handles the transient case too.

## Ordering

1 → 2 → 3 is one coherent thread (classify, propagate, decide). 4 and 5
are independent and can land separately.

## Smaller notes

- Telemetry only logs on success — the latency/token/cost line sits
  inside the `try`, so a failed turn leaves no record of what it
  attempted or what it cost in wall-clock.
- Asymmetry worth naming: manifest drift fails loud before the bus
  starts, but a `Tier` pointing at live providers never verifies
  reachability. The strictest-validated config is the one that silently
  degrades at runtime.
