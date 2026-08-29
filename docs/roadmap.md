# ECI-CAS — Roadmap

## Active — C# rebuild, genuinely decoupled agents

This repo is now the C# rebuild only. It replaces the Python prototype
that used to live here — a bus that dispatched synchronously and
recursively instead of decoupling agents, which meant Consolidator and
Reflection were never actually off the live reply path despite every
doc calling them "asynchronous." That prototype is preserved in a
sibling folder/repo, `eci-cas-python-prototype`, pushed to its own
remote as a fallback — see [`handover.md`](handover.md).

The rebuild is a from-scratch design, not a mechanical port: one queue
+ one listener per agent, fire-and-forget publish, Governance as a
plain listener rather than a synchronous orchestrator, console as a
plain bus subscriber. Full design capture:
[`csharp-rebuild-spec.md`](csharp-rebuild-spec.md). Standing rule for
all bus work here: `AGENTS.md`'s Architecture section.

**M1 (walking skeleton) is done.** Perception → Governance → Intent →
Security → Governance → Action runs end-to-end via
`dotnet run --project src/EciCas.Host`, with `ArchiveLogger` recording
every hop to `archive.jsonl`. Next up is M2 — the cognitive layer
(`CognitiveAgent<T>`, real `ISubstrateProvider`, budget tiers, and the
Reasoning/Recall/Self/Impulse agents) — see
[`handover.md`](handover.md).

## Morrow-ECI (frontend)

`morrow-eci/` is the Next.js companion surface — a Jarvis/tamagotchi
hybrid, currently rendering against a mocked event feed
(`morrow-eci/lib/mockTurn.ts`). Once the C# backend publishes real
`system.control` / `events.*` envelopes, swap the mock feed for a real
subscription (void-observer only) and wire `ConsolidationDoodle.tsx`'s
click to a real `ingest(source_type: "ui_click", ref_event_id: ...)`
call. Blocked on the C# backend existing, not on any frontend work.

## Long-term goals

**Minimal-tier local LLM.** A free 1.8B–3B model (Phi, Qwen, or
similar) for the `minimal` budget tier, so ECI-CAS can run on-device
where cloud connectivity is unreliable. Scope TBD: fine-tuning,
quantization, latency targets.

**Android native client.** On-device minimal-tier agent running the
full 12-role system, or a remote-client mode where only Perception and
Action cross process boundaries and all reasoning stays server-side.
Stretch: iOS via shared business logic. Needs UI parity with
Morrow-ECI.

## Companion & knowledge extensions (not started)

Four capabilities for device-sharing and persistent user identity, none
built yet, all backend features to design once the C# agents exist:

**Multi-user profiles.** Multiple users per device; a new name in
conversation offers to create a profile. Each profile is a separate
knowledge graph. Surface: Morrow-ECI profile picker.

**Voice recognition for user detection.** Speaker ID as the primary
detector (continuous, harder to spoof than camera alone), camera as a
fallback for ambiguous cases. Integration point: Perception, before
Impulse fires. Needs baseline voice samples from the original user.

**Biometric + camera authentication.** Device biometrics authenticate
the original user at unlock; a different person picking up the device
triggers camera-based profile-creation. Surface: lock screen / auth
flow. Backend: a user-context field on Perception's meta.

**Diary knowledge category.** A Recall category for entries that
accumulate rather than overwrite — recurring appointments, dated
milestones — so a new doctor's visit doesn't clobber the last one.
Query: Recall surfaces diary entries in temporal order, not as
overwriting facts.

These layer on top of the core system and don't block the C# rebuild
or Morrow-ECI. Voice + biometric + camera compose as one "who is this"
pipeline feeding profile context, which diary-aware archiving then
reads:

```
biometric unlock → voice/camera check → profile context →
diary-aware knowledge archiving
```

Profiles and auth are Morrow-ECI surface features; diary is a
Recall-agent feature that can be prototyped independently.
