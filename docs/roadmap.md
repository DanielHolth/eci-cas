# ECI-CAS — Roadmap

The C# backend and its Next.js companion surface (`morrow-eci/`) are both
built and wired end to end — see [`architecture.md`](architecture.md) for
what exists. This tracks what's still ahead.

## Long-term goals

**Minimal-tier local LLM.** A free 1.8B–3B model (Phi, Qwen, or
similar) for the `minimal` budget tier, so ECI-CAS can run on-device
where cloud connectivity is unreliable. Scope TBD: fine-tuning,
quantization, latency targets.

**Android native client.** On-device minimal-tier agent running the
full agent roster, or a remote-client mode where only Perception and
Action cross process boundaries and all reasoning stays server-side.
Stretch: iOS via shared business logic. Needs UI parity with
Morrow-ECI.

## Companion & knowledge extensions (not started)

Four capabilities for device-sharing and persistent user identity, none
built yet:

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

These layer on top of the core system and don't block anything else.
Voice + biometric + camera compose as one "who is this" pipeline feeding
profile context, which diary-aware archiving then reads:

```
biometric unlock → voice/camera check → profile context →
diary-aware knowledge archiving
```

Profiles and auth are Morrow-ECI surface features; diary is a
Recall-agent feature that can be prototyped independently.

## Data quality

**Normalize archive writes to English.** Consolidator and Reflection both
write `ArchiveRecord`s straight from whatever language the turn (or the
substrate's own reply) happened to be in. A user (or a persona) switching
languages mid-conversation currently produces separate archive entries for
the same fact under different words, since lookup is keyword/path-based, not
semantic — no dedup happens across languages. Translating to English before
write (or before path/keyword extraction) would keep one fact as one entry
regardless of what language it arrived in.

## Open design questions

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
inference question. Tension: archive-lookup's own design principle is
"report what the records say, not what you happen to know — never
invent a record." Pushing toward inference risks turning Recall/Self
into a second Reasoning. Needs a real design conversation.
