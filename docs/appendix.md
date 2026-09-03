# Appendix

Operational notes: how to run the host, how to read what it prints, and the
traps that have already cost someone an afternoon. `architecture.md` says
what the system *is*; this says how to work with it when it surprises you.

## Running the host

```
dotnet run --project src/EciCas.Host -- --Tier=Default
```

`--Tier=X` layers `appsettings.X.json` over `appsettings.json`. Tiers are
`Default`, `Budget`, `Minimal`, `Super`; unset means no extra layer. The
switch also works as the `Tier` environment variable.

The host starts the web surface on `Surface:Url` (default
`http://localhost:5179`, SSE at `/api/stream`) and then reads prompts from
stdin. Empty line exits.

**The REPL only appears on a real console.** If stdin is redirected — a
pipe, a service, a container, an agent tool — `Console.IsInputRedirected`
is true and the host runs until shutdown instead, because the first
`ReadLine()` would return null and take the whole surface down with it.
Drive it with `POST /api/perceive` (`{"text": "..."}`) in that case.

## Debug tracing

```
dotnet run --project src/EciCas.Host -- --Tier=Default --Logging:LogLevel:EciCas=Debug
```

At `Information` the console shows one line per agent per turn. At `Debug`
it also shows both edges of both stores, and every prompt and raw response.
That is the level to use when tuning an instruction file, because it prints
what the agent was actually handed rather than a summary of it.

| Level | Agent | Line |
|---|---|---|
| info | Recall | facts published — full `Category/Topic/Subtopic/Subject/Key = Value (importance N)`, or `nothing on file` |
| info | Archivist | records written, full path and value |
| info | Reflection | internal records written; passages written with id and pair pointers |
| info | Hindsight | notes woken, deepest echo depth |
| info | Librarian | notes matched, leads resolved |
| debug | Librarian | the whole index; the pairs selected; passage hits with cosine scores |
| debug | Recall | every row read from parquet; the picking prompt and response |
| debug | Hindsight | hits with score, passage id, echo depth, pair pointers; why it didn't search; `woke nothing` with topK/minScore |
| debug | Reflection | which passage a revisit supersedes, parent ids, echo depth, generation, embedding model |
| debug | any cognitive agent | full prompt `>>>` and raw response `<<<` |

Identity is deliberately silent — it is a placeholder, and a cached lookup
with no substrate call has nothing per-turn to report.

## Where did the persona get that from?

The honest answer is usually "not where you think". In order of how often
it has fooled someone:

**Identity injects the persona on every turn.** `MorrowIdentity` is a
constant in `Program.cs`, seeded into `IAgentStateStore` at `self/identity`
and published as advice on every `events.perception`. It reaches Intent as
`[Identity: I'm Morrow. ...]`. No archive read, no log line, every turn. If
the persona "remembers" its own name, this is why.

**Recall can publish facts without making a substrate call.** When the
selected pairs hold `<= MaxPickedPerWorker` rows, the picking call is pure
overhead and gets skipped — the rows go straight to Intent. Recall's log
now fires from `Publish` so this path is visible, but any console capture
predating that commit cannot distinguish "recalled nothing" from "recalled
everything quietly".

**`Archivist nothing` is about writing, not reading.** It means the
extraction call found no new fact worth storing this turn. It says nothing
about what the archive already holds.

**There is no conversation history.** Intent's prompt is this turn's text
plus the bundle — advisories, recalled facts, woken notes — and nothing
else. `OpenAiCompatibleSubstrateProvider` posts a single `user` message to
`chat/completions` with no conversation or agent id. A provider's prompt
caching is a prefix cache for cost and latency; it cannot add content the
request did not carry.

**Paths are a fingerprint.** Intent renders recalled facts as JSON keyed by
the full five-part address, so a reply that quotes
`person/interaction/nationality/french guy/perceived nationality` verbatim
was reading a row, not reminiscing. Librarian's index only ever carries
`Category/Topic`, so anything deeper than two segments came from Recall.

## Passage retrieval is off by default

```
warn: No embedding model at ...models/embedding/model.onnx — passage
      retrieval is off until it is downloaded
```

Expected, not a failure. The sentence-transformer weights are ~90MB and
deliberately uncommitted. Without them Hindsight wakes nothing and
Librarian gets no passage leads; everything else runs as it did before
vectors existed, which is why this is a warning and not marked
`substrate.degraded`.

```
./scripts/get-embedding-model.ps1
```

Downloads them to `<repo>/models/embedding/` — outside `bin/`, so
`dotnet clean` won't delete them and every configuration shares one copy —
and prints the absolute paths for `Embedding` in `appsettings.json`.
Relative paths resolve against the build output, not the repo root.

Changing model later is a startup error rather than a silent swap: the
corpus stamps which model wrote it and the host refuses to search it with
another.

## Known flaky test

`ChannelBusTests.Publish_WithSlowSubscriber_ReturnsImmediately` asserts a
publish completes in under 5ms and can fail on cold-start JIT. The claim is
worth keeping; the margin is what's thin.
