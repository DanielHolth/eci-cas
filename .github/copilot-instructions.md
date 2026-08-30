---
applyTo: "**/*.cs,**/*.csproj"
---

# ECI-CAS C# Guidelines

## General

- Target .NET 10 (LTS), latest C# features.
- MVP first — no speculative abstractions or premature generalisation.
- Flat code: early returns, guard clauses, max two levels of nesting.
- Composition over inheritance.

## Naming & Style

- PascalCase for public members, camelCase for private fields and locals, `I` prefix for interfaces.
- File-scoped namespaces.
- Nullable reference types enabled; use `is null` / `is not null`.

## Architecture

- Constructor injection via `Microsoft.Extensions.DependencyInjection`.
- Program to interfaces, not implementations.
- `async`/`await` for all I/O — never `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.
- Records for immutable data (envelopes, requests, responses).
- `IOptions<T>` for manifest-driven configuration.

## ECI-CAS Specifics

- Agents implement `IAgent` → `Task HandleAsync(Envelope envelope, CancellationToken ct)`.
- In-process pub-sub bus via `IMessageBus`: topic-based, envelope-centric,
  queue-per-agent, fire-and-forget publish — `Publish()` must never await
  a subscriber's own handling. See `docs/architecture.md`.
- Fan-out uses `Task.WhenAll` for parallel agent dispatch.
- `SemaphoreSlim` for async synchronisation (governance buffering).
- `Severity` enum with OR-upscale-only rule — never lower upstream severity.
- Security verdicts: Green → Action, Yellow → one Intent revision then proceed, Red → deterministic Blocked notice (never reaches Action).
- Budget tiers drive substrate selection — never hardcode model names.
- Substrate providers implement `ISubstrateProvider`, registered via DI.

## Testing

- xUnit with `[Fact]` / `[Theory]`.
- Name tests `MethodName_Condition_ExpectedResult`.
- No Arrange / Act / Assert comments.
- `[Trait("Category", "Live")]` for real-LLM tests (opt-in via env var).
- `[Trait("Category", "Calibration")]` for judgment tests.
- Moq or NSubstitute for mocking — pick one per project and stay consistent.

## Project Layout

- `EciCas.Core` — shared types: Envelope, Severity, agent contracts.
- `EciCas.Bus` — pub-sub, envelope routing.
- `EciCas.Agents` — all agent implementations.
- `EciCas.Substrates` — provider interfaces and implementations.
- `EciCas.Host` — Generic Host wiring, DI registration, `ConsoleSubscriber`, `ArchiveLogger`, routing manifest.
- `EciCas.Tests` — all tests.
