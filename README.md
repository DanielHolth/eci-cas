# ECI-CAS

**Emergent Cognitive Identity** on a **Continuous Agent System**: a persistent
AI persona built as narrow faculties on a message bus — Perception,
TurnWindow, Impulse, Recall, Identity, Hindsight, Sight, Governance, Intent,
Security, Action, Scribe, Reflection. One queue per agent, fire-and-forget
publish, no agent names another. Every hop streams to the `morrow-eci/` UI
over SSE. Design: [`docs/architecture.md`](docs/architecture.md).

## Memory

- **`archive/utterances/`** — what the person said, verbatim. Append-only,
  never read at reply time. Ground truth.
- **`archive/replies/`** — what Morrow said back, same shape. Every row in
  both carries a timestamp and a turn number; a reply shares its input's
  turn. `utterances/turns.txt` holds the count, +1 per concluded turn. The
  extractor reads the previous reply as context, never as a source of facts.
- **`archive/facts/`** — standalone one-claim sentences extracted from each
  utterance, with embedding, thread and supersession. The only store Recall
  reads. Disposable: boot extracts any utterance that has no facts yet.

Scribe writes both after the reply. With `Utterances:ExtractorEnabled` (every
tier but Mock) a model splits every utterance into facts, however short; a
bare question or greeting answers `NONE` and stores nothing. With it off, or
if the call fails, the utterance is kept as one fact.

Recall: cosine (multilingual-e5-small) plus a lexical lane, collapsed to each
thread's newest member, diversified by MMR. With `Utterances:PickerEnabled`
the sweep casts `FanoutWidth` (40) and a model keeps up to `PickMax` (8) by
number, or `NONE`; if it can't answer, cosine top-k stands. Reflection's own
thinking lives in `passages.parquet`.

## Sight

She sees the screen. The moment the voice key arms, the shell takes a
screenshot, reads the text off it locally, and starts a cheap low-detail
glance that runs while the person is still speaking — so the description is
usually waiting by the time the turn needs it. A close look at full detail
costs sixteen times as much and is only taken when the turn says so — either
because the person asked or because the glance itself asked — and only on Pro
and Premium; Budget pays for the glance and stops there. "Read my screen
to me" is a third path that goes to the person unshortened.

The local text always rides along, so a tier with no vision model still knows
what the screen says — nothing about the picture leaves the machine on Free.
See the `Sight` block in `appsettings.json` for the trigger phrases, the
ceilings, and how long a turn waits for a glance.

## Layout

```
src/EciCas.Core/        contracts, envelope, topics
src/EciCas.Bus/         ChannelBus, AgentBase
src/EciCas.Agents/      faculties; Utterances/ is the fact store
src/EciCas.Substrates/  model providers (mock + OpenAI-compatible)
src/EciCas.Host/        wiring, HTTP/SSE, tier configs, instructions/
src/EciCas.Shell/       the desktop app: overlay watermark + session window
src/EciCas.ArchiveTool/ REPL over the archive
tests/EciCas.Tests/     xUnit
morrow-eci/             Next.js UI
```

## Run

```powershell
./start.cmd -Tier Pro
```

Starts llama-server, then Morrow herself: one process (`src/EciCas.Shell`,
built as `Morrow.exe`) that hosts the swarm on `:5179`, serves the exported
client at its own origin, and draws a click-through watermark in the corner
of the screen. Hold `-` to talk, `|` to make her clickable, then click
her for the full session in a second window; quit from the tray. The client
export is built once if `morrow-eci/out` is missing.

Talking to her needs the speech model, once:

```powershell
./scripts/get-whisper-model.ps1
```

~140MB of whisper.cpp weights into `models/whisper/`. Multilingual `base`
rather than `base.en`, and `-Model small` is the upgrade. Without it the voice
key says so in the tray and records nothing.

```powershell
./start.cmd -NoBuild
```

The everyday relaunch: starts the `Morrow.exe` that is already built instead
of compiling first, and still brings llama-server up. Every path Morrow reads
hangs off the exe's own folder, so this is the same session either way.

```powershell
./start.cmd -Tier Pro -Dev
```

The development shape instead: console host in one window, `next dev` in
another, browser tab on `:3000`. Hot reload and a REPL to type at.
`-WhatIfOnly`, `-NoLlm`, `-Port`/`-UiPort`/`-LlmPort` apply to both;
`-NoUi`/`-NoBrowser` only to `-Dev`.

llama-server starts on every tier, not only the local ones: the dropdown swaps
tiers live and an empty energy meter swaps itself to Free unasked, so the boot
tier does not decide what the session will need. It is skipped silently if
`models/local/` is empty — a launch never downloads weights.

The hotkeys are watched, not claimed: a timer asks the OS whether those two
keys are down, so they are neither a keyboard hook (invisible to anti-cheat)
nor a registered hotkey (nothing is taken from other applications -- `-` still
types a hyphen in a game, a chat box or a terminal). The cost of that is the
other half: typing a hyphen would also open the microphone, so the key has to
be held for `Dictation:HoldMs` (350ms) before anything records. Keys are named
by the character they type, resolved through whatever keyboard layout is in
front, and take optional `Ctrl+` / `Shift+` / `Alt+` / `Win+` prefixes -- see
the `Shell` section of `appsettings.json`.

Speech to text runs on the CPU inside that same process: the microphone is the
one input that carries the room, so it never leaves the machine whatever the
tier says. Release the key and the take is transcribed in one pass -- under a
second for a spoken sentence -- and what she heard appears under the face
before it is answered, because a transcript that arrived wrong is otherwise
indistinguishable from a bad reply. A take with nothing in it says so instead
of being transcribed into words nobody said.

By hand:

```powershell
$env:OPENAI_API_KEY  = "..."   # Pro: Intent, Sight, extractor, picker, consolidator
$env:MISTRAL_API_KEY = "..."   # Pro: Reflection
dotnet run --project src/EciCas.Host -- --Tier=Pro   # API only, on :5179
cd morrow-eci; npm install; npm run dev              # dev surface on :3000
cd morrow-eci; npm run build                         # the export the shell serves
dotnet run --project src/EciCas.Shell -- --Tier=Pro  # the desktop app
```

No keys: `--Tier=Mock` echoes prompts (machinery only); `--Tier=Free` runs
everything on a local Qwen3.5 2B for $0 via
`scripts/get-local-model.ps1 -Start` (needs llama.cpp on `:8080`). Budget is
Free's local extractor and picker with Intent (Mistral) and Reflection
(OpenAI) on the API.

```bash
dotnet test EciCas.slnx
dotnet run --project src/EciCas.Host -- --Verbose=true
dotnet run --project src/EciCas.ArchiveTool -- src/EciCas.Host/bin/Debug/net10.0/archive
```

Archive tool: `utterances`, `threads`, `passages`, `thread merge|split`,
`facts clear` (forces a rebuild next boot), `reset`, `help`. One process per
archive directory.

## Configure

`--Tier=X` layers `appsettings.<X>.json`: Mock, Free, Budget, Pro, Premium.
The Debug panel switches tiers live. Any key overrides from the command line.

`Substrates:Agents` maps each model consumer — `Intent`, `Reflection`,
`Sight`, `extractor`, `picker`, `consolidator` — to a provider; unset means
`mock`. Providers name an env var for the key, never the key itself, and
`MaxTokensField` names whatever that endpoint calls the output ceiling. The
table is validated at boot; `Agent substrate manifest drift` usually means a stale build
(`dotnet clean`).

`src/EciCas.Host/instructions/` holds every sentence the persona speaks, one
`.txt` per agent. `identity.txt` only seeds an empty store; delete
`bin/.../memory.jsonl` to re-seed (`--Identity:Profile=grump|educator|playmate`).

Speech: whisper.cpp `ggml-base.bin`, in-process on the CPU, fetched by
`scripts/get-whisper-model.ps1` into `models/whisper/`. Any single `ggml*.bin`
in that directory is used, so a bigger download needs no config edit. See the
`Dictation` block in the `Shell` section for the hold threshold, the ceiling on
one take, and the silence gate.

Embeddings: multilingual-e5-small, in-process ONNX, fetched by
`scripts/get-embedding-model.ps1` into `models/embedding/multilingual-e5-small/`
(`model.onnx` + `sentencepiece.bpe.model`), or `--Embedding:Provider=api`.
Vectors are stamped with the model; switching models means deleting
`passages.parquet` and `facts/` (boot rebuilds facts from utterances).
Without an embedder, recall falls back to lexical.

Measurements behind the knobs: `tools/retrieval-bench/RESULTS.md` (the
scripts are in git history).

## Docs

[architecture](docs/architecture.md) · [roadmap](docs/roadmap.md) ·
[history](docs/roadmap-history.md) · [appendix](docs/appendix.md) ·
[AGENTS.md](AGENTS.md)

Apache 2.0 — see [`LICENSE`](LICENSE).
