---
mode: "agent"
description: "Convert a Python agent to its C# equivalent following ECI-CAS conventions."
---

# Convert Agent to C#

You are converting a single ECI-CAS agent from Python to C#.

## Input

The user will specify which agent to convert (e.g. "governance", "intent", "sensory").

## Steps

1. Read the Python agent directory under `agents/{name}/` — all `.py` files.
2. Identify the public contract: what topics it subscribes to, what it publishes, what dependencies it takes.
3. Create the C# equivalent under `src/EciCas.Agents/{Name}/`:
   - `{Name}Agent.cs` implementing `IAgent`.
   - Supporting types (options, enums, helpers) as separate files only when needed.
4. Register the agent in DI if a composition root exists.
5. Create or extend the xUnit test class under `src/EciCas.Tests/` matching the existing phase-numbered convention.

## Rules

- Follow `.github/copilot-instructions.md` for all style, architecture, and naming decisions.
- Preserve the exact topic names, severity semantics, and security-verdict routing.
- Keep the same `HandleAsync(Envelope envelope, CancellationToken ct)` signature.
- Use constructor injection for all dependencies (`IMessageBus`, `IOptions<T>`, etc.).
- Do not add features, abstractions, or error handling beyond what the Python agent does.
- Do not carry over Python idioms — write idiomatic C#.
- Ask before creating any file outside `src/`.
