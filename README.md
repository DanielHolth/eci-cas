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

Keywords: **local dev setup**, **getting started**, **quickstart**,
**dotnet run**, **npm run dev**. No API key needed — every `Budget:Tiers`
entry defaults to `"mock"`. To use a live substrate, set the environment
variable named in `appsettings.json`'s `SubstrateProvider` config (default
`OPENAI_API_KEY`) — never put a literal key in config.

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
