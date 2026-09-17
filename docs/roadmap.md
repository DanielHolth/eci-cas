# ECI-CAS — Roadmap

This is the active backlog only. The product direction and split-layer contract
live in [`architecture.md`](architecture.md); shipped or settled design lives in
[`roadmap-history.md`](roadmap-history.md); product framing lives in
[`product.md`](product.md).

Current architecture rules:

- Thick client on the user's machine.
- Cross-platform host runtime across Windows, macOS and Linux.
- Remote relay owns secrets and provider auth.
- Sync layer owns durable user state and archive storage.
- Local state remains transient and device-scoped.

## Near-term

- Stream Intent tokens after the first sentence clears Security.
- Carry fact-change context into reflection and salience decay.
- Score fact reliability against real extraction-quality and contradiction
  signal instead of the current mint-time proxy (see `architecture.md`).
- Keep tools explicit, provenance-aware, and auditable.

## Later

- Expand the toolkit marketplace and authored tool definitions.
- Tighten the sync and archive boundary around durable memory.
- Rebuild user-facing features on the split-layer model instead of legacy assumptions.
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
- **Both dials are now numbers. R = $5 of inference per person per year;
  M = 48 hours of regen.** Built, in `Energy/EnergyMeter.cs`.
  - **The bucket is denominated in dollars, not turns.** A turn on a full
    six-turn window costs several times a turn on an empty one, and a
    bucket counting turns would be most generous to exactly the sessions
    that cost the most. It debits `SubstrateTrace`'s cost — the same signal
    `CostLedger` records — so the history and the remaining budget cannot
    disagree about what a call cost.
  - **M is stated as a duration, not a sum.** That is what keeps the
    invariant above visible: at 48 hours it is self-evidently worth more
    than a day, and R can be retuned without silently changing what a full
    meter is worth relative to a day. Two days is also the smallest ceiling
    that survives a weekend.
  - **What that buys, on Pro's prices** (`gpt-5.6-luna`, $0.20/$1.20 per
    Mtok; Reflection is Mistral at zero): a turn is roughly 3,200 input and
    340 output tokens across the four paid agents, so ~$0.001. R is then
    **~14 turns a day** and M is **~27 turns** banked. The yearly pass at
    3×M / 1.5×R gives ~82 banked and ~21 a day, costing ~$7.50 against
    ~$14 net — which is the worked example above, reached independently.
  - **Regeneration is computed from elapsed wall-clock time, not ticked by
    a timer.** A timer has to be running to be correct, which makes every
    restart and every suspended laptop a small theft.
  - **The balance floors at zero; it never goes negative.** One expensive
    turn must not leave a person owing time before anything works again.
  - **Not authoritative, and not trying to be.** The file is editable. The
    relay meters what actually costs money, server-side, per owning
    account; this is the client's honest copy so the bar can move without a
    round trip.
  - **Still to do:** overnight weighting (it redistributes R rather than
    changing it, so no number here moves), the pass's multipliers, and
    surfacing the meter to the client.
- Recovery is faster overnight. "Rested" is good fiction and it smooths
  provider load off-peak.
- A daily full reset was considered and rejected: 365 buckets a year
  instead of 52 means each day's ceiling shrinks to a few requests to hold
  the same exposure, and a legitimately heavy day feels stingy.
- Exact numbers stay available in settings. The meter is flavour, not
  concealment.
- **Empty is never a brick.** Out of energy drops to Local rather than
  stopping. See *Empty falls back to Local* under Tiers.

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

**Empty falls back to Local.** Running out of energy switches the tier to
`Free` and keeps answering, rather than presenting a wall and a store page.

- **The mechanism already exists.** `TierCatalog.Switch` swaps tiers live,
  which is the same lever the settings toggle pulls. Falling back is a
  `Switch` on an energy-exhausted event and a `Switch` back when regen
  crosses the threshold. No new machinery.
- **Morrow says it herself, in character, once.** *"I'm tired and dumber
  now — I'm using your hardware to answer."* Not a modal, not a banner that
  stays up nagging. She is allowed to be diminished; she is not allowed to
  become an upsell.
- **Coming back is silent.** Announce the drop, not the recovery. A person
  who has been told she is tired will notice when she is not.
- **This makes regen a patchable cost lever.** Today R is bounded by how
  bad it feels to hit zero, because hitting zero is a wall. Once zero means
  *degraded but working*, R can be tuned down in a patch to chase inference
  costs without shipping a worse product — the floor stops being nothing
  and starts being Local. That is the real reason to build this.
- **The server has to already be up, and the weights already warm.** Both
  are now boot's job: `scripts/start.ps1` starts llama-server on every tier,
  not only the local ones, and `EnergyFallback` runs the same warm-up the
  tier dropdown runs. Before that, falling back landed on a dead `:8080` on
  any paid boot — the promise inverted into exactly the brick it denies.
- **It does not rescue a person who has no pack.** Fallback needs the
  weights on disk, so this argues for shipping the pack by default rather
  than on demand — 1.25GB against never being stranded. **Open:** default
  install versus opt-in download is not yet decided, and it trades install
  size against the fallback ever being available when it is needed.
- **The fallback must never touch Pro's paid stages silently.** Dropping to
  Local when energy is gone is a tier switch the person is told about;
  quietly downgrading one agent inside Pro is the thing that is forbidden
  above. Same rule, and the announcement is what separates them.

### The offline pack

The local model is not the cheap version: *your hardware, your turn* —
offline, private, unmetered. Whether it is an opt-in download or part of the
base install is open, and the energy fallback above is the argument for the
latter.

- **~1.3GB of weights plus the inference runtime**, shipped together. If
  they stay out of the base install, minimum specs stay tiny and nobody who
  only ever uses the relay pays for a hardware requirement on the page — but
  then the energy fallback has nothing to fall back to on first use.
- **If it stays opt-in, suggest it at the moment it is useful** — when
  energy runs out — with the download size and the quality trade stated
  plainly.
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
| 4B, GPU | 2.81s | 0.11s | 86.8 tok/s |
| 2B, GPU | 2.35s | 0.06s | 138.1 tok/s |
| 4B, CPU only | 7.20s | 0.55s | 12.6 tok/s |
| 2B, CPU only | **3.21s** | 0.24s | **31.3 tok/s** |

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
| length in range | 12/12 | **10/12** |
| said its own name (never) | 2 | 1 |
| said "we" (never) | 0 | 0 |
| said "I" (always) | 9/12 | 11/12 |
| leaked a bracket tag | 0 | 0 |
| mean reply length | 253ch | 140ch |

Format compliance splits: the 2B is better on pronouns and no worse on the
never-rules, but it undershoots the sentence cap and writes replies a little
over half the length.

**The terseness is in character, so it is not a defect.** A tired Morrow
running on the person's own hardware *should* be curt — short answers are
what being low on energy sounds like, and the fallback announces exactly
that. So undershooting the cap is accepted rather than tuned out, and the
2B's 140 characters against the 4B's 253 is the voice working, not the
model failing. This is the one place where the cheap model and the fiction
point the same way; take it.

Two real regressions remain, both about content rather than form:

- **It garbles recalled facts.** Given *caffeine after 4pm ruins his sleep*
  and *8am standup*, it produced "a shot of cold water and salt". Given two
  notes it fused them into a claim the person never made and quoted it back
  at him. **Acceptable:** the raw utterances are ground truth and the shelf
  is derived, so a bad read is a bad turn and a bad write is a re-derive —
  see the archive inversion. Neither is a loss.
- **It loses track of who is speaking**, answering as the person rather than
  to them, and reciting the Identity advisory as though it were a body it
  had. **This is the one to fix.** It is not recoverable from the archive,
  and the persona is the product. The 4B does the same thing when no
  Identity advisory is present and recovers the moment there is one, so the
  lever is the advisory's framing rather than model size.
- Sample is twelve cases at one seed. Enough to choose a direction, not
  enough to tune against.

**Rewording `intent.txt` is a partial fix, not a fix.** Tested directly:
twelve cases × three seeds, the current main section against a revision
that opens *"You are replying to another person"* instead of *"You are
speaking as yourself"* and forbids quote-wrapping.

| n=36, 2B | current | revised |
|---|---|---|
| addressed the person as "you" | 14 | **19** |
| wrapped the reply in quotes | 5 | **2** |
| said its own name (never) | 2 | **5** |

It buys real ground on addressing and on quote-wrapping, and loses ground on
the name rule. And the collapse survives in a shorter shape — the 2B holds
the speaker frame for about one sentence, then slips: *"You're finally done
with that report. I hate the way I had to stare at it all week."* So take
the wording change for what it is worth and treat the Identity advisory as
the actual fix; do not record this as closed.

**There is no fallback leak.** An earlier draft of this section claimed
`intent.txt` shipped its `## fallback` text to the model at ~1/32 on both
sizes. That was a harness bug, not a product bug, and the claim is
withdrawn. `InstructionFile.Parse` splits on `## ` and strips `#`
commentary; `BuildPrompt` calls `_instructions.For(Name)`, which is the
**main** section only — 378 characters of the file's 1,184. The fallback
string reaches the model never, and reaches replies only through
`FallbackResult`, which is what it is for.

**What is on disk.** `models/local/Qwen3.5-2B-UD-Q4_K_XL.gguf`, 1.25GB, is
the shipping weight and what `qwen3.5-2b` in the tier files resolves to. The
4B GGUF is still in `models/local/` and is now referenced by nothing.

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

Built: `src/EciCas.Shell`, shipped as `Morrow.exe`. `./start.cmd` launches it;
`-Dev` gets the old console-host-plus-browser loop back.

- A WPF window hosting **WebView2**, running the existing ASP.NET Core host
  **in the same process** via `HostBoot.StartAsync` — one exe, one language,
  no sidecar to supervise and no orphaned processes. `HostBoot` is the seam
  the console REPL and the shell share; neither is the real front end.
- The client is a single-page app, so `output: 'export'` was the whole port.
  The shell passes `--Surface:ClientPath=client` and the host serves `out/`
  at its own root — static files mapped before endpoints, so a file cannot
  shadow a route, and the API is same-origin with no CORS in the shipped
  path.
- Corner-sitting was the work and is done: transparent always-on-top window,
  click-through, remembered corner (`overlay.json` beside the exe, clamped
  against the virtual screen), tray icon, hotkeys.
- **Click-through needed every child HWND.** `WS_EX_TRANSPARENT` on the
  top-level alone does nothing, because hit testing finds the deepest window
  first and WebView2 nests two or three of its own. `ClickThrough` walks
  `EnumChildWindows` and the overlay re-applies on `NavigationCompleted`,
  once those children exist. Colour-keying was rejected: an anti-aliased
  face would fringe.
- **Drag and click are told apart by distance, not by button.** The page
  posts `{type: "drag"}` once the pointer travels 4px with the button down
  and `DragMove()` hands the rest of the gesture to the OS move loop, so no
  click follows. Under 4px it is a click and opens the session.
- **Open:** per-monitor DPI is untested on a mixed-DPI desktop.

### Two surfaces, one session

The desktop app is not a port of the client. Morrow is a **watermark** —
semi-transparent, animated, sitting wherever the person put her — and the
existing browser interface opens beside her on demand, unchanged. Nothing is
reimplemented for the desktop, which is the point: one client, shown two
ways. Built as `/overlay/` plus `lib/shell.ts`.

- **Most of the time she is only the watermark.** No chrome, no transcript,
  click-through everywhere except the character itself.
- **`-` is push-to-talk. `|` toggles interactable.** Polled with
  `GetAsyncKeyState`, per *Voice while gaming* below, so neither key is taken
  from anything else -- the hyphen still reaches the game. Config, in the
  `Shell` section, names the character rather than the virtual key and
  resolves it through the foreground layout, because `|` is a different
  physical key on a Norwegian keyboard than on a US one.
- **Interactable is what makes her clickable.** Clicking her opens the
  browser interface in a new window: the same `page.tsx`, the same
  collapsible debug drawer and thoughts panel. While she is a watermark the
  clicks belong to whatever is underneath her.
- **The window is a view, not a second client.** Closing it hides it; the
  session belongs to the host, and the host is the same process either way.
- **Two subscribers means two voices.** Settled by query string: the shell
  opens the session window as `?mute=1`, the overlay owns the mouth. A plain
  browser tab has no query string and speaks, as before. The overlay also
  needs `--autoplay-policy=no-user-gesture-required`, because a
  click-through window can never receive the gesture the policy wants.
- **Dictation is local, and only local.** whisper.cpp in the shell's own
  process, on the CPU, roughly 140MB of weights and well under a second for a
  spoken sentence. Every other model here may be a vendor's if the tier says
  so; this one may not, because it is the only input that carries the room.
  Held is the whole gesture: down opens the microphone, up closes it and
  transcribes the take in one pass. The transcript goes to
  `PerceptionAgent.Perceive` directly rather than over loopback HTTP —
  `/api/perceive` is for surfaces outside the process, and this one is a field
  away.
- **Three guards, because a held key is not a button.** The voice key is a
  hyphen that still types, so it must be held past `HoldMs` before anything
  records; a key stuck down by a game must not record the afternoon, hence
  `MaxSeconds`; and whisper asked to transcribe a quiet room answers with
  words nobody said, hence the peak gate and the dropped `[BLANK_AUDIO]`
  annotations. Each failure is a sentence under the face, never a silence.
- **What she heard is shown before it is answered.** Dictation is the one
  input the person cannot check for themselves — they know what they typed,
  they do not know what a model made of what they said — and a transcript that
  arrived wrong is indistinguishable from a persona that answered badly.

### What a window opened mid-session knows

The worry was the debug log. That was the half that already worked; the
conversation was the half that did not. Fixed by subtraction.

- **The turn log replays, then follows.** `useTurnLog` calls `GET /api/log`
  for what happened before the window opened and then `/api/log/stream` for
  what comes next. The debug drawer and the thoughts panel both derive from
  those records, so both arrive populated. `TurnLogSubscriber` does the
  reduction once, server-side — which is exactly why a second surface needs
  no reduction logic of its own.
- **The raw envelope feed is gone.** `/api/stream`, `SseBroadcaster` and
  `useEciStream` fanned out live envelopes and kept no backlog, so a window
  opened after five turns showed five turns of *debug* and an *empty*
  conversation. Deleting the feed fixed that: everything a surface draws now
  comes from a projection that replays, and no envelope meta leaves the
  process at all. The transcript is built from `TurnRecord`s — `Perception`
  the utterance, `Intent` the reply, the rest the bundle the bubbles draw.
  - **What that lost is the per-turn face.** `TurnRecord` carries `Impulse`
    but no expression word, so a replayed turn has no expression of its own.
    Accepted: the face shows how she is now, not how she was on turn three.
  - **`MAX_TURNS` went with it.** `TurnLog:Retain` (100 events) is now the
    only cap, server-side, where the retention dial already was. A long
    session replays its last hundred; anything older is on disk in the JSONL
    sink.

### Keys on someone else's machine

A desktop app ships its configuration to the person's disk, so "never
visible to the end user" is a delivery constraint rather than a UI one.

- **The shape is already right.** `SubstrateRegistration` reads
  `ApiKeyEnvironmentVariable` — the *name* of a variable, resolved at
  runtime — so no tier file has ever carried a secret, and shipping the
  config ships nothing.
- **What must not ship is a provider `BaseUrl` with any key path at all.**
  On Steam every paid stage points at the relay and carries a per-account
  token: revocable, metered, and worth nothing to anyone else. See *The
  relay* — it proxies and never dispenses provider credentials, and this is
  the platform that makes that non-negotiable.
- **`Sse:ExcludedMetaKeys` is the scrubber and it is config.** Envelope meta
  a stream must not carry outward is already a list rather than a code path,
  so keeping a secret out of the debug drawer is an entry, not a patch.
- **DevTools off in Release.** The window is WebView2 and the drawer is
  built to be read; the console next to it does not need to be.

### Steam Cloud

- **Parquet and JSONL under `archive/` are the save.** The format is the
  product — readable without our code — so the cloud story is a path
  configuration and an upload, not a serializer.
- **Settle before uploading.** `TurnLogSubscriber` holds an event for
  `SettleMs` (3s) because Archivist's write and Reflection's batch land
  behind the reply. Uploading mid-turn captures a shelf that disagrees with
  its own ground truth.
- **Boot recovery is the other half.** A cloud conflict or a half-synced
  shard is the same failure the recovery pass above already checks for, and
  quarantine plus an empty boot is the same answer.

### Voice while gaming

The feature that sells it, and the one with a real hazard.

- **`GetAsyncKeyState` polling, never a `WH_KEYBOARD_LL` hook.** Low-level
  hooks are what kernel anti-cheat dislikes, and `RegisterHotKey`, which this
  used first, swallows the key desktop-wide -- unacceptable for a key that is
  also a plausible game binding. Polling two keys at 40ms takes nothing from
  anyone and learns nothing about any other key. It cannot suppress the
  keystroke either, which is the point and also the trade: typing `-` opens
  the microphone, so both keys stay rebindable.
- **Never inject into the game process.** Audio in, audio out; no
  injection is needed and it is the whole anti-cheat surface.
- **Exclusive fullscreen cannot be drawn over.** So gaming is voice-only
  and the character is the desktop presentation. That is also the same
  audio path as the accessibility story.
- **Snappy is a latency budget**: under a second from releasing the button
  to first audio. STT is local and in-process now, but it runs on the whole
  take after release rather than streaming while the person still talks —
  ~1.5s for a five-second sentence on `base`, which spends most of the budget
  before the LLM has seen a word. Streaming the transcription, forwarding the
  first LLM token rather than buffering, and TTS a sentence at a time — which
  `MaxSentences` already suits — are what is left.

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

- A Budget Mode spend auto-latch.

## Open design questions

- **Swappable personas.** Shared recall, per-persona Identity. This is the
  largest piece of unscoped work.
- **Inference, not just retrieval.** "What do I know that changes how I
  should read this?" The risk is inventing records.
