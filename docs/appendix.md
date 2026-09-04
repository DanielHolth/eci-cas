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

**Run from the `main` checkout.** The
archive lives under the build output (`bin/Debug/net10.0/archive`), so
every checkout has a *different persona*. Start the host from a worktree
or a second clone and you get a freshly seeded brain that knows only
`assistant/identity/persona/this/name = morrow`, answers "what is my
name?" with "You're Morrow", and looks for all the world like Recall is
broken. The tell is in the first line of the turn:

```
LibrarianAgent  Librarian index holds 1 pair(s)
```

One pair means an empty archive, which means the wrong folder. A real
archive has dozens. `git pull` does not move an archive; nothing does.

The count prints at `Information`, so this is visible in an ordinary
session; the pair list beside it needs `Debug`.

**A running host holds a lock on `bin/`.** Stop it before rebuilding, or
the build fails with a file-in-use error rather than anything that
mentions the host still running.

## Debug tracing

```
dotnet run --project src/EciCas.Host -- --Tier=Default --Logging:LogLevel:EciCas=Debug
```

At `Information` the console shows one line per agent per turn, except
Recall, which prints one line per picking call — the fan-out is what
scales with the archive, so a single folded total hid the number worth
seeing. At `Debug`
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

## The turn log on disk

Everything the console prints about a turn is also projected into one
`TurnRecord` per event and served at `GET /api/log` (what a client missed)
and `GET /api/log/stream` (what happens next), both accepting `?profileId=`.

To keep a file as well, set a path — relative resolves against the build
output, beside the archive:

```
dotnet run --project src/EciCas.Host --TurnLog:Path=turnlog.jsonl
```

One JSON object per line, appended when the event has been quiet for
`TurnLog:SettleMs` (3 s by default). That delay is not slack: Archivist and
Reflection land *behind* the reply, so a record written the moment the
person is answered would be missing what the turn remembered. A line is
written once — a straggler still updates memory and live clients but does
not rewrite the file.

`TurnLog:Retain` (100) caps what `/api/log` can replay. It is memory, not
history; the file has no cap, and the archive remains the audit trail.

## Where did the persona get that from?

The honest answer is usually "not where you think". In order of how often
it has fooled someone:

**Identity injects the persona on every turn.** It is published as advice on
every `events.perception` and reaches Intent as `[Identity: You are Morrow.
...]`. No archive read, no log line, every turn. If the persona "remembers"
its own name, this is why.

The text lives in `instructions/identity.txt`, but only as a *seed*: the host
writes it to `IAgentStateStore` at `assistant/persona` the first time it finds
that path empty, and reads the store from then on. Editing the file changes
nothing on an existing brain — that is deliberate, since a persona that grows
should not be silently rewritten by a `git pull`. The boot log says which of
the two happened. To re-seed, delete the `assistant/persona` line from
`memory.jsonl` and restart.

`memory.jsonl` also holds Impulse's drive states, and keeps a window of them
per path rather than only the newest, because the superseded ones are the only
record of how the persona has been moving. Reflection reads that window and is
told the direction in words — "engagement rising, warmth steady" — never the
numbers, which would only invite the persona to quote its own telemetry back.

**Recall can publish facts without making a substrate call.** When the
selected pairs hold `<= MaxPickedPerWorker` rows, the picking call is pure
overhead and gets skipped — the rows go straight to Intent. Recall's log
now fires from `Publish` so this path is visible, but any console capture
predating that commit cannot distinguish "recalled nothing" from "recalled
everything quietly".

Librarian had the same skip on the pair index and no longer does — it calls
the selector on every turn the index is non-empty. So a `Librarian substrate
call` line on a three-pair archive is expected, not a sign the archive grew.

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

**`prompt >>>` is a log line, not an envelope.** It prints what goes into
the HTTP call, which is instructions plus context — the rules have to
reach the model somehow. Nothing puts them on the bus: Intent publishes
`intent.context`, built by `BuildContext`, which is the turn's text and
the bundle's contributions and deliberately never the standing rules.
Seeing an instruction file in the console at `Debug` is not a leak.

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

`PassageMemoryTests.PassageStore_RoundTripsPairsAndVectors_...` occasionally
fails with `UnauthorizedAccessException` from `File.Move` in
`ParquetPassageStore.WriteAsync`. The torn-write fix writes a temp file and
moves it over the real one; on Windows a virus scanner holding the
destination open for a few milliseconds turns that move into a denial. Rerun
before believing it.
