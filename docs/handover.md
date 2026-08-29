# Handover

Notes for whoever picks up the next session. Read this, then
[`csharp-rebuild-spec.md`](csharp-rebuild-spec.md) for the target
architecture and [`roadmap.md`](roadmap.md) for what's planned.
Replace this file's contents each session — it's a pickup point, not a
log.

## Current focus

**M1 (walking skeleton) is done.** The solution builds, all tests pass,
and a real prompt-in/reply-out turn runs end to end:

```
Perception → Governance (bundle + verdict gate) → Intent (mock) →
Security (stub, always green) → Governance → Action
```

Try it:

```bash
dotnet test EciCas.slnx              # 9 tests
dotnet run --project src/EciCas.Host  # interactive prompt loop
```

Every hop is logged to `archive.jsonl` (written to whatever directory
the process was launched from) via `ArchiveLogger`, a plain wildcard
subscriber — nothing publishes to it directly. `ConsoleSubscriber` is
the same shape, printing instead of writing.

What exists: `src/EciCas.Core` (Envelope, MetaBag, Severity, Verdict,
Topics, IAgent, IMessageBus, IArchiveStore, ISubstrateProvider),
`src/EciCas.Bus` (ChannelBus, AgentBase, BusActivityTracker),
`src/EciCas.Agents/{Perception,Governance,Intent,Security,Action}`,
`src/EciCas.Host` (Generic Host wiring, ConsoleSubscriber,
ArchiveLogger, RoutingManifest + startup drift validation).

## Next: M2 — cognitive layer

Per the foundation plan (§5): `CognitiveAgent<T>` (substrate call +
latency/token/cost diagnostics + budget failure recording),
`ISubstrateProvider` + a registry + `MockSubstrate`, budget tiers via
`IOptions<T>`, one live OpenAI-compatible HTTP provider. New agents:
**Reasoning**, **Recall** (thin adapter over `IArchiveStore`), **Self**,
**Impulse** (including the Critical reflex publishing straight to
`events.proposal`).

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
