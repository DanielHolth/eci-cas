# ECI-CAS

**Emergent Cognitive Identity (ECI)**, powered by the **Continuous Agent
System (CAS)** — a persistent, multi-substrate AI persona built as a
12-agent ecosystem on a message bus. Personality emerges from the
interplay of narrowly specialized roles, not from any single agent.

This repo is the **C# rebuild**: a from-scratch redesign with
genuinely decoupled, independently-listening agents (queue-per-agent,
fire-and-forget publish — see [`docs/csharp-rebuild-spec.md`](docs/csharp-rebuild-spec.md)
for the target architecture and why). No C# code exists here yet;
`docs/` and the `morrow-eci/` frontend are the current contents.

The original Python implementation that motivated this rebuild lives,
unmodified, in a sibling folder/repo — `eci-cas-python-prototype` —
pushed to its own remote as a fallback reference. It is not part of
this repo and nothing here depends on it.

## Structure

```
eci-cas/
  docs/                    csharp-rebuild-spec.md — target architecture
                            roadmap.md — planned milestones
                            handover.md — session pickup notes
  morrow-eci/               Next.js companion UI (frontend), unaffected by the rebuild
  .github/copilot-instructions.md   C# style/architecture conventions for this rebuild
```

## Docs

- [`docs/csharp-rebuild-spec.md`](docs/csharp-rebuild-spec.md) — target architecture, what carries over from the Python prototype and what doesn't
- [`docs/roadmap.md`](docs/roadmap.md) — planned milestones, current state
- [`docs/handover.md`](docs/handover.md) — open design questions, session pickup notes
- [`AGENTS.md`](AGENTS.md) — standing engineering rules (loose coupling/async is non-negotiable)

## License

Apache 2.0 — see [`LICENSE`](LICENSE).
