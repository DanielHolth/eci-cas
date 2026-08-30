# Gap analysis — Python business logic vs. C# implementation

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

## Missing

- **§3.2 Blocked-notice sequence, steps 2-4.** Only step 1 (deterministic
  Blocked template, [GovernanceAgent.cs:212-218](../src/EciCas.Agents/Governance/GovernanceAgent.cs))
  exists. No frustration nudge back to Impulse on Red (urgency +0.15,
  fatigue +0.05, temperature -0.05) — moot today since no agent holds any
  persistent drive-vector state to nudge. No `meta.expression` emotional-word
  tagging. No `security_alert: true` cold-storage log.
- **§4.2 `is_parroting()`.** No linguistic-boundary/near-verbatim-echo check
  anywhere; Intent has no in-character refusal lead-in, just one generic
  prompt (see also "Prompt composition" below).
- **§5.3 Slow-coloring feedback** and **§5.4 Somatic shortcut.** Neither can
  exist yet — there is no drive-vector state anywhere in the C# port for a
  Consolidator pass to nudge or for Impulse to shift instantly. This is the
  single largest missing piece: not a small logic patch, but a new kind of
  per-persona persistent state that doesn't exist today.
- **§6.1 Watchdog.** Confirmed absent — no file matches `Watchdog`, liveness,
  or heartbeat anywhere in `src`. No 5-level escalation ladder, no
  idle-musing timer. (Already flagged informally earlier this session.)
- **§6.2 Recovery bootstrap.** No dedicated 7-step IaC-style sequencer or
  `BootCheck` liveness step. `Program.cs` + `AgentSubstrateManifestValidator`
  + routing-manifest validation cover config-drift detection (fail loud on
  startup), which is a partial, differently-shaped analog.
- **§7.2 Budget Mode auto-latch.** Confirmed not ported — deliberately scoped
  out earlier this session; only per-event cost logging exists, not the
  spend-cap/manual/terminal/transient auto-latch to deterministic fallbacks.
- **§2.1 Knowledge-swarm tier-scaling.** `RecallOptions.MaxPerPath` is a
  single flat value, not scaled by `budget_tier`; `RecallAgent` also doesn't
  fan out to multiple parallel knowledge agents — it asks `IArchiveStore`
  once, which does its own internal N-way path lookup. Similar result,
  not tier-adjustable today.

## Agent behavior differences (from manual smoke-testing this session)

Requested explicitly: the C# agents behave noticeably differently from the
Python originals in one respect the spec review above doesn't capture —
**prompt composition compounds without bound.**

Every `CognitiveAgent<T>` prompt (`IntentAgent.BuildPrompt`,
`ReflectionAgent.BuildPrompt`) is built by literally appending upstream
agents' advisory *text* in `[Source: ...]` brackets. Advisory text itself
often already contains earlier bracketed advisories (e.g. Intent's own
reply, which Reflection then quotes verbatim as "having just said: ...").
Against the mock substrate — which echoes its input back prefixed with
`[mock:class]` rather than producing a short synthetic reply — this is
visible immediately:

```
> [mock:fast-medium] Reply to: [mock:slow-low] In one short sentence, note a
  follow-up thought ... having just said: [mock:fast-medium] Reply to: Hi
  [Impulse: no immediate concern] [Reasoning: ... Hi] [Recall: nothing on
  file] [Self: I'm ECI, here to help.] [Impulse: flagged as urgent]
  [Reasoning: ... [Recall: ... Hello there ...] ...
```

Each Reflection→Perception→Intent→Reflection loop re-embeds the full text of
every prior hop, so the prompt (and, against a mock provider, the reply) grows
generation over generation instead of staying a short, current-turn-scoped
message. Against a real substrate this mostly wastes tokens/cost rather than
visibly corrupting the reply (a real model summarizes rather than echoing),
but it's still unbounded context growth with no trim/summarize step — Python's
`current-spec.md` describes advisories as short one-line contributions folded
into a single considered-reply call, not literal string concatenation across
turns. Worth a follow-up: either cap what `AppendAdvice` includes (e.g. only
the immediate turn's advisories, never a Reflection-sourced reply) or have
`ReflectionAgent`/`IntentAgent` treat the "having just said" turn as a nested
citation rather than raw text.

## Reflection Agent — structural gap, not just a tuning knob

C#'s [ReflectionAgent.cs](../src/EciCas.Agents/Reflection/ReflectionAgent.cs)
fires on every single conclusion (no batching) and unconditionally reposts
an idea to `events.perception` every time, which reruns the *entire*
pipeline (Reasoning read, Consolidator write, Intent reply) as a second
full turn — the direct cause of the doubled console output/cost per real
message reported this session. This diverges from Python's actual batched,
at-most-one-output design (Dispatch #4).

The fix has grown into new scope (persona drive-vector state gating
whether an idea is surfaced or just archived) beyond a same-shape port —
see [`roadmap.md`'s "Reflection Agent redesign"](roadmap.md#reflection-agent-redesign-drive-gated-batched)
for the full design.

## Console verbosity fix (this session)

Separately from the above, the interactive console output was addressed
directly rather than filed as a gap: `ConsoleSubscriber` (see
[ConsoleSubscriber.cs](../src/EciCas.Host/ConsoleSubscriber.cs)) previously
printed one line per envelope on every topic. It now defaults to six lines
per turn — substrate cost, what Recall read, what Consolidator/Reflection
wrote, what Intent said, and what Security blocked — via a `Console:Verbose`
option (`--Verbose=true` restores the old exhaustive trace). See
`appsettings.json`'s `Console` and `Logging:LogLevel` sections.
