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
- Reasoning calling Knowledge directly (Python) vs. Reasoning publishing
  lookup paths for Recall to answer independently (C#) — one of
  `csharp-rebuild-spec.md`'s explicitly open decisions, already resolved.

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
| §2.1 | Knowledge-swarm tier scaling — `RecallOptions.MaxPaths` caps how many of Reasoning's proposed paths get queried, alongside the existing `MaxPerPath`, scaled per tier in `appsettings.*.json` | [RecallAgent.cs](../src/EciCas.Agents/Recall/RecallAgent.cs), [RecallOptions.cs](../src/EciCas.Agents/Recall/RecallOptions.cs) |
| Dispatch #4 (batching) | Reflection batches conclusions (`ReflectionOptions.BatchSize`) and scores candidates in one call rather than firing per turn; a candidate is only reposted to `events.perception` when persona eagerness clears a threshold, otherwise archived quietly | [ReflectionAgent.cs](../src/EciCas.Agents/Reflection/ReflectionAgent.cs) — see [`roadmap.md`](roadmap.md#reflection-agent-redesign-drive-gated-batched) for the design this replaced |

## Missing

- **§3.2 Blocked-notice sequence, steps 2-4.** Only step 1 (deterministic
  Blocked template, [GovernanceAgent.cs:212-218](../src/EciCas.Agents/Governance/GovernanceAgent.cs))
  exists. No frustration nudge back to Impulse on Red (urgency +0.15,
  fatigue +0.05, temperature -0.05) — drive-vector state now exists
  ([ImpulseAgent.cs](../src/EciCas.Agents/Impulse/ImpulseAgent.cs)) so this
  is now a small logic patch, not blocked on missing state. No
  `meta.expression` emotional-word tagging. No `security_alert: true`
  cold-storage log.
- **§4.2 `is_parroting()`.** No linguistic-boundary/near-verbatim-echo check
  anywhere; Intent has no in-character refusal lead-in, just one generic
  prompt.
- **§5.3 Slow-coloring feedback (Consolidator side).** Drive-vector state
  and the Somatic instant-shift shortcut are now ported (see above); what's
  still missing is Consolidator nudging drive state gradually based on
  archived-fact sentiment/theme over time, as opposed to Impulse's
  keyword-triggered instant shifts.
- **§6.1 Watchdog.** Confirmed absent — no file matches `Watchdog`, liveness,
  or heartbeat anywhere in `src`. No 5-level escalation ladder, no
  idle-musing timer.
- **§6.2 Recovery bootstrap.** No dedicated 7-step IaC-style sequencer or
  `BootCheck` liveness step. `Program.cs` + `AgentSubstrateManifestValidator`
  + routing-manifest validation cover config-drift detection (fail loud on
  startup), which is a partial, differently-shaped analog.

## Intentionally left out

Not gaps — scoped out on purpose. Listed here so they don't get re-flagged
as oversights or picked up as follow-up work without a fresh decision to do so.

- **Messaging-plumbing differences** (see top of this doc): Python's
  synchronous recursive `publish()` vs. C#'s decoupled per-agent queues;
  Governance-as-orchestrator vs. Governance-as-bus-listener; Reasoning
  calling Knowledge directly vs. publishing lookup paths for Recall to
  answer independently. Per `csharp-rebuild-spec.md`'s explicit framing,
  the port targets business logic, not architecture — these are
  by-design divergences, not things to reconcile.
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
