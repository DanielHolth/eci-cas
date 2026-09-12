# ECI-CAS — Roadmap

The active backlog. [`architecture.md`](architecture.md) covers what
exists, [`roadmap-history.md`](roadmap-history.md) covers what's shipped or
settled, and [`product.md`](product.md) covers what's sold.

The inverted archive is the only read path: utterances are ground truth,
facts are derived, and `ConsultAgent` sweeps them by cosine. The Librarian,
Recall, Cataloger and the pair-store retrieval path are gone.

---

# Next

## Latency

The critical path is Consult → Intent.

- **Stream Intent's tokens.** This is the largest perceived win. Red must
  never reach Action, so start streaming only after the first sentence has
  cleared `SecurityRuleSet`.

## Grounded interiority

The rule: surface interiority only where something actually happened. Each
item below is an event the system already detects and then discards.

- **Notice when a fact changes.** Supersession already knows. Carry the old
  value forward, so Morrow can say "you said Oslo before".
- **Salience decay.** Age down the retrieval weight unless a fact is touched
  again. Nothing is deleted.
- **Think while idle.** Fire Reflection on silence, so it thinks rather
  than speaks. Keep the generation cap and a ceiling on notes per idle
  period.
- **Use `EchoDepth`.** Reflection reads it, but no reply is shaped by it
  yet. Log what a damper would suppress before letting it act.

## Level-ups (not started)

**XP.** One XP per extracted fact, capped at 3 per turn. Leaving level L
costs `2·L` XP, so reaching level n takes `n(n-1)` facts in total (90 for
level 10). XP counts extracted facts, so it rides on
`Utterances:ExtractorEnabled`, which every tier file turns on.

**State.** Per profile, on the backend, next to a global turn counter.

**Schedule.** Lives in `levels.json` on the server. Each level lists what
it unlocks and what it forces.

**Cog.** One tooth per level. After 48 teeth, each further level turns one
tooth gold, starting from the first; the cog is fully gold at level 96.

**Unlocks.** Each unlock is animated, with one line explaining what Morrow
gained. Even levels unlock features and odd levels change the avatar.

| Lvl | Unlocks |
|---|---|
| 1 | Dialog only. The avatar is an empty circle. Tier forced to Pro. Reflection off. |
| 2 | Left side panel: what Morrow has learned. |
| 3 | Pupil that follows the pointer. |
| 4 | Speech: the voice dropdown, with the default voice picked. |
| 5 | Aperture blades. |
| 6 | Reflection: the first reflection fires and pushes its top note to `perception.self`. After that it runs every 5 turns. |
| 7 | Expressions. |
| 8 | Debug panel, without the knobs. |
| 9 | Speaking shockwaves. |
| 10 | Tier picker. Forced to Free: a deliberate nerf after a taste of Pro. |
| 11 | Inner layers. |
| 12 | Dark/light background. Forced to dark. |
| 13 | Hue phasing. |
| 14 | MaxSentences knob. |
| 15 | Wobble and idle shake. |
| 16 | Mood knob. The face binds to the slider. |
| 17 | Accent colour follows mood (not built yet). |
| 18 | ContextTurns knob. |
| 20 | RecallDepth knob, the recall animation, and the toolkit marketplace. |
| 22 | ReflectionEvery knob. |
| 24 | PerceptionChars knob. |
| 30+ | One premium toolkit per even level. |

**Still open:** odd levels from 19 on, and even levels 26 and 28.

**The recall animation** (level 20): thought bubbles come in, Morrow sorts
over them, the picked one is highlighted, and then the answer starts.

## Tools

**Toolbox agent.** A registry of callable capabilities, placed after
Governance, so a tool call is a second action on the same verdict and a
Red turn emits neither. A tool request produces two actions:

- Intent's own spoken acknowledgement.
- A `tool` dispatch.

The turn ends at that point. The result comes back later as an ordinary
perception.

**ToolManager.** Sits in front of the handlers, the way Governance sits in
front of the agents. It owns:

- **Timeouts.** A timeout is a perception, never silence, because the
  acknowledgement already promised an answer.
- **Per-device flood guard (`DeviceBlockCount`).** A trip is spoken once.
  Only the event count is suppressed, not the drive nudge. Recovery is
  still undecided.
- **Its own state.** It is reachable as a diagnostics tool, rather than
  living in Intent's prompt.

**How a result comes back.** The result decides, not the tool:

- Folded into the next reply as an advisory.
- As a `perception.self`, so Morrow speaks up.
- As a push notification, for long jobs. This also lands as a perception.

**Hazards:**

- **The loop.** Action → perception → action. A `device`-triggered turn may
  speak but not act.
- **Archiving.** Device and tool turns skip the archive by default, and the
  handler writes the rows that matter.

**Context.** Only a small `skills.txt` index lives in Intent's context. A
skill agent on perception publishes a hint (for example
`toolkit: metric-analysis`), and Intent decides whether to use it.

**Maintenance is consented, never silent.** "Shall I tidy up?" Tools may
adjust scores and pins. Deleting ground truth is its own action and must be
loudly confirmed (GDPR requires it regardless).

**Open:**

- One registry, or one agent per protocol.
- Which integration surface: Matter, Home Assistant, MQTT.
- Whether `tool` and `device` perceptions unify.

### First handler: `correction_tool`

"You misspelled X, can you fix it?"

- The utterance log is never edited; the correction is itself an utterance.
- The handler finds the fact rows lexically and writes corrected rows on
  the same threads, so supersession makes the correction current.
- It reports by folding into the next reply, or as a perception if it found
  nothing.

It's local and bounded, and exercises the whole path: index → dispatch →
acknowledge → write → report.

### Toolkit definitions

Each toolkit has two JSON files:

- **Definition.** What Morrow reads. It reads much like a skill.
- **Manifest.** What the definition is generated from. It is flexible, and
  it declares what the toolkit may touch.

The toolkits planned so far:

- **`toolkit_creator`.** Morrow interviews the person to build both files,
  and can reopen a draft to edit it.
- **Marketplace.** Upload and download toolkits. A downloaded toolkit is
  untrusted: Security checks it against its manifest.
- **Emergency.** Call an ambulance or reach next-of-kin. Impulse's reflex
  detects an emergency today, but cannot act on one. This is the one action
  where a Red verdict must be argued for rather than assumed.
  `Impulse:ReflexFloor`/`ReflexMargin` stay estimates until real traffic
  calibrates them.
- **Assistive:**
  - **Speech:** compose and speak, and hear atypical speech.
  - **Vision:** describe, read aloud, find an object.
  - **Cognition:** reminders, and step-by-step prompting.
- **Screen as perception.** Read the device's own screen, or another screen
  through the camera. On request only; continuous capture is surveillance.

### Deferred answers

"Let me ponder that", with the answer arriving minutes later. This covers
tool results, deep recall ("did you reflect on this in 2026?") and idle
Reflection, so it should be one mechanism. It needs:

- A `FallbackPosture` for Reflection that isn't Closed.
- A fresh `CorrelationId` thread back to the conversation.
- Rate limiting.

## Boot recovery (not started)

One bad file currently stops the host. On a phone there's no shell to fix
it with. Before the agents start, a recovery pass checks:

- Every parquet store: it opens, and one row parses.
- The vector sidecar dimensions.
- Instruction-file placeholders.
- Provider reachability. Failing this is not fatal.

A failing store is moved to `archive/quarantine/` and the host boots empty.
The pass produces a report the person can open, and the same checks become
toolbox handlers, so Morrow can self-diagnose mid-session.

---

# Backlog

## Open against the archive

- **Retired facts are unreachable.** Pass B in `FactConsult` is a top-up,
  not a lane, so "what did I used to drive" loses to the current car. A
  likely fix: take the top thread and return its members oldest to newest.
  Open whether that's a second verb, or a slot inside `Find`.
- **Characterise is unmeasured.** It needs a longitudinal corpus (v5):
  habits, decoys, dated changes, and a refusal question. First gate, which
  needs no corpus: check whether the keyword extractor keeps the tokens an
  answer needs.
- **Consolidator: may it say "neither"?** If so, does that unthread the
  facts, or only mark them?
- **Scale.** The approach held to 20k rows on a single synthetic corpus
  shape, so slow drift beyond that is not ruled out.
- **Thread naming.** Threads have no names. Does a person ever want to see
  or rename one?
- **Renaming Morrow doesn't stick.** `PersonaName` reads the old pair
  store, which conversation no longer writes to. It needs a deterministic
  write path.
- **Reflection is underfed.** It can't see "the third time this week". A
  digest pyramid would buy it reach.
- **Perception is embedded twice** (as `Query` and as `Passage`). On e5
  these are different encoders, so merging them changes quality. Measure
  the cost before touching it.

## Open on the surface

- **Attribution on shared devices.** The picker keeps the last person, so
  add a "not me" affordance.
- **No auth.** Profile ids are guessable and `/api/profiles` is open. Fine
  for a household, not beyond it.
- **Reflection's colouring isn't per profile.** One person's tone colours
  everyone's persona. Partition the batch by profile.

## Security rules — low priority

The 8 rules in `config/security-rules.json` are a backstop.

- **Every pattern is English.** A Norwegian reply passes all of them.
  Decide per rule what to do about that. Revisit when non-authors speak
  Norwegian to Morrow, or when Action reaches outside the process.

## Delivery — Android

- **The whole runtime runs on the phone** (`net10.0-android`). The Android
  host replaces `EciCas.Host`.
- **A metered relay** holds the API key, validates Play Billing, counts
  tokens and issues short-lived tier tokens. The spend ceiling is the risk
  control; the tail of heavy users is the threat, not the median.
- **Tiers become plans:** Free / Budget / Pro / Premium, verified on the
  server. Level-ups gate how a new user meets them.
- **Reflection as the paid hook.** Give free users a small permanent taste,
  not a trial.
- **Background thought is dropped.** Android kills background work, so
  notify on reflection instead.
- **Legacy.** The archive format is the product: Parquet/JSONL readable
  without our code. That means:
  - Re-embedding is a deliberate overnight job.
  - Encrypted backup with a passphrase key.
  - An export the person owns.
  - Inheritance: the archive *of* someone, versus a persona speaking *as*
    them, is undecided.
- **Personas are content** (instruction files), not engines.
- **On-device decode speed is unmeasured.** It gates the whole plan, and
  it's the cheapest open question to answer.
- **iOS later**, on the same shared logic.

## Companion extensions (not started)

- **Dictation.** Push-to-talk into the composer. No speaker ID.
- **Biometric unlock.** A new face creates a profile.
- **Diary category.** Entries that accumulate rather than supersede.
- **Profiles from conversation.** Create a profile when a new name comes
  up; also delete and merge profiles.

## Parked

- **Elapsed time on the envelope.** Waits until something downstream wants
  it.
- **Watchdog / recovery bootstrap / installer.** Waits on the platform
  decision; boot recovery above covers the urgent part.

## Out of scope

- The Python prototype's messaging plumbing. The port targets business
  logic.
- A Budget Mode spend auto-latch.
- `is_parroting()`: moot without the Librarian.

## Open design questions

- **Swappable personas.** Shared recall, per-persona Identity. This is the
  largest piece of unscoped work.
- **Inference, not just retrieval.** "What do I know that changes how I
  should read this?" The risk is inventing records.
