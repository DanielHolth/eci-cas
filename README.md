# ECI-CAS

**Emergent Cognitive Identity** on a **Continuous Agent System** — a
persistent AI persona built as faculties of a mind on a message bus:
Perception, Impulse, Librarian, Recall, Identity, Hindsight, Governance,
Intent, Security, Action, Archivist, Reflection. Personality emerges from the
interplay of narrow roles, not from any one of them.

Agents are genuinely decoupled — one queue and one listener each,
fire-and-forget publish, no agent naming another. A prompt enters through
Perception, fans out to advisory agents, resolves through Governance's
security gate, and comes out voiced by Action — every hop logged and streamed
to the `morrow-eci/` companion over SSE. Full design in
[`docs/architecture.md`](docs/architecture.md).

## Structure

```
src/
  EciCas.Core/        Envelope, MetaBag, Severity, Verdict, Topics, contracts
  EciCas.Bus/         ChannelBus, AgentBase, BusActivityTracker
  EciCas.Agents/      the twelve faculties
  EciCas.Substrates/  provider registry (mock + OpenAI-compatible HTTP)
  EciCas.Host/        host wiring, console, archive logger, turn log, HTTP surface
    instructions/     one .txt per agent — every sentence the persona speaks
  EciCas.ArchiveTool/ REPL over the Parquet archive
tests/EciCas.Tests/   xUnit
docs/                 architecture · roadmap · appendix
morrow-eci/           Next.js companion UI
```

## Run it

Two terminals. The host serves the bus and the SSE feed on `:5179`; the
surface talks to it from `:3000`.

```powershell
$env:MISTRAL_API_KEY = "..."      # fast-* classes
$env:OPENAI_API_KEY  = "..."      # slow-* classes
dotnet run --project src/EciCas.Host -- --Tier=Default
```

```powershell
cd morrow-eci
npm install                        # first time only
npm run dev
```

Open **http://localhost:3000**. The first screen asks who is talking — the
archive is scoped per person. Type and Send; the avatar and transcript follow
live. **Thoughts** (left) is a running list of what was recalled, learned and
reflected on; **Debug** (right) holds the live knobs and one row per event —
what each faculty contributed, what was read and written, cost and latency.

Order doesn't matter and neither survives the other: the surface retries the
stream, and a host started later is picked up without a reload. If the host
isn't on `:5179`, point the surface at it with `NEXT_PUBLIC_ECI_API_BASE`.

**No API keys?** Two free routes. `--Tier=Mock` needs nothing at all: each
substrate call echoes its prompt back, so `[mock:fast-high] Reply to: what is
a tide` is the echo, not a failure to answer — the right way to see the
machinery, not a way to hear the persona. `--Tier=Minimal` is the one that
actually thinks, running a local Qwen3.5 4B behind every faculty for $0; it
costs one ~2.5GB download and llama.cpp, both set up below.

Other entry points:

```bash
dotnet test EciCas.slnx                                  # build + full suite
dotnet run --project src/EciCas.Host -- --Verbose=true   # exhaustive trace
dotnet run --project src/EciCas.ArchiveTool -- src/EciCas.Host/bin/Debug/net10.0/archive
```

By default the host prints roughly a line per agent per turn. The archive
tool is a REPL (`list`, then `show <category> <topic>`) over the Parquet
files, which live beside the host's binary rather than in the repo root; only
one process may point at an archive directory at a time. See
[`docs/appendix.md`](docs/appendix.md) for reading the output and for the
traps that have already cost an afternoon.

## Configuration

`Tier` (env var or `--Tier=X`) layers `appsettings.<Tier>.json` over the
defaults: **Mock** (no substrate at all, $0, no dependencies), **Minimal**
(one local Qwen3.5 4B behind every class — free, but needs a server up),
**Budget** (cheap live models), **Default** (Mistral for `fast-*`, OpenAI for
`slow-*`), **Super**. Operator only — nothing edits this config or restarts
the process on your behalf.

The Debug panel's **Tier** dropdown switches between the same presets on a
running host, without a restart or a lost conversation: every tier file is
bound at boot and selecting one swaps the substrate table, the agent
assignments, and the Recall/Librarian sizing together. Tiers whose API keys
are unset are listed but greyed out, and the list runs cheapest to best --
`Tier:Rank` in each tier file, not a filename sort and not a C# enum.
`--Tier` decides what you boot into; the dropdown decides what you run next.

Minimal expects an OpenAI-compatible server on `http://localhost:8080/v1/`.
One command gets you there:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/get-local-model.ps1 -Start
```

That fetches the Qwen3.5 4B GGUF (~2.7GB, uncommitted, into `models/local/`),
installs llama.cpp via winget if it is missing, finds `llama-server.exe` even
when winget leaves it off PATH, reports which GPU it will use, and starts the
server. Drop `-Start` to get the launch line printed instead of run.

The flags it uses are not decorative. `-ngl 99` puts the layers on the GPU;
without it llama.cpp runs on CPU beside an idle card and a turn takes long
enough to feel broken rather than slow. `--jinja` is what lets the tier's
`Thinking` flag reach the chat template. The first call after a cold start
pays one-off shader compilation and can take seconds; warm picking calls land
in tens of milliseconds.

Without a server the tier degrades rather than crashing, but `Mock` is the
tier that is meant to run on nothing.

Any key can be overridden on the command line, which is the cheap way to
exercise one agent live against an otherwise mocked swarm:

```bash
dotnet run --project src/EciCas.Host -- --Substrates:Classes:fast-low:Provider=mistral --Recall:RowsPerWorker=5
```

Every class under `Substrates:Classes` defaults to `"mock"`. To go live, add
a provider (base URL plus the *name* of the env var holding its key — never a
literal key) and point classes at it; classes pick independently, so several
providers can be live at once. `AgentSubstrates:Agents` maps each cognitive
agent to a class, so an operator can retarget a role without touching C#:

```jsonc
"Substrates": {
  "Providers": {
    "openai":  { "BaseUrl": "https://api.openai.com/v1/",  "ApiKeyEnvironmentVariable": "OPENAI_API_KEY" },
    "mistral": { "BaseUrl": "https://api.mistral.ai/v1/",  "ApiKeyEnvironmentVariable": "MISTRAL_API_KEY" }
  },
  "Classes": { "fast-low": { "Provider": "mistral", "Model": "ministral-3b-2512" } }
},
"AgentSubstrates": {
  "Agents": { "Intent": { "Class": "fast-medium" }, "Archivist": { "Class": "slow-low" } }
}
```

Both blocks are validated at startup — including that `Class` names a real
class even when `UseSubstrate` is `false` — so a typo fails loud rather than
falling back to mock. `UseSubstrate: false` publishes the agent's fallback
instead of calling out, on any of the five substrate-calling agents; that is
the persona working as configured, not a degradation, so it is never marked
degraded.

`Routing manifest drift` on startup is almost always a stale build:
`dotnet clean src/EciCas.Host && dotnet build src/EciCas.Host`.

### The instructions folder

Every sentence the persona speaks or is steered by lives in
`src/EciCas.Host/instructions/`, one `.txt` per agent — prompts, but also
Identity's persona, Impulse's reflex reply and Governance's honesty notices,
which never reach a model at all. Changing how the persona sounds is editing
prose, not C#. Files split on `## ` markers and interpolate `{placeholder}`
values; a missing file or unknown placeholder fails at startup.

One exception to "edit the file, restart": `identity.txt` *seeds* the persona
only when the store is empty and is ignored thereafter, so a persona that has
grown survives a `git pull`. `Identity:Profile` picks the section to seed —
`grump`, `educator`, `playmate`, or unset for warm and plain-spoken. Switching
means clearing the stored one:

```powershell
Remove-Item src\EciCas.Host\bin\Debug\net10.0\memory.jsonl
dotnet run --project src/EciCas.Host -- --Tier=Default --Identity:Profile=grump
```

No profile carries a name. The persona answers to `Identity:DefaultName`
(`Morrow`) until someone renames it in a turn and Archivist judges it worth
writing down. That rename is stored per profile at
`persona/name/this/assistant/name` — deliberately not under the shared
`assistant` category, so two people on one device each name their own.

### Optional: the embedding model

Vector memory (the passage corpus — see
[architecture.md](docs/architecture.md#the-passage-corpus-what-it-missed-not-what-it-knows))
is off until you supply an embedding model, and **that is a supported way to
run**: the host logs one warning and the swarm behaves as it did before
vectors existed.

`./scripts/get-embedding-model.ps1` downloads `all-MiniLM-L6-v2` (~90MB) to
`<repo>/models/embedding/` — outside `bin/`, so `dotnet clean` won't delete
it — and prints the absolute paths for the `Embedding` config block. Any
BERT-family ONNX sentence-transformer export plus its `vocab.txt` works.
Relative paths resolve against the build output, not the repo root; `models/`
is gitignored. `Embedding:Provider=api` calls an OpenAI-compatible
`embeddings` endpoint instead, reusing a configured provider:

```bash
dotnet run --project src/EciCas.Host -- --Embedding:Provider=api --Embedding:ApiProvider=openai
```

## Docs

- [`docs/architecture.md`](docs/architecture.md) — what exists: roster, bus, storage, the knowledge swarm, verification
- [`docs/roadmap.md`](docs/roadmap.md) — what's next, what's parked, what's out of scope, design records for shipped work
- [`docs/appendix.md`](docs/appendix.md) — operational notes, and where the persona really got that from
- [`AGENTS.md`](AGENTS.md) — standing engineering rules

The Python prototype this replaced lives unmodified in a sibling repo,
`eci-cas-python-prototype`. Nothing here depends on it.

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
