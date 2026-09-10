# ECI-CAS

**Emergent Cognitive Identity** on a **Continuous Agent System**: a persistent
AI persona built as narrow faculties on a message bus — Perception,
TurnWindow, Impulse, Recall, Identity, Hindsight, Governance, Intent,
Security, Action, Scribe, Reflection. One queue per agent, fire-and-forget
publish, no agent names another. Every hop streams to the `morrow-eci/` UI
over SSE. Design: [`docs/architecture.md`](docs/architecture.md).

## Memory

- **`archive/utterances/`** — what the person said, verbatim. Append-only,
  never read at reply time. Ground truth.
- **`archive/facts/`** — standalone one-claim sentences extracted from each
  utterance, with embedding, thread and supersession. The only store Recall
  reads. Disposable: rebuilt from utterances at boot.

Scribe writes both after the reply. With `Utterances:ExtractorEnabled`
(Default, Super) a model splits long messages into facts; otherwise the
utterance is one fact. Recall is one cosine sweep plus a lexical lane,
collapsed to each thread's newest member, diversified by MMR. Reflection's
own thinking lives in `passages.parquet`.

## Layout

```
src/EciCas.Core/        contracts, envelope, topics
src/EciCas.Bus/         ChannelBus, AgentBase
src/EciCas.Agents/      faculties; Utterances/ is the fact store
src/EciCas.Substrates/  model providers (mock + OpenAI-compatible)
src/EciCas.Host/        wiring, HTTP/SSE, tier configs, instructions/
src/EciCas.ArchiveTool/ REPL over the archive
tests/EciCas.Tests/     xUnit
morrow-eci/             Next.js UI
```

## Run

```powershell
./start.cmd -Tier Default
```

Starts llama-server (if the tier needs it), the host on `:5179` and the UI on
`:3000`, then opens the browser. `-WhatIfOnly`, `-NoUi`, `-NoLlm`,
`-NoBrowser`, `-Port`/`-UiPort`/`-LlmPort`.

By hand:

```powershell
$env:OPENAI_API_KEY  = "..."   # Intent, extractor, consolidator
$env:MISTRAL_API_KEY = "..."   # Reflection
dotnet run --project src/EciCas.Host -- --Tier=Default
cd morrow-eci; npm install; npm run dev
```

No keys: `--Tier=Mock` echoes prompts (machinery only); `--Tier=Minimal` runs
a local Qwen3.5 4B for $0 via
`scripts/get-local-model.ps1 -Start` (needs llama.cpp on `:8080`).

```bash
dotnet test EciCas.slnx
dotnet run --project src/EciCas.Host -- --Verbose=true
dotnet run --project src/EciCas.ArchiveTool -- src/EciCas.Host/bin/Debug/net10.0/archive
```

Archive tool: `utterances`, `threads`, `passages`, `thread merge|split`,
`facts clear` (forces a rebuild next boot), `reset`, `help`. One process per
archive directory.

## Configure

`--Tier=X` layers `appsettings.<X>.json`: Mock, Minimal, Budget, Default,
Super. The Debug panel switches tiers live. Any key overrides from the
command line.

`Substrates:Agents` maps each model consumer — `Intent`, `Reflection`,
`extractor`, `consolidator` — to a provider; unset means `mock`. Providers
name an env var for the key, never the key itself. The table is validated at
boot; `Agent substrate manifest drift` usually means a stale build
(`dotnet clean`).

`src/EciCas.Host/instructions/` holds every sentence the persona speaks, one
`.txt` per agent. `identity.txt` only seeds an empty store; delete
`bin/.../memory.jsonl` to re-seed (`--Identity:Profile=grump|educator|playmate`).

Embeddings: `scripts/get-embedding-model.ps1` (ONNX, into `models/embedding/`)
or `--Embedding:Provider=api`. Without one, recall falls back to lexical.

## Docs

[architecture](docs/architecture.md) · [roadmap](docs/roadmap.md) ·
[history](docs/roadmap-history.md) · [appendix](docs/appendix.md) ·
[AGENTS.md](AGENTS.md)

Apache 2.0 — see [`LICENSE`](LICENSE).
