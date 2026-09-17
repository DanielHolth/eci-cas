# Architecture

Narrow agents on a message bus. Each owns one queue; publish is
fire-and-forget; no agent names another. Tests assert outcomes, never
cross-agent ordering.

## A turn

1. **Perception** publishes `events.perception` with a fresh `CorrelationId`.
2. On that, in parallel: **Impulse** appraises, **Identity** reads the
   persona, **Hindsight** sweeps passages, **Recall** sweeps facts, **Sight**
   reports the screen, and **Scribe** logs the utterance and extracts facts.
3. **Governance** bundles advisories by `CorrelationId` (or times out) and
   publishes `events.bundle`.
4. **Intent** writes the reply on `events.proposal`.
5. **Security** judges each proposal. Yellow sends it back to Intent for
   another pass. Red replaces the reply.
6. **Governance** gates on the verdict and hands off to **Action**, which
   delivers the reply.
7. **Reflection** fires every `BatchSize` conclusions. It writes one passage
   and may publish an idea back as `triggered_by: self`. `Generation` caps
   how deep those ideas can chain.

Impulse's Critical reflex can publish its own proposal, and so does a
reading of the screen; Security gates both like any other. Wildcard
subscribers (logger, console, SSE, turn log, telemetry) watch everything and are invisible to agents.

## Sight

The screen, as an advisory beside the rest. `ScreenShotOptions.Enabled`
(`Shell:Screen:Enabled`) gates whether a screenshot is taken at all —
off by default on every tier, no exceptions, since capturing the screen
without being asked first is the disclaimer moment, not a knob a tier file
should ever flip. When it is on, the shell captures a screenshot and runs
local OCR the moment the voice key arms, and `Glimpse` starts a
low-detail look *before the turn exists* — the person spends a second or two
speaking and the call runs through it, so the turn usually collects a
finished answer.

- **glance** — every take, low detail, no question. Pays 173 tokens to say
  what kind of thing is on screen.
- **look** — only when the turn needs it, high detail, sixteen times the
  price. Asked for by the person (`CloserPhrases`) or by the glance itself
  (`NEED-A-CLOSER-LOOK`).
- **read** — "read my screen to me" (`ReadPhrases`). Goes straight to
  Security past Intent, because a reading is owed the screen and not a
  summary of it.

`SightOptions.Enabled` (`Sight:Enabled`) is a second, separate switch: even
with a screenshot in hand, this decides whether it (or its OCR transcript)
ever reaches a vendor. Off means it is still read by the local OCR and
nothing leaves the machine. Three switches stand between a screen and a
network call, not one: capture, `Sight:Enabled`, and a `Substrates:Agents:Sight`
entry actually pointing at a provider with eyes.

`CloserEnabled` is the cost switch: off, nothing is ever sent at high detail
— the escalation is not bought and a reading drops to the cheap detail rather
than being refused. That is Budget. Free has no eyes at all.

The OCR transcript rides alongside the picture on every call and reaches
Intent as its own key, separate from the model's description: one is
evidence, the other judgement. A tier with no vision configured (Free) still takes
the shot, still reads it locally, and nothing leaves the machine — she knows
what the screen says and not what it looks like. Sight also keeps one turn of
context: the last screen, what was asked about it, and what Morrow answered,
because "tell me more about that" names nothing on the picture.

## Memory

| Store | Holds | Read at reply time |
|---|---|---|
| `archive/utterances/` | verbatim input and replies, append-only, by turn | no |
| `archive/facts/` | one claim per row, with vector, thread and supersession | yes (Recall) |
| `archive/passages.parquet` | Reflection's own thinking | yes (Hindsight) |
| `memory.jsonl` | agent state (persona) | yes (Identity) |

The utterances are the ground truth; everything else is derived from them and
`FactBackfill` rebuilds what is missing at boot.

**Write.** The extractor splits each utterance into standalone facts; a
greeting stores nothing, a failed call stores the utterance whole.

**Read.** Cosine over multilingual-e5-small plus a lexical lane, one row per
thread, MMR. The scores do not separate related from unrelated (`ReadMinScore`
is a guard, not a cutoff), so with `PickerEnabled` the sweep casts 40 wide and
a model picks up to 8 — one call on the turn's critical path.

| Tier | extractor, picker | Intent | Reflection | Sight |
|---|---|---|---|---|
| Free | local qwen3.5-2b | local | local | OCR only |
| Budget | local qwen3.5-2b | Mistral | OpenAI | glance only |
| Pro | OpenAI | OpenAI | Mistral | glance + look |
| Premium | OpenAI | Mistral | OpenAI | glance + look |

## Governance

Governance only makes decisions. It bundles advisories, gates on the
verdict, and counts revision passes. It also writes the honesty notice: a
fixed, non-model message that says which advisor was degraded or missing.

## Toolkits

The companion UI can expose one or more toolkits as first-class, async
operations. A toolkit is not a normal agent call stack: it is a dedicated
request/result pipeline that reports back into perception so the persona can
speak the outcome without making the tool execution itself part of the
turn-loop call chain.

- `events.toolkit.request` — request sent by the UI or a prompt-driven
  action to run a toolkit capability.
- `events.toolkit.result` — the execution result from the toolkit handler.
- `events.perception.toolkit` — a perception event created by the toolkit
  manager for logging and display, in parallel with the normal
  `events.perception` path.

The flow is:

1. The UI opens a left-side Toolkit panel listing available toolkits and
   their current status.
2. `ToolkitManagerAgent` fans a turn out to whichever registered toolkit's
   trigger exemplars it best matches, above `Toolkit:RouteFloor` — the built-in
   ones (`powershell`, `guide`, `accessibility`, `discord`) and any approved
   JSON manifest toolkit alike; the toolkit guide is always listed as a
   built-in explanation surface (`MorrowGuide` plus each registered
   descriptor).
3. `ToolkitHandlerAgent` executes the requested command in-process and
   publishes the structured result back to the bus.
4. `ToolkitManagerAgent` converts that result into a perception-ready text
   payload, which can then be vocalized by Morrow as ordinary perception.
5. Status ordering is stable: running toolkits first, then completed ones by
   finish time descending, then ready/unassigned entries by updated time.

This keeps toolkit execution explicit and observable: the system can explain
what it is doing, report a result, and keep the UI agent-local rather than
mixing tool execution into the core bus semantics.

**Built-in toolkits.** `PowerShellToolkit` translates natural language into a
script via a substrate call, then runs it; `AccessibilityToolkit` speaks text
aloud through the Windows speech engine; `DiscordToolkit` posts to a
configured channel through a bot token; `GuideToolkit` answers "what can you
do" from the live descriptor set plus `MorrowGuide`'s fixed prose about
Morrow herself.

**JSON manifest toolkits.** Anything dropped as a `*.json` file in the
`Toolkits/` directory is read at startup by `ManifestToolkitLoader` as a
`ToolkitManifest` — no rebuild required. A manifest can only compose one of
two fixed verbs (`http_call` to an author-fixed URL, or `speak_text`), never
arbitrary code, which keeps its ceiling small enough to be safe if the format
is ever exposed as a Steam-Workshop-style "subscribe" button. Every manifest
carries an `Approved` flag that defaults to `false` and that nothing in code
ever sets: a manifest that fails validation, or is valid but not yet
approved, is logged at startup and skipped, never registered as a callable
`ManifestToolkit`. Turning one on is a deliberate, human, one-line edit —
installing a manifest and it running are never the same action. See
`Toolkits/README.md` for the manifest schema.

**Consent gates.** Two of the built-in toolkits carry a real capability —
running commands, capturing the screen — and both are off by default on
every tier, with no tier file ever flipping them on:

- `PowerShellOptions.Approved` (`PowerShell:Approved`) gates
  `PowerShellToolkit.ExecuteAsync` directly, separately from the shared
  `Toolkit:Enabled` switch every toolkit answers to. `Toolkit:Enabled` decides
  whether the fan-out runs at all (off on Free/Budget); `PowerShell:Approved`
  is PowerShell's own switch for its own risk, and defaults to `false`
  everywhere. Not approved means `ExecuteAsync` returns a clear refusal
  outcome instead of translating or running anything.
- `ScreenShotOptions.Enabled` (`Shell:Screen:Enabled`), covered under Sight
  above, gates whether a screenshot is ever taken in the first place — same
  off-by-default, human-edit-only shape.

Both follow the same pattern as a manifest's `Approved` flag: a config value
nothing in code sets to `true`, so turning the feature on is a deliberate act
by the person who read what it does.

## Config over code

- **Tiers:** `appsettings.<Tier>.json` files, layered on top of the base
  config.
- **Substrates:** `Substrates:Agents` maps each model consumer to a provider.
  The map is validated at boot.
- **Instructions:** all prompt prose lives in `src/EciCas.Host/instructions/`,
  one file per agent. Everything is loaded at boot, and any error stops the
  host.
- **Runtime knobs:** these can be changed live from the Debug panel.

Vendors disagree about their own wire format, so the disagreements are
config too: `Substrates:Providers:<name>:MaxTokensField` names what an
endpoint calls the output ceiling, because OpenAI's reasoning models answer
400 to `max_tokens` while llama-server and Mistral still take it.
