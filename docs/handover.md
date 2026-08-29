# Handover

Notes for whoever picks up the next session. Read this, then
[`csharp-rebuild-spec.md`](csharp-rebuild-spec.md) for the target
architecture and [`roadmap.md`](roadmap.md) for what's planned.
Replace this file's contents each session — it's a pickup point, not a
log.

## Current focus

**This repo is now the C# rebuild, full stop.** The Python prototype
that used to live here (`agents/`, `bus/`, `budget/`, `substrates/`,
`tools/`, `tests/`, `manifests/`, plus all the Python-era docs in
`docs/archive/`, `docs/current-spec.md`, `docs/dispatch.md`) has been
removed from this folder entirely. It still exists, unmodified, in a
sibling folder **`eci-cas-python-prototype`**, and the last Python
state is pushed to its own remote (commit `58b350f`, "Final prototype
of python before converting to C#") — that's the fallback if anything
from the old system needs to be referenced or re-derived. Nothing in
this repo depends on it; treat it as read-only reference, not a
dependency.

`morrow-eci/` (the Next.js frontend/companion UI) is untouched and
stays exactly where it is — see [`roadmap.md`](roadmap.md).

## Why this happened

The Python bus (`bus/pubsub.py` in the old prototype) dispatched
synchronously and recursively instead of decoupling agents —
Consolidator and Reflection were never actually off the live reply
path despite every doc calling them "asynchronous." Daniel chose a
from-scratch C# rebuild with genuinely decoupled, independently-
listening agents over patching that in Python. Full diagnosis and
target design: [`csharp-rebuild-spec.md`](csharp-rebuild-spec.md).

## Where things stand (this repo)

No C# code exists yet — this repo currently holds only docs
(`docs/`), the frontend (`morrow-eci/`), and repo scaffolding
(`.github/copilot-instructions.md` for C# style/architecture
conventions). The next real step is standing up the actual C# solution
per `csharp-rebuild-spec.md`'s target architecture and
`.github/copilot-instructions.md`'s project layout
(`EciCas.Core` / `Bus` / `Agents` / `Substrates` / `Tests`).

## Genuinely open, not yet designed

Carried over from the Python prototype — still unresolved, still need
a real design pass before they're buildable:

**Swappable personas.** Switching which persona is active ("which
tamagotchi am I playing with today?"). Knowledge should stay shared
across personas (it's "what happened," not character); Personality
should not — each persona needs its own trait bank that only develops
while active. Open question: does a swap create a new Intent instance
or re-hydrate the same one from a different store? Probably wants its
own design doc before any code — this is the largest single piece of
unscoped work in the project.

**Match input to output, not just retrieve.** Personality and
Knowledge currently answer "what does the archive say that's relevant
to this event" — a retrieval question. The sharper version is "given
this event, what do I already know that changes how I should read
it" — an inference question. Tension: archive-lookup's own design
principle was "report what the records say, not what you happen to
know... never invent a record." Pushing toward inference risks turning
Knowledge/Personality into a second Analytics. Needs a real design
conversation.
