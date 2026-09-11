# Architecture

Narrow agents on a message bus. Each owns one queue; publish is
fire-and-forget; no agent names another. Tests assert outcomes, never
cross-agent ordering.

## A turn

1. **Perception** publishes `events.perception` with a fresh `CorrelationId`.
2. On that, in parallel: **Impulse** appraises, **Identity** reads the
   persona, **Hindsight** sweeps passages, **Recall** sweeps facts, and
   **Scribe** logs the utterance and extracts facts.
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

Impulse's Critical reflex can publish its own proposal, which Security gates
like any other. Wildcard subscribers (logger, console, SSE, turn log,
telemetry) watch everything and are invisible to agents.

## Memory

| Store | Holds | Read at reply time |
|---|---|---|
| `archive/utterances/` | verbatim input, append-only, with turn number; `turns.txt` +1 per concluded turn | no |
| `archive/replies/` | Morrow's replies, keyed by the input's turn; extractor context only | no |
| `archive/facts/` | one claim per row, with vector, thread and supersession | yes (Recall) |
| `archive/passages.parquet` | Reflection's own thinking | yes (Hindsight) |
| `memory.jsonl` | agent state (persona) | yes (Identity) |

The utterances are the ground truth. Facts are derived from them; at boot
`FactBackfill` extracts every utterance whose id no fact carries as its
source, then embeds and threads any fact missing a vector or thread.

**Write.** The extractor splits every utterance into standalone facts,
however short. `NONE` (a bare question, a greeting) stores nothing; a failed
call keeps the utterance as one fact. With `ExtractorEnabled` off, every
utterance is one fact. The consolidator (optional) decides joins onto an
existing thread above `ThreadThreshold`.

**Read.** Cosine over multilingual-e5-small plus a lexical lane, one row per
thread, MMR. e5 packs related and unrelated rows into roughly 0.73-0.83, so
no cutoff separates them (`ReadMinScore` 0.60 is a guard only). With
`PickerEnabled` the sweep casts 40 wide and the picker model returns up to 8
row numbers, or `NONE`; if it can't answer, cosine top-k stands. That is one
model call on every turn's critical path.

| Tier | extractor, picker | Intent | Reflection |
|---|---|---|---|
| Free | local qwen3.5-4b | local | local |
| Budget | local qwen3.5-4b | Mistral | OpenAI |
| Pro | OpenAI | OpenAI | Mistral |
| Premium | OpenAI | Mistral | OpenAI |

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
