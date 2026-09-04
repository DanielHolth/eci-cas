# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as a
faculty-of-a-mind agent ecosystem on a message bus: Perception, Impulse,
Librarian, Recall, Identity, Governance, Intent, Security, Action,
Archivist, Reflection. Personality emerges from the interplay of
narrowly specialized roles, not from any single agent.

Agents are genuinely decoupled — one queue and one listener per agent,
fire-and-forget publish, no agent calls or knows about another directly.
See [`docs/architecture.md`](docs/architecture.md) for the full design.

A prompt goes in through Perception, fans out to advisory agents,
resolves through Governance's security gate, and comes out as a voiced
reply through Action — every hop logged to `archive.jsonl` and streamed
live to the `morrow-eci/` companion UI over SSE.

## Structure

```
eci-cas/
  EciCas.slnx
  src/
    EciCas.Core/         Envelope, MetaBag, Severity, Verdict, Topics, agent/bus/store/substrate contracts
    EciCas.Bus/           ChannelBus, AgentBase, BusActivityTracker
    EciCas.Agents/         Perception, Impulse, Librarian, Recall, Identity, Governance, Intent, Security, Action, Archivist, Reflection
    EciCas.Substrates/     substrate provider registry (mock + live OpenAI-compatible HTTP)
    EciCas.Host/            Generic Host wiring, ConsoleSubscriber, ArchiveLogger, routing manifest, SSE endpoint
      instructions/          one .txt per agent — every sentence the persona speaks or is steered by
    EciCas.ArchiveTool/      console REPL for inspecting/editing the Parquet archive
  tests/EciCas.Tests/      xUnit
  docs/                     architecture.md — system design; roadmap.md — plans, parked work, design records; appendix.md — operational notes
  morrow-eci/                Next.js companion UI, consumes the SSE stream
  .github/copilot-instructions.md   C# style/architecture conventions
```

## Run it locally

```bash
dotnet test EciCas.slnx                    # build + full test suite
dotnet run --project src/EciCas.Host       # interactive prompt loop (SSE + console)
cd morrow-eci && npm install && npm run dev   # companion UI at localhost:3000
```

Two console tools, run separately:

```bash
dotnet run --project src/EciCas.Host -- --Tier=Default --Verbose=true
```

```bash
dotnet run --project src/EciCas.ArchiveTool -- src/EciCas.Host/bin/Debug/net10.0/archive
```

The host prints roughly a line per agent per turn by default — substrate
cost, what Recall read, what Archivist/Reflection wrote, what Intent said,
what Security blocked. It is not a fixed count: Recall prints one line per
picking call, so a deep archive is noisier than a young one.
`--Verbose=true` restores the exhaustive per-envelope trace. The archive
tool is a REPL over the same Parquet files (`list`, then
`show <category> <topic>`); it takes the archive directory as its one
argument, which lives next to the host's binary rather than in the repo
root. On Windows use PowerShell or forward slashes — Git Bash mangles a
backslash-prefixed path. Only one process should point at a given archive
directory at a time.

Any config key can be overridden on the command line, which is the cheap way
to exercise one agent live against an otherwise mocked swarm, or to force
Recall's fan-out to split without needing hundreds of rows:

```bash
dotnet run --project src/EciCas.Host -- --Substrates:Classes:fast-low:Provider=mistral --Recall:RowsPerWorker=5
```

No API key needed — every substrate class under `Substrates:Classes` in
`appsettings.json` defaults to `"mock"`. To go live, add a provider under
`Substrates:Providers` (base URL + the *name* of the env var holding its
key — never a literal key in config) and point one or more classes at it.
Multiple providers can be live simultaneously — e.g. `fast-*` classes on
Mistral, `slow-*` on OpenAI — since each class picks its provider
independently:

```jsonc
"Substrates": {
  "Providers": {
    "openai":  { "BaseUrl": "https://api.openai.com/v1/",  "ApiKeyEnvironmentVariable": "OPENAI_API_KEY" },
    "mistral": { "BaseUrl": "https://api.mistral.ai/v1/",  "ApiKeyEnvironmentVariable": "MISTRAL_API_KEY" }
  },
  "Classes": {
    "fast-low":     { "Provider": "mistral", "Model": "ministral-3b-2512" },
    "slow-medium":  { "Provider": "openai",  "Model": "gpt-4o" }
  }
}
```

Which class each cognitive agent (Intent, Librarian, Recall, Reflection,
Archivist) uses comes from `AgentSubstrates:Agents` in `appsettings.json` —
an operator can retarget a role, or add a class like `fast-local`, without
touching C#:

```jsonc
"AgentSubstrates": {
  "Agents": {
    "Intent":       { "Class": "fast-medium" },
    "Archivist": { "Class": "slow-low" }
  }
}
```

### The instructions folder

Every sentence the persona speaks or is steered by lives in
`src/EciCas.Host/instructions/`, one `.txt` per agent — prompts, but also
Identity's persona, Impulse's reflex reply and Governance's honesty notices,
which never reach a model at all. Changing how the persona sounds is editing
prose, not C#. Files split into sections on `## ` markers and interpolate
`{placeholder}` values the agent supplies; a missing file or an unknown
placeholder fails at startup rather than quietly shipping an agent with no
standing text.

One exception to "edit the file, restart": `identity.txt` *seeds* the
persona the first time the store is empty and is ignored thereafter, so a
persona that has grown is not overwritten by a `git pull`. The boot log says
which happened; delete the `assistant/persona` line from `memory.jsonl` to
re-seed.

`UseSubstrate: false` skips the substrate call and publishes the agent's
fallback instead. It is honoured on all five substrate-calling agents —
Intent, Librarian, Recall, Reflection and Archivist — and is deliberately
*not* treated as a degradation: a turn run without a substrate by
configuration is the persona working as configured, not the persona
apologising. Note Intent's fallback is a fixed sentence — the `## fallback`
section of `instructions/intent.txt` — so turning it off there makes that the
only reply the persona can give.

Both this manifest and `Substrates:Classes` are validated at startup — including that
`Class` names a real substrate class even when `UseSubstrate` is `false` — so
a typo in either one fails loud before the bus starts rather than silently
falling back to mock.

The `mock` provider echoes the prompt's last line back — where every prompt
here puts the turn itself, so the echo stays diagnostic without pasting a
wall of rules into the companion's speech bubble. The exception is the two
prompts that
enumerate candidates and expect index numbers back — Librarian picking
archive pairs and Recall picking rows. Those get the first index, so the whole
knowledge swarm runs end to end on the free tier instead of silently taking
its empty path.

To swap a whole bundle of provider/model choices at once, set the `Tier`
config value (env var or `--Tier=X`) to layer in `appsettings.<Tier>.json`
over the defaults — `Minimal` (all mock, $0), `Budget` (cheap live models),
`Default` (the real vendor/model mix — Mistral for `fast-*`, OpenAI for
`slow-*`), and `Super` (highest-tier models). This is an operator-only
escape hatch for now: nothing automated edits this config or restarts the
process on your behalf.

If `dotnet run` throws `Routing manifest drift` or `Agent substrate manifest
drift` on startup, it's almost always a stale build — the agent roster or
manifest changed since the last compile. Clean and rebuild:

```bash
dotnet clean src/EciCas.Host && dotnet build src/EciCas.Host && dotnet run --project src/EciCas.Host
```

### Optional: the embedding model

Vector memory (the passage corpus — see
[architecture.md](docs/architecture.md#the-passage-corpus-what-it-missed-not-what-it-knows))
is off until you supply an embedding model, and **that is a supported way to
run**, not a broken one: with no model present the host logs one warning at
startup and the swarm behaves exactly as it did before vectors existed.

To turn it on, drop any BERT-family ONNX sentence-transformer export and its
`vocab.txt` — `all-MiniLM-L6-v2` is a good default at ~90MB — into
`models/embedding/` next to the host's binary, the same place `archive/`
lives:

```
src/EciCas.Host/bin/Debug/net10.0/models/embedding/model.onnx
src/EciCas.Host/bin/Debug/net10.0/models/embedding/vocab.txt
```

The model is not committed; `models/` is gitignored. Paths come from the
`Embedding` config block, so an absolute path outside the build output works
too. Setting `Embedding:Provider=api` calls an OpenAI-compatible
`embeddings` endpoint instead, reusing a provider already configured under
`Substrates:Providers` for its base URL and key:

```bash
dotnet run --project src/EciCas.Host -- --Embedding:Provider=api --Embedding:ApiProvider=openai
```

## Docs

Four docs, deliberately:

- [`docs/architecture.md`](docs/architecture.md) — what exists: agent roster, bus mechanics, storage, the knowledge swarm, archive tool, verification
- [`docs/roadmap.md`](docs/roadmap.md) — everything else: what's next, what's parked, what's out of scope, and the design records for shipped work
- [`docs/appendix.md`](docs/appendix.md) — operational notes: running the host, reading its output, and where the persona really got that from
- [`AGENTS.md`](AGENTS.md) — standing engineering rules (loose coupling/async is non-negotiable)

The Python prototype this project replaced lives, unmodified, in a
sibling folder/repo — `eci-cas-python-prototype` — pushed to its own
remote as a fallback reference. It is not part of this repo and nothing
here depends on it.

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
