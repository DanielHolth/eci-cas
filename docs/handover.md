# Handover

Notes for whoever picks up the next session. Read this, then
[`csharp-rebuild-spec.md`](csharp-rebuild-spec.md) for the target
architecture and [`roadmap.md`](roadmap.md) for what's planned.
Replace this file's contents each session — it's a pickup point, not a
log.

## Current focus

**M1 and M2 are done.** The solution builds, all tests pass, and a
real prompt-in/reply-out turn runs end to end, with the full advisory
fan-out feeding Intent's reply:

```
Perception → {Impulse, Reasoning, Self} → Governance (bundle + verdict
gate) → Intent (real substrate call, sees the advisories) →
Security (stub, always green) → Governance → Action
```

Impulse's Critical reflex is wired per plan §3.5: on a trigger word it
publishes straight to `events.proposal` as a second publisher
alongside Intent, bypassing the considered path — Security and
Governance need no reflex-specific branch. The "don't double-conclude
a reflex + the considered reply that follows it" refinement is M3
(gating matrix) work and was deliberately not attempted here.

Try it:

```bash
dotnet test EciCas.slnx              # 20 tests
dotnet run --project src/EciCas.Host  # interactive prompt loop
```

Every hop is logged to `archive.jsonl` (written to whatever directory
the process was launched from) via `ArchiveLogger`, a plain wildcard
subscriber — nothing publishes to it directly. `ConsoleSubscriber` is
the same shape, printing instead of writing.

What exists: `src/EciCas.Core` (Envelope, MetaBag [now with `Merge`],
Severity, Verdict, Topics, IAgent, IMessageBus, IArchiveStore,
ISubstrateProvider), `src/EciCas.Bus` (ChannelBus, AgentBase,
BusActivityTracker, `CognitiveAgent<TResult>` + `FallbackPosture`),
`src/EciCas.Substrates` (MockSubstrateProvider,
OpenAiCompatibleSubstrateProvider, SubstrateRegistry routing by
`Budget:Tiers` config), `src/EciCas.Agents/{Perception,Impulse,
Reasoning,Self,Governance,Intent,Security,Action}`, `src/EciCas.Host`
(Generic Host wiring, ConsoleSubscriber, ArchiveLogger, RoutingManifest
+ startup drift validation).

`SubstrateProvider` in `appsettings.json` names an environment variable
(`OPENAI_API_KEY` by default) for the live provider's key — never a
literal key in config. Every `Budget:Tiers` entry defaults to `"mock"`,
so `dotnet run` needs no key to work.

## Next: M3 — safety and gating

Security's real rule engine (green/yellow/red gating, currently a
stub that's always green), the Critical reflex's double-conclusion
guard in Governance, severity OR-upscale-only enforcement with
Impulse's Elevated ceiling. After that, M4: `IArchiveStore` (JSONL,
then Parquet), **Recall** (thin adapter over it), **Consolidator**,
**Reflection**, persona cache with `EpochWritten` re-hydration. `Self`
is currently a fixed identity string — it becomes a real archive-backed
lookup once `IArchiveStore` exists.

## Why this happened

The Python bus (`bus/pubsub.py` in the old prototype) dispatched
synchronously and recursively instead of decoupling agents —
Consolidator and Reflection were never actually off the live reply
path despite every doc calling them "asynchronous." Daniel chose a
from-scratch C# rebuild with genuinely decoupled, independently-
listening agents over patching that in Python. Full diagnosis and
target design: [`csharp-rebuild-spec.md`](csharp-rebuild-spec.md).

The old Python prototype still exists, unmodified, in a sibling
folder/repo `eci-cas-python-prototype`, pushed to its own remote
(commit `58b350f`) as a read-only fallback reference. Nothing in this
repo depends on it.

`morrow-eci/` (the Next.js frontend/companion UI) is untouched and
stays exactly where it is — see [`roadmap.md`](roadmap.md). It's still
rendering against a mock event feed; wiring it to the real backend is
blocked on M5.

## Genuinely open, not yet designed

Carried over from the Python prototype — still unresolved, still need
a real design pass before they're buildable:

**Swappable personas.** Switching which persona is active ("which
tamagotchi am I playing with today?"). Recall should stay shared
across personas (it's "what happened," not character); Self should
not — each persona needs its own trait bank that only develops while
active. Open question: does a swap create a new Intent instance or
re-hydrate the same one from a different store? Probably wants its own
design doc before any code — this is the largest single piece of
unscoped work in the project.

**Match input to output, not just retrieve.** Self and Recall
currently answer "what does the archive say that's relevant to this
event" — a retrieval question. The sharper version is "given this
event, what do I already know that changes how I should read it" — an
inference question. Tension: archive-lookup's own design principle was
"report what the records say, not what you happen to know... never
invent a record." Pushing toward inference risks turning Recall/Self
into a second Reasoning. Needs a real design conversation.
