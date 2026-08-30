# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as a
faculty-of-a-mind agent ecosystem on a message bus: Perception, Impulse,
Reasoning, Recall, Self, Governance, Intent, Security, Action,
Consolidator, Reflection. Personality emerges from the interplay of
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
    EciCas.Agents/         Perception, Impulse, Reasoning, Recall, Self, Governance, Intent, Security, Action, Consolidator, Reflection
    EciCas.Substrates/     substrate provider registry (mock + live OpenAI-compatible HTTP)
    EciCas.Host/            Generic Host wiring, ConsoleSubscriber, ArchiveLogger, routing manifest, SSE endpoint
  tests/EciCas.Tests/      xUnit
  docs/                     architecture.md — system design; roadmap.md — what's ahead
  morrow-eci/                Next.js companion UI, consumes the SSE stream
  .github/copilot-instructions.md   C# style/architecture conventions
```

## Run it locally

```bash
dotnet test EciCas.slnx                    # build + full test suite
dotnet run --project src/EciCas.Host       # interactive prompt loop (SSE + console)
cd morrow-eci && npm install && npm run dev   # companion UI at localhost:3000
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

Which class each cognitive agent (Intent, Reasoning, Reflection, Consolidator)
uses comes from `AgentSubstrates:Agents` in `appsettings.json` — an operator
can retarget a role, or add a class like `fast-local`, without touching C#:

```jsonc
"AgentSubstrates": {
  "Agents": {
    "Intent": { "Class": "fast-medium" },
    "Consolidator": { "Class": "fast-low", "UseSubstrate": false }
  }
}
```

`UseSubstrate` defaults to `true`; setting it `false` skips the substrate call
entirely (the agent publishes its fallback result instead) — Consolidator
ships with it off, since its deterministic keyword write already covers most
turns without an LLM call. Both this manifest and `Substrates:Classes` are
validated at startup — including that `Class` names a real substrate class
even when `UseSubstrate` is `false` — so a typo in either one fails loud
before the bus starts rather than silently falling back to mock.

To swap a whole bundle of provider/model choices at once, set the `Tier`
config value (env var or `--Tier=X`) to layer in `appsettings.<Tier>.json`
over the defaults — see `appsettings.Minimal.json`/`Budget.json`/`Super.json`
for examples. This is an operator-only escape hatch for now: nothing
automated edits this config or restarts the process on your behalf.

If `dotnet run` throws `Routing manifest drift` or `Agent substrate manifest
drift` on startup, it's almost always a stale build — the agent roster or
manifest changed since the last compile. Clean and rebuild:

```bash
dotnet clean src/EciCas.Host && dotnet build src/EciCas.Host && dotnet run --project src/EciCas.Host
```

## Docs

- [`docs/architecture.md`](docs/architecture.md) — system design: agent roster, bus mechanics, storage, verification
- [`docs/roadmap.md`](docs/roadmap.md) — what's ahead, open design questions
- [`AGENTS.md`](AGENTS.md) — standing engineering rules (loose coupling/async is non-negotiable)

The Python prototype this project replaced lives, unmodified, in a
sibling folder/repo — `eci-cas-python-prototype` — pushed to its own
remote as a fallback reference. It is not part of this repo and nothing
here depends on it.

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
