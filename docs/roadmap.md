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

**State.** Per profile, on the backend, next to a global turn counter. The
levels 1–10 taste of Pro is **once per account**, not per profile: deleting
a profile and starting over must not hand out a second free run, so the
account carries a "has spent the taste" flag the new profile inherits. The
account is the relay's, which is why the relay is required before this can
be enforced at all.

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
- **Screen reader.** Read the device's own screen, or another screen
  through the camera, and speak it. On request only; continuous capture is
  surveillance. The point is accessibility — text to voice — not memory,
  so nothing it reads becomes a fact about the person.

### Toolkit input log

Every toolkit writes its raw input to its own parquet, sharded by month:
`2026_09_screen_reader.parquet`. Separate files, not the utterance log,
because this is not the person speaking.

- **No fact split by default.** The extractor never runs over it, so a page
  of read-aloud email produces no facts. A toolkit may opt in.
- **Why keep it at all:** analytics later — what people actually point a
  toolkit at — and it is the raw record a handler can re-read without
  having had to guess in advance what mattered.
- Shares `ParquetUtteranceLog`'s shelf shape and its month shard, so
  retention, export and delete land on one mechanism.

**One row per sentence, keyed by turn.** A dump is split on sentence
boundaries at ingest and each sentence stored as its own row, all rows from
one invocation sharing the same `Turn`. The row shape already has
`long Turn` — no schema change.

Why sentences: it is what lets screening drop one sentence instead of
refusing a page, deterministically and without a model.

*Does splitting break fact extraction later?* No, as long as nothing reads
a single row and calls it the input. Rows are a storage unit; the **turn is
the unit of meaning**. Any later reader — the fact splitter above included
— selects by `Turn`, orders by row, and rejoins before it extracts, so a
fact that spans two sentences ("He left. She followed.") survives. The one
real loss is a sentence screening removed, which is the point. Splitting is
only safe under that rule, so the reader must never iterate rows.

### Reading a toolkit log back

"From the last screen dump, what was the plot that led to X, excluding Y?"
Morrow answers "let me get back to you" and hands a background job a
toolkit name plus the question. The job is lazy, and the LLM is the last
resort, not the default.

**Only on invocation.** The whole pipeline below runs when the parquet
reader toolkit is called and at no other time: nothing ambient, no
boot-time backfill over toolkit logs, no sweep on write. A person who never
asks never pays for an extraction.

1. Read `toolkit_facts.parquet` for that toolkit. A hit answers.
2. Miss: run the fact splitter over the current month's shard, write the
   facts, and try again.
3. Still nothing: walk backwards a shard at a time, skipping any shard
   already extracted. Facts are written once and reused after that.

**The ledger.** The job keeps its own table of shards it has extracted:
filename plus when. The open month is never in it — it is still growing.
A ledger rather than deriving it from `SourceId` coverage, because a shard
that honestly yields zero facts would otherwise be re-extracted forever.

**Reuse.** This is the existing pipeline pointed at a different directory:
`ParquetUtteranceLog` (the dump), `ParquetFactLog` (the toolkit facts) and
`FactBackfill` — which already takes `(IUtteranceLog, IFactLog,
IFactExtractor)` and fills gaps — with `FactConsult` as the reader. New
code is the ledger, the shard-at-a-time walk, and the dispatch.

### Screening a screen dump

Accessibility comes first: a dump is read aloud unless it is *obviously* a
secret. Over-blocking is the worse failure, so a story containing the word
"password" must pass.

- **Reuse `SecurityRuleSet`** — deterministic regex, no model, and
  `disclose-credentials` already matches what this needs: `sk-`, `ghp_`,
  `AKIA`, private-key headers, and `password: <6+ chars>`. The bare word
  matches nothing, which is the intended weakness.
- **Screen per sentence, not per dump.** Rules run over each sentence row.
  A Red sentence is dropped; its siblings on the same turn are spoken and
  stored. Blocking a page because one line looked like a key is the failure
  mode that makes a screen reader useless.
- So "this screenshot can't be read because it contains passwords" is never
  said. Morrow reads the page and says one line was held back — which is
  what resolves the conflict between blocking and not using a model: the
  split is a sentence boundary, not a judgement.
- Screening decides two things separately: what is spoken, and what is
  written to the toolkit parquet.
- Leaks will happen and are accepted. The goal is the obvious cases only.

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

## Delivery — common to every platform

The runtime, the archive and the relay are the same everywhere. What
changes per platform is delivery, billing and the input surface. Anything
in this section is shared; anything in a platform chapter is not.

### The relay

Every paid path goes through one server. It is not Android's — it was
written down there first, but Steam needs it first.

- **It proxies; it never dispenses provider credentials.** Provider keys
  cannot be scoped per user or per spend, so any key that reaches a client
  is a key someone drains. The client gets a token for *our* relay; the
  relay holds the provider keys and forwards.
- **It speaks the OpenAI wire format**, which means
  `OpenAiCompatibleSubstrateProvider` needs no change at all — a tier file
  points `BaseUrl` at the relay and carries a token instead of a key. The
  whole substrate layer is relay-ready already.
- **Model choice moves server-side.** Swapping a lab model, or failing over
  when one stops answering, ships without a client update. Reuse the shape
  already built for tiers: an ordered list per agent, health-driven
  failover, `SubstrateHealth.Classify`'s TimedOut / Refused / Unreachable
  as the signal.
  - Record which model served each turn. A weaker fallback feels like a
    quality drop with no explanation, and without this we are guessing.
- **Keep the hot path dumb.** Stream through, meter asynchronously; never
  block a token on a billing write.
- **Not hosted from home.** Residential ISP, no SLA, a dynamic IP, and an
  outage only fixable from the kitchen — against a proxy so I/O-bound that
  a small VPS absorbs it. Self-hosting saves nothing and buys downtime
  inside Steam's refund window. Home hardware is for the local-model box,
  analytics over the archives, and a warm failover origin.
- **Metering state lives in managed storage**, so the relay itself is
  disposable and rebuildable anywhere in minutes.
- One origin, an anycast edge in front to terminate TLS near the person.
  A second region when measurements demand one, not before.
- **Meter per owning account, not per playing account** — otherwise family
  sharing turns one purchase into several buckets.

### Energy and focus

The spend ceiling is Morrow's own energy, not a quota error. A token
bucket, which is both the correct rate limiter and the better fiction.

- **Energy regenerates at rate R up to a maximum M.** R alone sets the
  annual cost. M sets how good a heavy session feels, and must be worth
  more than one day's regen so a quiet week banks a reserve.
- Recovery is faster overnight. "Rested" is good fiction and it smooths
  provider load off-peak.
- A daily full reset was considered and rejected: 365 buckets a year
  instead of 52 means each day's ceiling shrinks to a few requests to hold
  the same exposure, and a legitimately heavy day feels stingy.
- Exact numbers stay available in settings. The meter is flavour, not
  concealment.
- **Empty is never a brick.** Out of energy offers the local model instead
  of a wall. See the offline pack below.

### The yearly pass

**Triple the energy cap, and energy refills faster.** That is the wording,
and the imprecision is deliberate: the cap is a promise, the regen rate
stays a tunable.

- **Multiply the two dials separately.** The cap is what a person feels —
  a big session works. The regen is what we pay. 3×M and roughly 1.5×R
  gives triple the felt benefit at half the cost of tripling both, and the
  store line is true either way.
- **The pass sets R, not the base price.** It self-selects heavy users, so
  the blended average that makes the base tier safe does not rescue this
  one. Choose R so that the pass's regen at ~70% utilisation still clears
  its net revenue with room; the base tier at R is then safe by a wide
  margin. Worked once: a $20 pass nets ~$14, leave ~60% for inference, and
  at 1.5×R that puts R near $0.15/week. At 3×R it would be half that —
  which is what separating the dials buys.
- **Never state the regen multiplier in store copy, and never hide it
  in-app.** Vague on the page, exact numbers in settings. The first keeps
  R tunable; the second keeps it honest.
- **Buying again extends the duration; the multiplier never compounds.**
- **Lapsing drains, it does not confiscate.** R and M return to base and
  banked energy above the new M is spent down, not deleted. The opposite
  writes its own reviews.
- **Lapsed is still a working product** — base energy plus the offline
  pack. The pass is more of Morrow, never permission to run her.
- **The entitlement is the relay's and is common. The purchase is not:**
  Steam needs a consumable microtransaction item, because a DLC is owned
  permanently and cannot expire; Play has real subscriptions natively.

### Tiers

Three, not five. `Premium` is dropped and `Mock` stays a dev tier nobody
sees. Config keys keep their names — they are spread across every tier
file — and only the display names change.

| Key | Shown as | Engines | Energy |
|---|---|---|---|
| `Free` | Local | all five agents local on the 2B | none |
| `Budget` | Balanced | Intent and Reflection remote, the rest local | some |
| `Pro` | Pro | all five remote | full |

- **Only five agents ever call a model:** `Intent` (the reply),
  `Reflection`, `extractor`, `picker`, `consolidator`. Everything else —
  Action, Governance, Identity, Perception, Security, TurnWindow, Recall,
  Scribe, Hindsight — is deterministic or embeddings-only, and embeddings
  are local ONNX already. So the tier ladder is those five entries and
  nothing more.
- **Which agents go remote is config, not code.** `CognitiveAgent`
  resolves per-agent from `Substrates:Agents`, so the balance point moves
  without a build. Budget already ships this shape.
- **Balanced is the interesting one:** the reply and reflection are where a
  remote model is felt, and the three fact-pipeline agents are where the
  volume is. Spend the energy where it shows.
- **The Qwen never serves Pro.** A paid tier that quietly downgrades a
  stage to a local model is the one thing a paid tier must not do.
- **Local and Balanced need the offline pack**, so they are unavailable
  until it is installed.
- **Local runs everywhere**, desktop and phone alike. The 2B is what makes
  that true; see the offline pack for the measurement.
- **Local is a quality step down, and is sold as one.** Not slower — the
  measurements say a weak machine keeps up — but dumber. It misreads
  recalled facts and sometimes loses track of who is speaking. That is the
  trade the person is choosing, and it should be stated in those words.
- **Defer background extraction to AC power and idle.** It is the most
  deferrable work in the system, and on an old or fanless laptop the cost
  is heat and battery rather than time.
- Base R still has to leave an empty Pro user slowed rather than stranded,
  but it is no longer the only thing standing between them and a wall.

### The offline pack

The local model is an opt-in download, not the default and not the cheap
version: *your hardware, your turn* — offline, private, unmetered.

- **~1.3GB of weights plus the inference runtime**, shipped together and
  never in the base install, so base minimum specs stay tiny and nobody
  who only ever uses the relay pays for a hardware requirement on the page.
- **Suggested at the moment it is useful** — when energy runs out — with
  the download size and the quality trade stated plainly.
- **Confirm the weights are redistributable** before this is committed to.
  Shipping them is redistribution; a research-only licence kills it.

**One model: Qwen3.5-2B.** The 4B is dropped. The size ladder here is
Qwen3.**5** — 0.8B / 2B / 4B / 9B / 27B and up — not Qwen3's
0.6B / 1.7B / 4B, which is what earlier drafts of this section assumed.

Measured on the twelve-case Intent suite, prompts rebuilt exactly as
`IntentAgent.BuildPrompt` composes them, same `UD-Q4_K_XL` quant for both,
`-t 4`:

| | wall | time-to-first | decode |
|---|---|---|---|
| 4B, GPU | 2.96s | 0.16s | 89 tok/s |
| 2B, GPU | 2.46s | 0.14s | 133 tok/s |
| 4B, CPU only | 7.70s | 0.59s | 13.4 tok/s |
| 2B, CPU only | **3.51s** | 0.27s | **32.1 tok/s** |

- **The 2B with no GPU lands where the 4B lands on a gaming GPU.** That is
  the whole case. One model covers the gaming rig, the old laptop and the
  phone, so Local stops being a desktop-only tier and the hardware fork
  disappears from the product.
- **Halve the download** — 1.25GB against 2.71GB — which matters most on
  the platform that was going to be excluded.
- **It leaves the GPU to the game.** 1.33GB resident instead of 2.9GB, and
  far less compute taken mid-frame. A gamer trades quality for framerate
  without being asked; the reverse is not a trade anyone accepts.
- The CPU numbers are four threads of a *fast desktop* CPU. An old DDR4
  laptop has roughly half the memory bandwidth, so read it as ~16 tok/s and
  ~6–7s a turn — usable. The same machine on the 4B would be ~7 tok/s,
  which is not.
- **Quantization is the remaining axis:** Q4 / Q5 / Q8 plus layer offload,
  chosen by what the machine has.
- Per-agent `Model` in `Substrates:Agents` already carries all of this. No
  new mechanism.

**What the 2B actually costs, measured on the same suite.** Format
compliance is not the casualty — that was the thing earlier drafts feared:

| n=12 | 4B | 2B |
|---|---|---|
| length in range | 12/12 | 12/12 |
| said its own name (never) | 1 | 0 |
| said "we" (never) | 1 | 1 |
| said "I" (always) | 9/12 | 11/12 |
| leaked a bracket tag | 0 | 0 |

The 2B is marginally *better* on the checkable rules. Two real regressions,
both about content rather than form:

- **It garbles recalled facts.** Given *caffeine after 4pm ruins his sleep*
  and *8am standup*, it produced "a shot of cold water and salt". Given two
  notes it fused them into a claim the person never made and quoted it back
  at him. **Acceptable:** the raw utterances are ground truth and the shelf
  is derived, so a bad read is a bad turn and a bad write is a re-derive —
  see the archive inversion. Neither is a loss.
- **It loses track of who is speaking**, answering as the person rather than
  to them, and reciting the Identity advisory as though it were a body it
  had. **This is the one to fix.** It is not recoverable from the archive,
  and the persona is the product. Prompt work, not model size — the 4B does
  the same thing when no Identity advisory is present and recovers the
  moment there is one, so the lever is the advisory's framing.
- Sample is twelve cases at one seed. Enough to choose a direction, not
  enough to tune against.

**The fallback string leaks into replies — on both models.** `intent.txt`
carries its own `## fallback` text inside the instruction blob, so the model
sees its error message as candidate output and sometimes emits it while the
substrate call *succeeded*. Measured at 1/32 on the 4B and 1/32 on the 2B —
identical, so this is a prompt bug and not a capability gap. ~3% of turns
are indistinguishable from an outage, and Governance cannot tell, because
the call returned 200. Keep the fallback out of the prompt.

**The model string is confirmed.** `models/local/Qwen3.5-4B-UD-Q4_K_XL.gguf`
is what is on disk, so `qwen3.5-4b` in the tier files was right; the new tag
is the 2B.

**Anchor the context window instead of sliding it.** The highest-leverage
change for local latency, and it helps the remote tiers too.

- `IntentAgent.BuildPrompt` already composes instructions, then the window,
  then this turn — stable to volatile, exactly the order KV prefix caching
  needs. That part is right already.
- But `TurnWindowAgent.Recent` is `TakeLast(turns)`. The oldest turn drops
  off the front every turn, so the prefix changes just past the
  instruction file and the cache dies there.
- Estimated, not measured: `intent.txt` is ~300 tokens of stable prefix
  against a full prompt nearer 1,000, so roughly 700 tokens are
  re-prefilled every single turn. On a GPU that is under a second; on CPU
  it is the reason Local looked GPU-only.
- Grow the window from a fixed anchor and reset it when it exceeds a cap.
  Each prompt is then the previous one plus new content, per-turn prefill
  falls to the newest turn alone, and the cost is one expensive turn when
  the anchor moves — so move it at a natural boundary, never mid-exchange.
- **It cuts Pro's bill as well.** Provider prompt caching wants the same
  byte-identical prefix and discounts cached input, which feeds straight
  back into R and therefore the pass economics.
- Do this before the probe below. It changes what the probe measures.

**Probe by measurement, not by spec sheet.** Run a fixed prompt once at
install and time prefill and generation separately.

- Reported specs hide what matters: shared versus dedicated VRAM, memory
  bandwidth, iGPU capability.
- The result picks the quantization and decides which tiers are offered,
  against the latency budget in the Steam chapter.
- Prefill is the number that disqualifies a machine, not generation. On the
  2B both are cheap enough that the probe's job shrinks to a floor check —
  enough RAM, and not so slow that a turn outlasts patience.
- It also makes an honest sentence possible — "your machine runs Local at
  about X" — instead of selling someone a disappointment.

**Two different warnings, and only one of them is about hardware.**

- **Speed is a hardware question, and the answer is "slower", not "worse".**
  Weak hardware does not change the weights or the sampling; the same reply
  arrives later. Prefer the measurement over a disclaimer: clear the bar and
  say nothing, marginal and say the number — "about 6 seconds per reply on
  this machine" — below the floor and do not offer it. Keep a disclaimer
  only as the net for machines that change under us: an external GPU
  unplugged, thermal throttling, a driver update.
- **Quality is a model question, and here the warning is earned.** Local is
  the 2B and it is measurably dumber than Balanced and Pro — it mishandles
  recalled facts and can slip on whose voice it is speaking in. Say so at
  the moment Local is selected, in plain words, because the person is
  choosing it and has a right to know what they are choosing. This is
  Morrow, just dumber.
- Do not merge the two. A hardware warning that says "quality" sends the
  person looking for a better GPU to fix something a better GPU cannot fix.

**Local is never a trap.** The person can switch back to Balanced or Pro at
any moment; it simply costs energy again. That is what makes the honest
version safe to say — a slow machine is a room they chose to walk into and
can walk out of, not a downgrade they are stuck with until the week turns
over. `TierCatalog.Switch` already does this live, so the setting is the
whole mechanism.

### Gaming contention

A loaded model holds VRAM resident and idle, which hurts a game before any
inference happens. Dropping to the 2B more than halves it — ~1.33GB against
~2.9GB — which is most of why the 2B wins, and it does not remove the need
to unload.

- **Unload on game detect**, don't merely stop inferring. Sustained GPU
  load plus a foreground fullscreen app, with a manual override — guessing
  wrong here is infuriating.
- **Or keep it on the CPU while a game is running.** The 2B holds 32 tok/s
  on four threads, so a gamer can talk to Morrow without the GPU being
  touched at all. That is a live tier switch like any other, and it is the
  version of "keep gaming" that costs no framerate.
- **This is a live tier switch**, and `TierCatalog.Switch` already performs
  those on a running session. "Out of energy" and "a game started" are the
  same mechanism with different triggers. Only the trigger is new.

### Legacy

The archive format is the product: Parquet/JSONL readable without our code.

- Re-embedding is a deliberate overnight job.
- Encrypted backup with a passphrase key.
- An export the person owns.
- Inheritance: the archive *of* someone, versus a persona speaking *as*
  them, is undecided.
- **Personas are content** (instruction files), not engines.

## Delivery — Steam (first)

The desktop is where Morrow can sit beside what the person is already
doing, which is the shape the toolkits were designed for and the one the
phone is worst at.

- **Assistant while doing other things.** Morrow is a companion overlay,
  not a window to switch to.
- **Screen reader toolkit.** The person triggers it; Morrow reads what is
  on screen and can be asked about it. On request only — continuous capture
  is surveillance, and that line is what makes the feature shippable.

### The shell

- A thin WPF/WinForms window hosting **WebView2**, running the existing
  ASP.NET Core host **in the same process**: one exe, one language, no
  sidecar to supervise and no orphaned processes.
- The client is already a single-page app — `app/` has one `page.tsx` and
  no route handlers — so `output: 'export'` is the whole port.
- Corner-sitting is the work: transparent always-on-top window,
  click-through outside the character, per-monitor DPI, remembered corner,
  tray icon, summon hotkey. Finite, fiddly, not architectural.

### Voice while gaming

The feature that sells it, and the one with a real hazard.

- **`RegisterHotKey`, never a `WH_KEYBOARD_LL` hook.** Low-level hooks are
  what kernel anti-cheat dislikes.
- **Never inject into the game process.** Audio in, audio out; no
  injection is needed and it is the whole anti-cheat surface.
- **Exclusive fullscreen cannot be drawn over.** So gaming is voice-only
  and the character is the desktop presentation. That is also the same
  audio path as the accessibility story.
- **Snappy is a latency budget**: under a second from releasing the button
  to first audio. That forces local STT streaming while the person still
  talks, first LLM token forwarded not buffered, and TTS streamed a
  sentence at a time — which `MaxSentences` already suits.

### Money

- **$10 base, including a permanent energy allowance that never expires.**
  What they bought keeps working forever; that is what stops a one-time
  purchase from feeling volatile.
- **The yearly pass is common; only its purchase is Steam's.** A DLC is
  owned permanently and cannot expire, so the pass is a consumable item
  through the Steam Microtransaction API, consumed into a dated entitlement
  on the relay. See the pass above for what it grants.
- **The offline pack is free DLC, auto-granted** to owners of the base app,
  so the floor is universal and only the download is a choice.
- **Never sell more quota for a one-time fee.** A recurring cost sold once
  is the trap the $10 price exists to avoid.
- Open: bring-your-own-key as a cheap permanent unlock — low-priced rather
  than free, because it costs us nothing per use and converts the heaviest
  users, who are otherwise the worst margin. Not settled.
- Valve takes 30% and tax comes off the top: $10 nets roughly $6–7. Model
  the blended average, not the ceiling — the ceiling is insurance.

### Store

- **Steam Direct is $100 per app, recoupable**, with a 30-day wait between
  paying and being allowed to release. Start that clock early.
- **AI disclosure is mandatory** and asks what the guardrails are.
  `SecurityRuleSet` is the answer, which is a better position than most
  submissions manage.
- **Screen reading must be disclosed plainly**, to Valve and to the person.
  "On request only, never continuous capture" is the sentence.
- The risk is category, not quality: a companion is Steam-shaped, a
  productivity utility is on the line. Write the page accordingly.
- Discovery, not approval, is the hard part. Software gets little
  algorithmic traffic.
- Streamers are the cheapest marketing available — a reactive character on
  screen is inherently streamable — and also the heaviest users, which is
  what the BYO-key question above is really about.

### Workshop toolkits — Steam only

Steam Workshop is hosting, moderation and discovery we would otherwise
build, so community toolkits are a desktop feature and stay one.

- **Gated on a capability model.** `SecurityRuleSet` screens *output*; it
  says nothing about what a toolkit is *allowed to do*. Strangers' toolkits
  cannot reach IoT or the filesystem until capabilities are declared and
  sandboxed. This gate ships before Workshop does.

## Delivery — Android (after Steam)

- **The whole runtime runs on the phone** (`net10.0-android`). The Android
  host replaces `EciCas.Host`.
- **Play Billing replaces Steam's**, against the same relay and the same
  energy bucket.
- **Tiers become plans**, verified on the server. Level-ups gate how a new
  user meets them.
- **Reflection as the paid hook.** Give free users a small permanent taste,
  not a trial.
- **Background thought is dropped.** Android kills background work, so
  notify on reflection instead.
- **Local ships on Android too.** Dropping to a single 2B is what makes
  this true: the tier that was going to be cut from the phone is now the
  same tier the desktop runs, with the same weights and the same 1.25GB
  download. Android is no longer a reduced product, and there is no
  per-platform model matrix to maintain.
- Deferring background extraction to charging and idle is the common rule,
  not an Android one — see the tiers section.
- **On-device decode is still unmeasured**, and it is the one number that
  could still take Local off the phone. Sustained generation throttles and
  drains regardless of size; the 2B moves the ceiling, it does not remove
  it. Measure before promising Local on the store page.
- **No Workshop.** Community toolkits stay a desktop feature.
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
