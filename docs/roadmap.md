# ECI-CAS — Roadmap

The C# backend and its Next.js companion (`morrow-eci/`) are built and wired
end to end — [`architecture.md`](architecture.md) says what exists. This
document is the **active backlog**: what's next, what's parked, what's out of
scope, and open design questions. Everything shipped, discarded or otherwise
settled has moved to [`roadmap-history.md`](roadmap-history.md).
[`product.md`](product.md) owns the other half — what is being sold, to
whom, and what is deliberately not promised.

**The archive is being inverted.** *The archive inverted* (see history) is
the current design direction; it supersedes the pair-addressed store that
older sections here were written against, and is shipped behind
`Utterances:Enabled` (default false).

**Next up.** Nothing is outstanding against the Python prototype's business
logic. The live work comes from the September 2026 external review, below.
Leading it: streaming Intent's tokens once the first sentence has cleared
Security.

---

# What's next

## From the external review

An outside model reviewed the codebase in September 2026. The findings are
fixed; three ideas were pulled forward and shipped with them (see history).
One was declined — a per-key in-flight `Task` map in
`CachingEmbeddingProvider`; releasing the lock across the call already gets
the parallelism, and the map would only dedup a batching caller that does
not exist.

### Latency — the three serial calls

The floor is **Librarian → Recall → Intent**, roughly 700 + 500 + 900 ms
before a word reaches the person. Impulse, Identity and Hindsight run beside
them and cost nothing. In order to attempt:

**Stream Intent's tokens.** The largest win, and it moves perceived rather
than actual latency. Red must never reach Action, so streaming provisionally
and retracting is out. Whether a `SecurityRuleSet` rule written against a
sentence stays sound on a prefix is open, so: **stream only after the first
sentence has cleared the rules.** Gives up the first ~200 ms, keeps the
invariant.

**Prefetch Recall's file reads from Hindsight's leads.** The passage sweep
is local and finishes in microseconds; its leads are usually a subset of the
final selection. Reading those pair files while selection is still in
flight spends idle disk and warms `ParquetArchiveStore`'s cache. No
substrate cost, no new message; a wrong prefetch is only a wasted read.

**Raise Recall's skip threshold.** With `MaxPickedPerWorker` at 6, an
archive of 40 rows still pays a full picking round to discard almost
nothing. A threshold at "as much as a prompt comfortably holds" removes a
serial call from most turns. Safe where `a0b43c9` was not (see history):
this skips picking *after* selection, so no judgment is bypassed — only a
filter with nothing to filter. Config knob, measured by `RetrievalProbe`.

### Interiority that is actually grounded

Constrained by the standing rule: **surface interiority only where something
actually happened to cause it.** Every item below is an event the system
already detects and throws away. Nothing here should make the persona talk
about itself more; that is the failure mode, not the goal.

- **Notice when a fact changes.** `ParquetArchiveStore.Merged` already
  detects the collision and silently replaces. Carrying the superseded
  value forward — as a prior, or a one-line note to Reflection — buys "you
  said Oslo before" with no new retrieval, no new call, no invention.
- **Let salience decay without deleting anything.** `Importance` is fixed
  at write time, so an old important fact permanently outranks a current
  one. Decaying it with age unless re-touched, as a *retrieval* weight
  only, gives forgetting-shaped behaviour with no data loss.
- **Let the corpus grow while nobody is talking.** Fire Reflection on
  silence so it **thinks** rather than speaks. Thinking unprompted doesn't
  need the parked platform decision; speaking unprompted does. The one item
  that acts with no person in the loop — wants the generation cap honoured
  and a hard ceiling on notes per idle period.
- **Make the echo depth do something.** Hindsight computes `EchoDepth` and
  nothing reads it — it detects the persona resonating with its own past
  thoughts rather than the person's present one. Last of the four: the
  damper can suppress genuine continuity as easily as an echo, so log what
  it actually does across real sessions before letting it change a reply.

## Companion & knowledge extensions (not started)

- **Speech-to-text input.** Dictation only, push-to-talk into the existing
  composer — sent text stays reviewable, `sendPerceive` is unchanged. No new
  topic, no agent contract change. Speaker identification stays cut (see
  *One instance per person*, history) — the mic answers *what was said*,
  never *who said it*.
- **Biometric + camera authentication.** Device biometrics at unlock; a
  different person picking up the device triggers camera-based profile
  creation. Backend is a user-context field on Perception's meta, which the
  profile field already is.
- **Diary knowledge category.** A category whose entries accumulate rather
  than overwrite — recurring appointments, dated milestones. Recall
  surfaces them in temporal order, not as overwriting facts.
- **Profiles, later increments.** A new name in conversation offering to
  create a profile; profile deletion and merge.

## Toolbox agent — IoT actions (not started)

Action today only produces speech. A companion that matters to someone with
a disability has to *do* things: lights, locks, thermostat, blinds. The
sketch is a **toolbox agent** owning a registry of callable device
capabilities, on the action side of Governance so every device call passes
the same verdict gate a reply does.

**A device response comes back in as perception**, not a return value: the
toolbox publishes what the device said onto `events.perception` and it runs
as an ordinary turn (tag `"device"`, same seam Reflection's ideas use). No
new topic, no new contract. Impulse colours on it for free (a lock that
refuses to close is something the persona should *feel*); unsolicited state
(a doorbell) is free too, as a perception with no preceding action.

Two hazards to settle in the design pass:

- **The loop.** Action → perception → action is a cycle; likely a
  Governance rule that a `triggered_by = "device"` turn may speak but may
  not act.
- **Archivist.** It hard-skips `"self"` today. Device turns need the same
  decision made deliberately — likeliest shape is skip by default, let the
  toolbox write the rows that matter.

**Flood guard — `DeviceBlockCount`.** Count events per device over a window
and stop admitting past a threshold, per device, in the toolbox (not
Governance, so filtering happens before four agents make substrate calls).
A trip is spoken once, on the transition, not silent (suppression a person
can't see reads as a broken device). The *count* is suppressed, not the
drive nudge — a flapping sensor must not colour Impulse per event. Recovery
is open: not automatic decay (hides a real fault), not manual-only (loses a
sensor for someone who can't reach it) — likely the persona raises it after
a quiet window and stays blocked until a person answers (the drive-gated
push Reflection already does).

Open beyond that: one agent with a tool registry or one per protocol; which
integration surface (Matter, Home Assistant, MQTT, vendor APIs); how a tool
call is represented on the bus without giving Intent a second output
vocabulary. Wants its own design pass before code.

**IoT is one instance of a general shape.** Any lookup Intent shouldn't
carry in context every turn (technical docs about the system, later
whatever else) fits the same seam: a dedicated agent answers async, off the
bus, tagged `self`/`tool` rather than `device`, and comes back in as an
ordinary `perception.text`. A minimal `skills.txt` index (not the rows
themselves) is the only thing that needs to live in context. Parked behind
the toolbox design pass — the hazards are the same: provenance on the
returned fact, and Archivist deciding whether a skill's answer is worth
writing down.

### One request, two actions

A tool request produces **two** actions: the ordinary spoken reply — "I've
started working on that and I'll get back to you" — and beside it an action
of type `tool`, dispatched to the toolbox. Dispatching is not waiting: the
turn ends, nothing on the bus holds a call open, and the *result* comes
back later as a fresh perception, running an ordinary turn. This is why the
toolbox sits on the action side of Governance: a tool call is a second
action on the same verdict, gated once, and a Red turn emits neither. The
acknowledgement is Intent's own sentence, not a canned string.

**The class is wider than IoT**, worth naming early because it decides
whether the registry is a device registry or a tool registry: the system
reading/writing itself (manual, debug settings — the last is a proposal
the person approves); IoT devices; another machine's or agent's state; the
open web (needs its own answer on provenance and Security); the passage
corpus (cheapest first tool, already local); a workshop registry that
changes under the running system (a different problem from calling a tool).

### ToolManager — Governance for handlers

One **ToolManager** in front of many **tool handlers**, each owning one
device or service — to handlers what Governance is to agents.

**It owns the timeout, and a timeout is a perception.** A dispatched call
the manager never hears back about must not be silent, since the
acknowledgement already promised an answer. The manager publishes the
timeout as a perception like any other result; a handler's failure reaches
the manager the same way and stops there — nothing propagates back to the
action that started it. `DeviceBlockCount` belongs here for the same reason
admission control does.

Open: whether the tool result arrives tagged `tool` and unified with
`device` at the perception seam or stays distinct.

**The manager's own state is reached by a tool like anything else.** "What
were you checking?" dispatches a diagnostics handler rather than being
plumbed into Intent's prompt as resident context — same trade the
`skills.txt` index makes.

### Three ways a result comes back

The acknowledgement ("I'll let you know when it's fixed") promises a
report. The handler declares, or the manager picks, how that report
arrives:

- **Folded into the next reply**, for results that aren't important. No new
  turn; the manager holds the result until the next `events.bundle` and hands
  it to Intent as an advisory ("fixed that spelling, by the way").
- **As a `perception.self`** when the handler reports back. It runs an
  ordinary turn, so the persona can speak up without being asked.
- **As a push notification**, for long jobs on mobile, where the person has
  left. It also lands as a perception, so the next conversation knows the
  report went out.

A timeout uses the same three channels. Which one is a property of the
result, not of the tool: a quick fix that failed may deserve a perception
even if its success would only have been folded in.

### First handler: `correction_tool`

"In your last reply you misspelled 'prototype emergent Cognitive
Identity'. Can you fix it?" Morrow finds the tool in the `skills.txt`
index, calls it like any other tool, and replies "I'll let you know when
it's fixed."

Seen on a live turn: Recall-2 returned "The prototype mergent Cognitive
Identity…" and Intent repeated the typo word for word. The mistake lives in
a *fact*, so the fix has to reach the store and not just the next reply:

- Utterances are append-only ground truth and are never edited. The
  person's correction is itself an utterance.
- The handler finds the fact rows that hold the mistake (lexically;
  "mergent" is a rare token), writes corrected rows on the same threads so
  that supersession makes them *now*, and reports which rows changed.
- If the mistake came from the extractor rather than from the person, the
  same handler repairs it. Either way the old rows remain superseded
  history.
- Small and bounded, so it reports by folding into the next reply unless
  it touched nothing ("couldn't find that spelling anywhere"), which comes
  back as a perception.

It is a good first handler because it exercises the whole path (index →
dispatch → acknowledgement → store write → report) on a tool that is
entirely local and cannot reach the outside world.

## Skill hints and deferred turns

**A skill agent on perception, where the Librarian sat.** Publishes a hint
(`toolkit: metric-analysis`) and nothing more; Intent decides whether the
tool runs, Governance executes it — the split the Librarian had. Killing
the Librarian vacates the slot rather than removing the pattern. A second
call on the critical path, so a clean paid-tier boundary.

**A turn may answer later.** "I have started the tool... and will come back
when I know" — a turn producing a deferred result, delivered as a
notification rather than blocking. Reflection wants the same
schedule-and-notify mechanism, so the two features cost one piece of
machinery.

**Maintenance is a consented tool, never a silent process.** "That is old
news" leads to "shall I tidy up?", and the person chooses what gets
demoted. These tools adjust scores and pins; they never delete ground
truth — deletion is its own action, loudly confirmed (and required for
GDPR regardless).

## Toolkit definitions (not started)

**Two JSON files per toolkit.**

1. **Definition.** What Morrow reads at runtime. It reads much like a skill:
   when the toolkit applies, what it does, and how to call it.
2. **Manifest.** What the toolkit is built from. It is complicated and very
   flexible on purpose, because the definition is generated from it and
   never written by hand.

**First toolkit: `toolkit_creator`.** Morrow interviews the person and
builds up both files as the conversation goes, which makes a toolkit a
conversation rather than a config file. It also needs an edit skill that
reopens an existing draft, so revising a toolkit is not starting over.

**Second toolkit: a marketplace** for uploading and downloading toolkit
definitions. A downloaded toolkit is untrusted input: it has to pass through
Security like any other proposal, and the manifest says what a toolkit may
touch, so Security has something to check it against.

## Level-ups — features unlocked by interaction (not started)

**Facts are the XP.** One XP per fact Morrow extracts, capped at 3 XP per
turn, so a fact-dense monologue can't farm levels. Leaving level L takes
`2·L` XP, so reaching level n takes `n(n-1)` facts in total: level 10 is
90 facts, level 14 is 182. The first ten levels go fast, and after that
each one takes 22+ new facts.

**The cog shows the level.** Each level adds one tooth to the cog around
Morrow, so level 1 is one tooth on an empty circle. Once the cog has its
full count (48, the telemetry ring's cells), further levels turn the teeth
gold one at a time, starting from the first. A fully gold cog is level 96.

**State lives on the backend, per profile:** level, XP and a global turn
counter. A cleared browser doesn't reset them.

**The schedule is data.** It lives in a JSON file on the server
(`levels.json`), not in code. Each level lists what it unlocks and what it
forces (tier, background, reflection on or off).

**What each level unlocks.** Even levels unlock features and odd levels
change the avatar:

| Lvl | Unlocks |
|---|---|
| 1 | Dialog only. The avatar is an empty circle with every other visual hidden. Tier is forced to Default (free for new users). Reflection is off. |
| 2 | Left side panel: what Morrow has learned. |
| 3 | Avatar: the pupil that follows the pointer. |
| 4 | Speech: the voice dropdown, with the default voice picked automatically. |
| 5 | Avatar: the aperture blades. |
| 6 | Reflection: the first reflection fires, and its highest note is pushed to `perception.self`. After that it runs every 5 turns. |
| 7 | Avatar: expressions (brows and lids follow Impulse). |
| 8 | Debug panel, without the knobs. |
| 9 | Avatar: speaking shockwaves. |
| 10 | Tier picker. The tier is swapped to Minimal, which is renamed **Free**. This is a deliberate nerf: Default up to here lets the person feel what Morrow can be. |
| 11 | Avatar: inner layers (second blade set, filaments, pulse). |
| 12 | Dark/light background. It swaps to dark automatically. |
| 13 | Avatar: hue phasing. |
| 14 | Knob: MaxSentences (reply length). |
| 15 | Avatar: wobble and idle shake. |
| 16 | Knob: Mood. It also binds the face to the slider. |
| 17 | Avatar: accent colour follows mood. |
| 18 | Knob: ContextTurns. |
| 20 | Knob: RecallDepth, along with the thought-bubble recall animation. The toolkit marketplace also unlocks here. |
| 22 | Knob: ReflectionEvery. |
| 24 | Knob: PerceptionChars. |
| 30+ | One premium toolkit per even level. |

Odd levels from 19 on are open, and so are even levels 26 and 28. New avatar effects get added to the file
as they're built.

**An unlock is an event.** The avatar animates it, so you can see the knob
arrive, with a one-line explanation of what Morrow gained.

**The machinery becomes visible as it unlocks.** Once recall is deeper:

1. Thought bubbles come in.
2. An animation shows Morrow sorting over them.
3. The one it picks is highlighted.
4. Then the answer starts.

In short, the bus traffic is shown as animation rather than hidden.

Open:

- Renaming Minimal to Free touches the tier convention everywhere:
  config files, docs, the bench.

## The archive inverted — open items

The inversion itself (utterance log, threading, consolidator, keywords,
two consult modes) is shipped behind `Utterances:Enabled` — see
[`roadmap-history.md`](roadmap-history.md) for the full design record. What
is not yet built:

**Characterise — pre-registered bench, not run.** *Characterise* (the
aggregate-over-a-filtered-subset consult mode: trait/habit/change queries)
is specified but unmeasured. v4 cannot be the instrument (no recurrence, no
time axis, one speaker) — needs **v5, a longitudinal corpus** built before
generation: timestamps and speakers over years, an authored distribution
(a true habit stated 12x, a decoy 3x, a former habit 8x before a dated
hinge and never after), near-miss terms, deliberately thin subsets, one
"what am I like" question scored on refusal. Key is terms that must/must-not
appear, gross correctness like `filing_key.py`.

Arms to run, over one frozen archive:

    find-only        top-k (the mode Characterise claims cannot answer these)
    count             aggregate, raw term frequency
    contrast          aggregate, log-odds against the whole archive
    contrast+thread   + recurrence and date ranges
    +exemplars        + bounded exemplar utterances

`find-only` is the baseline that matters: if top-30 flat cosine already
answers these adequately, most of the Characterise design is unnecessary.

**The first gate, needing no corpus and no server:** run the deterministic
keyword extractor (tokenise, drop stopwords, keep capitalised/numeric/rare
tokens) over v4's statements and nulls, and score whether the tokens an
answer needs survive it. If keywords are noise, the lexical half of *Find*,
all of *Characterise*, and the consolidator's gate are noise together, and
v5 isn't worth writing yet. Cheapest disconfirmation available; run first.

**Consolidator: open.** Whether the model should be allowed to say
*neither* — two rows that cosine threaded and a reader would keep apart —
and whether that unthreads them or only marks them. Splitting a thread is
a write no other path in the design performs.

## Memory architecture — remaining open pieces

Most of what this section originally proposed has shipped or been
superseded by the archive inversion — see history for the two-layer vector
retrieval, aliases, the assistant scope, and the retrieval-summary
findings. What's still open:

**Renaming Morrow — the write path that cannot be reached.** Telling the
persona a new name doesn't stick: `PersonaName` reads `persona/name`
correctly, but `persona` is not in the closed vocabulary, so `Cataloger`
can never route a conversational statement there — only a direct test
write proves the read path. Two ways out, second preferred:

- Add `persona` as a 33rd category — cheap, but exactly the ranking mistake
  `AssistantScope` exists to prevent (measured 1 of 16).
- Give the name a deterministic write path that bypasses vocabulary
  routing, the way the scope already bypasses ranking — recognised as an
  instruction to the persona, written straight to `PersonaName.Pair`.
  Cataloger never sees it, vocabulary stays closed.

Until one is done, the fallback is the name.

**The recency lane — a bundled cache beside the shelf.** Daniel's proposal:
every row written to `category/topic.parquet` also appends to one bundled
`recent.parquet` holding roughly the last year, trimmed at boot (unit is
time, not row count — "lately" shouldn't reach back equally far in a quiet
fortnight and a busy one). Not a performance cache — nothing here is slow
— it's a second lane into the archive that bypasses shelf routing
entirely, which is where every measured read-side loss lives, and it's
strongest exactly where the shelf is weakest on a young archive (see
history, batch 20). Cost is negligible (10k rows ≈ 15MB, ms to scan).

**The one real design question is deletion.** This would be the first
place the same fact lives at two addresses: a user asks to forget
something, the row is removed from the shelf file, and the lane serves it
back for months. A delete has to reach both, which means the lane needs a
real id to be addressed by (`ArchiveRecord` has none today). Read-side
dedupe against the shelf's own hits needs the same key. Config knob
(`Archive:CacheRows`, default off) rather than code either way.

**Reflection is already the cross-event agent, just underfed.** Archivist
runs at `BatchSize: 1`, structurally blind to "third time this week." A
digest pyramid (letting Reflection see a year in a prompt smaller than
today's batch) is what buys reach without the cost/accuracy hit of large
flat inputs. Reflection deliberately stays on a mid-strength model until
its prompts are proven — a weak model fails loudly on bad instructions, a
strong one quietly compensates and ships the flaw.

**Async deep recall (far future).** "Did you make any reflections on this
topic in 2026?" → immediate "let me ponder that" → dispatch through the
toolbox → answer minutes later, unprompted. Most of the machinery exists
(self-triggered loop-back, fire-and-forget bus, Impulse answering instantly
while slow work runs). Three things need designing: Reflection's
`FallbackPosture` (Closed today — wrong for an answer someone is waiting
on); a deferred-answer thread back (fresh `CorrelationId`); rate limiting
(same instinct as `DeviceBlockCount`, most expensive call in the system).

**Open: inheritance.** One instance per person is right for symbiosis, but
a legacy means a second person eventually opens the first's archive — a
child querying a parent's decades. Undecided whether that's a read-only
record they can search, or whether their own Morrow may Recall against it
— an archive *of* someone versus a persona speaking *as* them. Easier to
decide now than after twenty years of rows.

## Security rule coverage — low priority

The eight rules in `config/security-rules.json` are a backstop, not the
primary safety mechanism, and a backstop that grows without bound stops
being auditable.

- **Every pattern is English.** Matched against reply text, so the same
  reply in Norwegian passes all eight rules. Not a uniform translation
  pass — some rules are about phrasings that don't translate, others about
  nouns that mostly do. Worth a pass that decides per rule.
- **The irreversible rules are on the soft side of the split.**
  `irreversible-world-effect`, `spend-money`, `disclose-credentials` are
  Yellow (Intent revises once and proceeds), but
  `irreversible-world-effect`'s own description argues for Red. One word
  plus a test.
- Not a defect, left as-is: Security sees only the proposed reply text, so
  paraphrase walks past it — the cost of keeping the hard stop mechanical.
- Revisit when the persona is routinely spoken to in Norwegian by someone
  other than its author, or when Action gains a side effect reaching
  outside the process.

## Still open on the surface

- **`= ""` is doing the work of a null.** Every field on
  `ParquetArchiveStore.RecordRow` is non-nullable and defaults to `""`, so
  a missing subject and a legitimately-empty subject round-trip
  identically. `Subtopic`/`Domain` being genuinely optional is fine; the
  spread to fields where blank means "the system failed" is not. Any new
  column inherits the hole unless declared `required` with no initialiser
  and guarded on the way in — parquet hands back a blank regardless of what
  the constructor says.
- **The picker does not solve attribution.** `localStorage` keeps the last
  person's identity until someone switches, so on a shared device the
  persona can attribute one person's turn to another. With speaker ID cut,
  nothing closes this automatically — an explicit "not me" affordance is
  probably worth more than pretending the picker handles it.
- **No auth means the registry is open.** Profile ids are guessable and
  `GET /api/profiles` is unauthenticated. Fine for a household device, not
  beyond it.
- **Impulse's drive state is per profile; Reflection's slow colouring is
  not.** A batch spans whoever was talking, so one person's tone colours
  everyone's persona. Partitioning the batch by profile (pay per-profile
  calls) is probably right over scoping mood to whichever profile
  dominated, since the cheap option contradicts the stated intent that one
  child's tone must not pre-colour how the persona meets the parent an hour
  later.
- **Two mood vocabularies, unconnected.** The Debug slider sets `Mood`
  (Maleficent..Ecstatic); Reflection reports a label Impulse maps to drive
  vectors (`tense`, `curious`, ...). "Ecstatic" exists in both and is
  recognised by neither side of the other.
- Tier validation asymmetry: a tier is validated for shape at boot, never
  for whether its providers actually answer (see *Live tier switching*,
  history).

## Delivery — Morrow as an Android product

Supersedes the old one-line Android stretch goal. Destination is a Play
Store app.

**The client hosts everything.** `ChannelBus` is in-process
`System.Threading.Channels`; .NET 10 targets `net10.0-android`, so Core,
Bus, Agents and Substrates run on-device essentially unchanged.
`EciCas.Host`'s DI wiring is replaced by an Android host project, SSE
endpoints by a UI reading the bus directly. This is the whole runtime on
the phone, not a remote-client split — chosen for legal posture and cost
over latency.

**The relay is metered, not a router.** An API key can't ship in an APK.
Masking which providers are used isn't the point (arguably a trust asset to
name them); what can't be dropped is that entitlement and spend must be
checked somewhere the client doesn't control. A thin gateway holds the key,
validates the Play Billing purchase token, counts tokens, forwards, and
issues a short-lived token carrying tier and remaining budget. Its only
state is an account row (tier, running count) — bad to lose, not
catastrophic to leak. `MaxConcurrent`/circuit breaker move to the relay
(per-device once on-phone defends nothing against shared vendor quota); a
hard monthly ceiling is the whole risk control on free (the top 1% will do
50x the median).

**Tiers become plans**, verified server-side: Free (sponsored, cheapest
provider, hard ceiling, no toolkit), Standard (best value/token, daily
reflection), Pro (fastest/strongest models, toolkit, premium support).
Economics: roughly $0.0005/turn on a cheap model at Intent + reduced
Archivist, so free tier cost is not the threat — the tail is, and Pro needs
pricing that doesn't imply support a solo developer can't staff. Free is no
longer merely "worse" — a deterministic read path measured *better* than
the LLM one, so tiers aren't one axis worst-to-best (a `TierCatalog`
assumption to revisit).

**Reflection as the paid hook — open.** A small permanent taste (free gets
reflection rarely — once a day, on charge, on wifi) likely converts better
than a trial that teaches the feature then removes it, since reflection is
most of what separates a companion from a chatbot with a database.

**The continuous-thought promise is dropped.** Android kills background
work — "it thinks while away" becomes a foreground service with a
persistent notification, or WorkManager batches, or it doesn't happen.
Notification-on-reflection is the honest and better shape.

**What the legacy claim commits us to**, given an archive is meant to
outlive the app, the phone, and possibly the author:

- The format is the product — Parquet/JSONL readable in 30 years without
  our code; say so publicly.
- Re-embedding is a designed experience — on a phone, an overnight
  plugged-in job, not a silent one.
- Device loss must not be fatal — encrypted backup where we hold only
  ciphertext and the key derives from a user passphrase (not Play savegame
  sync — account-tied, size-capped, opaque). An export the person owns.
- Inheritance is a feature — someone must be able to open a dead relative's
  archive; an export format plus a passphrase-recovery story.

**Personas are content, not engines.** Student, coach, secretary, confidant
are one engine with different instruction files and seeded vocabularies —
a real authoring cost per persona. See [`product.md`](product.md) for which
launches.

## Boot recovery — a diagnostic that repairs before the host refuses (not started)

A single bad file on disk currently stops the whole host (2026-09-09: an
empty column in `archive/passages.parquet` threw a JSON parse error out of
`ParquetPassageStore.FromRow`, and the only fix was knowing which file to
delete — impossible on a phone with no shell). Per-call tolerance was tried
and reverted (`c8a736d`/`820dd42`) — the standing rule is a stale store gets
truncated, not read through a legacy-schema shim.

The fix is a **recovery agent that runs before the agents start**, checking
everything that's rotted before:

- Parquet stores (every archive pair, passage store, profile store): open,
  read one row, confirm columns parse. A failure moves the file to
  `archive/quarantine/<name>.<timestamp>` rather than deleting it.
- Schema drift — same treatment, continue with an empty store (cold archive
  is a working product; a refused boot is not).
- The vector sidecar — rows with missing/wrong-dimension embeddings
  (`ArchiveBackfill` fixes this but only for files it can open).
- Instruction files — each parses, every `{placeholder}` an agent fills is
  present (a renamed placeholder today surfaces as a model behaving oddly).
- The routing manifest — already enforced at boot; the model for the rest.
- Substrate reachability — one probe per provider, not fatal
  (`SubstrateWarmup` already makes the call).

Two things fall out: **a report, not just a repair** (what was checked,
fixed, quarantined — a person taps "something is wrong" to see it; also the
first piece of the diagnostic-agent-config direction), and **a toolkit the
persona can reach** (the checks become toolbox handlers, so Morrow can
self-diagnose mid-session on a device with no operator).

Sequenced after the toolbox agent for the handler half; the boot half is
independent and small.

## The emergency reflex can detect, but cannot act (not started)

Impulse recognises a life-threatening turn (heavy bleeding, not breathing,
fire, "call an ambulance") as an embedding-space distance and interrupts to
say the persona is paying attention — that's all it does. Calling an
ambulance, raising an alarm, reaching a next-of-kin belongs on the
**toolbox agent** as its most consequential handler: an emergency call is
an action gated the same way every device call is, and the one action
where a Red verdict must be argued for rather than assumed. Until it
exists, `Impulse:ReflexFloor`/`ReflexMargin` are estimates calibrated by
logging both scores on real traffic.

## Toolkit for assisting the disabled (not started)

The companion's reason to exist, as work rather than motive. Three
impairments, three toolkits, one persona:

- **Speech.** Compose/speak for someone who can't; hear someone a general
  recogniser fails on. The 512-char perception bracket and reply-length
  knob need their own profile here.
- **Vision.** Describe what's in front of the camera, read text aloud, find
  a named object — depends on screen/camera perception below.
- **Cognition.** Reminders that survive being forgotten, step-by-step task
  prompting, recognising a question's been asked four times without saying
  so (the last is a Reflection behaviour, not a tool).

Each is toolbox handlers plus, probably, a per-need instruction profile —
where "one instance per person" stops being a privacy argument and starts
being a functional one.

## Reading the screen as perception (not started)

Perception should also be **what's on a screen** — device's own screen, or
another screen through the camera (parking meter, a laptop the person can't
read, an error dialog). Direct capture is exact but platform-bound and
permission-heavy; camera works on any screen but needs vision + OCR. Both
arrive on `events.perception` as an ordinary turn with a different
`triggered_by` — no new downstream contract. Open question is *when* it
looks: continuous capture is surveillance, on-request is a tool. Start
on-request.

## Perception embeds once — needs evaluation

The same utterance is embedded more than once a turn: Librarian as
`EmbeddingKind.Query`, Hindsight as `Passage` (default,
`HindsightAgent.cs:176`), the reflex as `Query` again. The caching provider
keys on `(text, kind)`, so the two `Query` calls collapse and `Passage`
doesn't. Not accidental — on multilingual-e5, query and passage are two
different encoders, so forcing one kind to serve both is a
retrieval-quality change, not a caching change. Measure what a second pass
actually costs on the local model first; if noise, leave it.

## Long-term goals

**iOS**, via the same shared business logic the Android client runs on.

---

# Parked

Real gaps against the Python prototype's `current-spec.md`, deliberately
not being worked. Revisit when the named condition holds, not before.

**Elapsed time.** Stamping the gap since the last conclusion onto the
perception envelope, so the persona can say "it's been a while" about
something measured rather than guessed. Parked: the debug latency readout
covers the diagnostic half, and nothing downstream asks for the
interiority half. Revisit if a concrete behaviour wants the gap.

**§6.1 Watchdog.** No liveness ladder, no idle-musing timer. Parked until
the destination platform is known, or the running system proves flaky in
practice.

**§6.2 Recovery bootstrap.** No IaC-style sequencer.  `Program.cs` plus
manifest validators already cover config-drift detection, a partial analog.
When revived it should be scoped wider than the original — one sequencer
that doubles as an **installer**, provisioning a missing local LLM and
missing agents rather than only restarting dead ones. Waits on the same
platform decision.

# Out of scope

Not gaps. Listed so they don't get re-raised as oversights without a fresh
decision.

- **Messaging-plumbing differences** against the Python prototype
  (synchronous recursive `publish()` vs. per-agent queues,
  Governance-as-orchestrator vs. listener, Librarian calling Knowledge
  directly vs. selecting pairs for Recall). The port targets business
  logic, not architecture.
- **§7.2 Budget Mode auto-latch.** Only per-event cost logging exists, not
  a spend-cap auto-latch to deterministic fallbacks. Revisit if real spend
  becomes worth automating around.
- **§4.2 `is_parroting()`.** Structurally moot — `LibrarianAgent` is a pure
  selector emitting no advisory text. The refusal-lead-in constraint is
  moot the same way — Governance appends the Blocked text deterministically.
- **Two arrays into Intent.** The merged, importance-sorted result set
  replaces it on purpose (see knowledge-swarm record, history).

# Open design questions

**Swappable personas.** Switching which persona is active. Recall should
stay shared across personas ("what happened," not character); Identity
should not — each needs its own trait bank that only develops while
active. Open: does a swap create a new Intent instance or re-hydrate the
same one from a different store? Wants its own design doc — the largest
single piece of unscoped work in the project.

**Match input to output, not just retrieve.** Identity and Recall answer
"what does the archive say that's relevant" — retrieval. The sharper
version is "what do I already know that changes how I should read this" —
inference. Tension: archive-lookup's own principle is "report what the
records say, never invent one," and pushing toward inference risks turning
Recall into a second Librarian.

**Two extractors over one message.** Redundant archive columns cost
nothing at rest — a strict field extractor and a looser second pass could
run side by side and let the reader see both. Attractive because the
`writable` ceiling (29/35 on v3) is a write-side loss nothing downstream
recovers. Unmeasured; wants the v4 corpus.

## Open against the inverted archive

**Can a read reach a retired fact at all?** No, as built, and it's worse
than it looks. The two-read split's pass B is a *top-up*, not a lane — it
only runs when pass A fails to fill five slots, and skips any thread A
already used, so at real corpus scale it never fires, and in the one case
it would matter ("what car did I used to drive") the old-car thread is
exactly the one A already spent its slot on. The cause isn't ranking —
"what do I drive" and "what did I used to drive" produce near-identical
query vectors; the discriminator (*which member* of the matched thread) 
isn't in the text at all, so no score threshold separates them. The shape
that likely fits: score and collapse as `Find` does, then instead of each
thread's newest live member, take the top thread and return its members
oldest to newest — one thread's whole history, in order. Sweep and thread
ids already exist, so cost is a projection. Open: whether Intent should
call this as a second verb (a defensible reading — `Find` meaning strictly
*now* prevents the expensive failure, a retired fact competing with its
replacement, absolutely rather than on average), or whether `Find` should
spend one of its five slots on the superseded runner-up automatically. The
`UtteranceConsult` class comment still credits pass B with a job it doesn't
do at scale and needs correcting either way.

**Does flat retrieval hold at scale?** Partly answered — batches 24-25 ran
to 20,000 rows (an order of magnitude past the 1559 that raised the
question) and the read rule held. Still one synthetic corpus shape; rules
out a collapse between 1.5k and 20k, not a slow drift beyond it.

**Is the write side filterable without a model — the novelty half.** The
length half shipped (`UtteranceFilter`, `MinContentWords`, see history).
The novelty half (drop near-duplicate junk before it's even written) was
deliberately not built — threading already collapses restatements into one
thread read back as its newest phrasing, so dropping the row too would buy
nothing at read time and lose the fact that it was said again, and when.
Unmeasured: whether the length filter changes retrieval at all (it's one
integer; zero restores old behaviour).

**Thread naming.** The clustered-vs-authored shelf question is moot (the
inversion deleted the shelf rather than reauthoring it — see history), but
it leaves behind threads: discovered structure with no names at all.
Whether a person ever wants to see or rename one is the same
merge/rename/pin feature argument, moved onto a different object.

**On-device decode speed.** Every claim about a free tier that isn't
merely sponsored assumes a phone can run a small model at a tolerable
rate, and nobody has measured it. Gates the whole delivery plan; cheapest
thing on this list to answer.
