# Gap analysis — Python business logic vs. C# implementation

**Note:** not everything below is an oversight. See
[Intentionally left out](#intentionally-left-out) for behaviors that were
deliberately scoped out of the C# port rather than missed — don't file
follow-up work against those without checking that section first.

Compares the C# rebuild against `eci-cas-python-prototype/docs/current-spec.md`,
which `csharp-rebuild-spec.md` names as the canonical reference for what each
agent is *supposed to decide*. `docs/archive/ECI-spec-v0-40.md` is a
superseded/archived draft (older agent naming, older substrate-class names
predating the `fast-*`/`slow-*` rename) and is not used as the baseline here.

Per `csharp-rebuild-spec.md`'s explicit framing — **"ports as business logic,
not architecture"** — messaging-plumbing differences are excluded below by
design, not counted as gaps:

- Python's synchronous recursive `publish()` vs. C#'s decoupled per-agent
  queues (`ChannelBus`, one queue/one worker per agent, fire-and-forget).
- Governance-as-orchestrator (Python) vs. Governance-as-bus-listener (C#).
- Reasoning calling Knowledge directly (Python) vs. Reasoning selecting
  archive triples for Recall to fan out and answer independently (C#) —
  one of `csharp-rebuild-spec.md`'s explicitly open decisions, already
  resolved. See [`architecture.md`](architecture.md#the-reasoning--recall-knowledge-swarm).

## Ported / matches spec

| Spec section | Behavior | C# location |
|---|---|---|
| §3.1 | Gating matrix: Green→Action, Yellow→one revision then proceeds regardless, Red→immediate deterministic Block, no revision | [GovernanceAgent.cs](../src/EciCas.Agents/Governance/GovernanceAgent.cs) |
| §2.3 | Severity OR-upscale; reflex severity hard-capped at Elevated; only Perception/Reasoning may tag Critical | [ImpulseAgent.cs](../src/EciCas.Agents/Impulse/ImpulseAgent.cs) |
| §3.3 (partial) | Critical reflex fast-path — Impulse double-publishes straight to `events.proposal`; Governance's `isReflex` flag lets the reflex reply reach Action without concluding the event, so Intent's considered reply still lands after | [ImpulseAgent.cs](../src/EciCas.Agents/Impulse/ImpulseAgent.cs), [GovernanceAgent.cs](../src/EciCas.Agents/Governance/GovernanceAgent.cs) — mechanism differs from Python's "discard incomplete Analytics/Personality slots," but achieves the same practical effect (reflex reaches the user without waiting on the bundle) |
| §5.1 | Persona caching, invalidated on a write epoch | [SelfAgent.cs](../src/EciCas.Agents/Self/SelfAgent.cs) — lazily hydrated on first request rather than at bootstrap, but zero live-Archive-reads-per-event once warm |
| §5.2 | Consolidator batches records, flushes at a threshold, announces the epoch | [ConsolidatorAgent.cs](../src/EciCas.Agents/Consolidator/ConsolidatorAgent.cs) |
| §4.1 | Fallback posture: non-gating fails open, gating fails closed | `FallbackPosture.Open`/`Closed` on [CognitiveAgent.cs](../src/EciCas.Bus/CognitiveAgent.cs) — Intent is Open, Reflection is Closed |
| §7.3 | Manifest-driven substrate swap | `Substrates:Providers`/`Classes` + tier files |
| Dispatch #4/#5 | Reflection agent (12th role); `fast-*`/`slow-*` substrate naming | present |
| §5.3 (partial), §5.4 | Persistent drive-vector state (`ImpulseAgent.DrivePath`), Reflection eagerness gating reading it, and a keyword-triggered Somatic-shortcut nudge on approval/disapproval phrases | [ImpulseAgent.cs](../src/EciCas.Agents/Impulse/ImpulseAgent.cs), [ReflectionAgent.cs](../src/EciCas.Agents/Reflection/ReflectionAgent.cs) — see "Reflection Agent" below for what §5.3's Consolidator-side slow-coloring feedback still lacks |
| §2.1 | Knowledge-swarm tier scaling — `ReasoningOptions.MaxSelectedTriples` caps how many triples Reasoning selects, `RecallOptions.MaxPerTopic` caps candidate rows per triple, both scaled per tier in `appsettings.*.json` | [ReasoningAgent.cs](../src/EciCas.Agents/Reasoning/ReasoningAgent.cs), [RecallAgent.cs](../src/EciCas.Agents/Recall/RecallAgent.cs) |
| §3.2 | Blocked-notice sequence, all four steps — deterministic Blocked template, frustration nudge to Impulse on Red via `system.control` (urgency +0.15, fatigue +0.05, temperature -0.05), `meta.expression` emotional-word tagging, and a `security_alert` cold-storage record | [GovernanceAgent.cs](../src/EciCas.Agents/Governance/GovernanceAgent.cs), [ImpulseAgent.cs](../src/EciCas.Agents/Impulse/ImpulseAgent.cs), [DriveVectors.cs](../src/EciCas.Agents/Impulse/DriveVectors.cs) |
| Dispatch #4 (batching) | Reflection batches conclusions (`ReflectionOptions.BatchSize`) and scores candidates in one call rather than firing per turn; a candidate is only reposted to `events.perception` when persona eagerness clears a threshold, otherwise archived quietly | [ReflectionAgent.cs](../src/EciCas.Agents/Reflection/ReflectionAgent.cs) — see [`roadmap.md`](roadmap.md#reflection-agent-redesign-drive-gated-batched) for the design this replaced |

## Missing

- **§5.3 Slow-coloring feedback.** Drive-vector state and the Somatic
  instant-shift shortcut are ported (see above); what's still missing is
  gradual drive-state coloring from archived-fact sentiment/theme over
  time, as opposed to Impulse's keyword-triggered instant shifts. **This
  now belongs to Reflection, not Consolidator** — Consolidator stays a dumb
  writer of new facts, and Reflection (which already reads drive state to
  gate its push-vs-write decision) is the agent that reasons over a batch
  and can color Impulse back. See
  [`roadmap.md`](roadmap.md#reflection-colors-impulse-slow-coloring-feedback).

## On ice

Real gaps, deliberately parked. Not scoped out forever — revisit when the
named condition holds, not before.

- **§6.1 Watchdog.** Confirmed absent — no file matches `Watchdog`, liveness,
  or heartbeat anywhere in `src`. No 5-level escalation ladder, no
  idle-musing timer. Parked until the destination platform is known, or
  until the running system actually proves flaky in practice — whichever
  comes first. Designing a liveness ladder before knowing what it runs on
  would be guesswork.
- **§6.2 Recovery bootstrap.** No dedicated 7-step IaC-style sequencer or
  `BootCheck` liveness step. `Program.cs` + `AgentSubstrateManifestValidator`
  + routing-manifest validation cover config-drift detection (fail loud on
  startup), which is a partial, differently-shaped analog. Parked, and when
  revived it should be scoped wider than the Python original: one sequencer
  that doubles as an **installer** — provisioning a missing local LLM and
  any missing agents, not just restarting dead ones. That makes it heavily
  platform-dependent, so it waits on the same platform decision the
  Watchdog does.

## Obsoleted by the C# architecture

Spec behaviors with nothing left to guard — the structure that made them
necessary no longer exists. Not gaps, and not "scoped out" either.

- **§4.2 `is_parroting()`.** The check exists in Python to stop Intent
  echoing *Analytics'* raw recommendation back to the user — a real risk
  there, because Analytics handed Intent advisory prose. In C#,
  `ReasoningAgent` is a pure selector: it returns
  `(category, topic, subtopic)` triples and emits no advisory text at all
  (see [`architecture.md`](architecture.md#the-reasoning--recall-knowledge-swarm)).
  There is no analytical sentence for Intent to parrot, so the
  near-verbatim-echo check guards nothing. The related refusal-lead-in
  constraint is also moot: Governance appends the Blocked text
  deterministically in native code
  ([GovernanceAgent.cs](../src/EciCas.Agents/Governance/GovernanceAgent.cs)),
  so Intent never gets the chance to soften a block.

## Intentionally left out

Not gaps — scoped out on purpose. Listed here so they don't get re-flagged
as oversights or picked up as follow-up work without a fresh decision to do so.

- **Messaging-plumbing differences** (see top of this doc): Python's
  synchronous recursive `publish()` vs. C#'s decoupled per-agent queues;
  Governance-as-orchestrator vs. Governance-as-bus-listener; Reasoning
  calling Knowledge directly vs. selecting archive triples for Recall to
  fan out and answer independently. Per `csharp-rebuild-spec.md`'s
  explicit framing, the port targets business logic, not architecture —
  these are by-design divergences, not things to reconcile.
- **§7.2 Budget Mode auto-latch.** Deliberately scoped out — only
  per-event cost logging exists (`ISubstrateProvider` results log estimated
  cost at default log level), not the spend-cap/manual/terminal/transient
  auto-latch to deterministic fallbacks. Revisit only if real substrate
  spend becomes a concern worth automating around.

## Prompt composition growth — resolved

Previously flagged from manual smoke-testing: every `CognitiveAgent<T>`
prompt (`IntentAgent.BuildPrompt`, `ReflectionAgent.BuildPrompt`,
`ConsolidatorAgent.ExtractFactsAsync`) built its prompt by literally
appending upstream agents' advisory *text*, which could itself already
contain earlier bracketed advisories (e.g. Intent's reply, which Reflection
then quoted verbatim). Each Reflection→Perception→Intent→Reflection loop
re-embedded the full text of every prior hop, so the prompt grew generation
over generation instead of staying a short, current-turn-scoped message.

Fixed by [PromptCap.cs](../src/EciCas.Core/PromptCap.cs): every piece of
upstream text is capped (240 chars, `…`-truncated) at the point it's folded
into a prompt — the ceiling per hop stays fixed no matter how many
generations deep a loop runs, rather than trying to track or trim history.
Applied at `IntentAgent.BuildPrompt`/`AppendAdvice`, `ReasoningAgent.BuildPrompt`,
and `ConsolidatorAgent.ExtractFactsAsync`.

## Reflection Agent — resolved (drive-gated, batched)

C#'s [ReflectionAgent.cs](../src/EciCas.Agents/Reflection/ReflectionAgent.cs)
previously fired on every single conclusion (no batching) and unconditionally
reposted an idea to `events.perception` every time, rerunning the *entire*
pipeline as a second full turn — the direct cause of doubled console
output/cost per real message. Now batches conclusions
(`ReflectionOptions.BatchSize`), scores candidates in one substrate call, and
only reposts the best-scored idea when persona eagerness (read from
`ImpulseAgent.DrivePath`) clears `EagernessThreshold` and idea generation is
below `MaxIdeaGeneration` — otherwise the candidate is archived quietly. See
[`roadmap.md`'s "Reflection Agent redesign"](roadmap.md#reflection-agent-redesign-drive-gated-batched)
for the design this implements.

## Console verbosity fix

Separately from the above, the interactive console output was addressed
directly rather than filed as a gap: `ConsoleSubscriber` (see
[ConsoleSubscriber.cs](../src/EciCas.Host/ConsoleSubscriber.cs)) previously
printed one line per envelope on every topic. It now defaults to six lines
per turn — substrate cost, what Recall read, what Consolidator/Reflection
wrote, what Intent said, and what Security blocked — via a `Console:Verbose`
option (`--Verbose=true` restores the old exhaustive trace). See
`appsettings.json`'s `Console` and `Logging:LogLevel` sections.
