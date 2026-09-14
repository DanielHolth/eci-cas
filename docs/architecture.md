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

The screen, as an advisory beside the rest. The shell captures a screenshot
and runs local OCR the moment the voice key arms, and `Glimpse` starts a
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
