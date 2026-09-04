# Appendix

How to run the host, how to read what it prints, and the traps that have
already cost someone an afternoon. `architecture.md` says what the system
*is*; this says how to work with it when it surprises you.

## Running the host

```
dotnet run --project src/EciCas.Host -- --Tier=Default
```

`--Tier=X` layers `appsettings.X.json` over `appsettings.json` — `Default`,
`Budget`, `Minimal`, `Super`; unset means no extra layer. Also works as the
`Tier` environment variable.

The host starts the surface on `Surface:Url` (default
`http://localhost:5179`, SSE at `/api/stream`) and then reads prompts from
stdin. Empty line exits.

**The REPL only appears on a real console.** With stdin redirected — a pipe,
a service, a container, an agent tool — the host runs until shutdown instead,
because the first `ReadLine()` would return null and take the surface down
with it. Drive it with `POST /api/perceive` (`{"text": "..."}`).

**Run from the `main` checkout.** The archive lives under the build output
(`bin/Debug/net10.0/archive`), so every checkout has a *different persona*.
Start from a worktree or second clone and you get a freshly seeded brain that
looks for all the world like Recall is broken. The tell is the first line of
the turn:

```
LibrarianAgent  Librarian index holds 1 pair(s)
```

One pair means an empty archive, which means the wrong folder. A real archive
has dozens. `git pull` does not move an archive; nothing does. The count
prints at `Information`; the pair list beside it needs `Debug`.

**A running host holds a lock on `bin/`.** Stop it before rebuilding, or the
build fails with a file-in-use error that never mentions the host.

## Resetting the archive

`dotnet run --project src/EciCas.ArchiveTool -- <archive-dir>`, then `reset`.
Deletes every `*.parquet` under that directory — shared tier and all profiles
— and reseeds one fact, `assistant/system/eci/this/version = 0.1`, so
Librarian always has at least one pair to index. Stop the host first (same
`bin/` lock). This is what "reset parquet" means when asked for.

## Reading the console

```
dotnet run --project src/EciCas.Host -- --Tier=Default --Logging:LogLevel:EciCas=Debug
```

At `Information`, one line per agent per turn — except Recall, which prints
one per picking call, since the fan-out is what scales with the archive. At
`Debug`, both edges of both stores plus every prompt and raw response. That
is the level for tuning an instruction file: it prints what the agent was
handed rather than a summary of it.

| Level | Agent | Line |
|---|---|---|
| info | Recall | facts published — full `Category/Topic/Subtopic/Subject/Key = Value (importance N)`, or `nothing on file` |
| info | Archivist | records written, full path and value |
| info | Reflection | internal records written; passages written with id and pair pointers |
| info | Hindsight | notes woken, deepest echo depth |
| info | Librarian | notes matched, leads resolved |
| debug | Librarian | the whole index; pairs selected; passage hits with cosine scores |
| debug | Recall | every row read from parquet; the picking prompt and response |
| debug | Hindsight | hits with score, passage id, echo depth, pair pointers; why it didn't search; `woke nothing` with topK/minScore |
| debug | Reflection | which passage a revisit supersedes, parent ids, echo depth, generation, embedding model |
| debug | any cognitive agent | full prompt `>>>` and raw response `<<<` |

Identity is deliberately silent — a cached lookup with no substrate call has
nothing per-turn to report.

## The turn log on disk

Everything the console prints is also projected into one `TurnRecord` per
event and served at `GET /api/log` (what a client missed) and
`GET /api/log/stream` (what happens next), both accepting `?profileId=`.

To keep a file too, set a path — relative resolves against the build output,
beside the archive:

```
dotnet run --project src/EciCas.Host --TurnLog:Path=turnlog.jsonl
```

One JSON object per line, appended once the event has been quiet for
`TurnLog:SettleMs` (3 s). That delay is not slack: Archivist and Reflection
land *behind* the reply, so a record written the moment the person is
answered would be missing what the turn remembered. A straggler still updates
memory and live clients but does not rewrite the line.

`TurnLog:Retain` (100) caps what `/api/log` can replay. It is memory, not
history — the file has no cap, and the archive remains the audit trail.

`TurnLog:CostPath` (`cost.json`, on by default) holds one number: everything
this installation has ever spent. The drawer's `Cost:` line reads *event ·
session · total* — record, host restart, that file. Delete it to zero the
lifetime figure; nothing else reads it.

## Where did the persona get that from?

Usually not where you think. In order of how often it has fooled someone:

**Identity injects the persona on every turn.** Published as advice on every
`events.perception`, reaching Intent as `[Identity: Your tone is warm,
unhurried, plain-spoken. You are called Morrow.]` — tone and name in one
aside, because one aside is what Intent reads. No log line, every turn.

Keep it to a few keywords. A paragraph there competes with the person's own
sentence instead of colouring the answer to it.

The text lives in `instructions/identity.txt`, but only as a *seed*: the host
writes it to `IAgentStateStore` at `assistant/persona` the first time it finds
that path empty, and reads the store from then on. Editing the file changes
nothing on an existing brain — deliberate, since a persona that grows should
not be silently rewritten by a `git pull`. The boot log says which happened.
To re-seed, delete the `assistant/persona` line from `memory.jsonl` and
restart. Which section is seeded is `Identity:Profile` — `grump`, `educator`,
`playmate`, or unset — so switching profiles means clearing that line too:

```powershell
Remove-Item src\EciCas.Host\bin\Debug\net10.0\memory.jsonl
dotnet run --project src\EciCas.Host --Identity:Profile=grump
```

**Its name is per profile, and the default is not a record.** Every profile
starts calling it `Identity:DefaultName` (`Morrow`) — a fallback, not a row,
so there is no seed for a rename to lose a race with. Renaming is ordinary
conversation: say what to call it and let Archivist decide the fact is worth
keeping. The address is `persona/name/this/assistant/name`, deliberately
*not* under the shared `assistant` category, so two people on one device name
their own persona and neither overwrites the other. The archive's single seed
record remains `assistant/system/eci/this/version = 0.1`.

Two undramatic reasons a rename doesn't stick. On the mock tier Archivist
extracts nothing at all — the console says `Archivist nothing`, and that is
the whole story. On a real tier it is a judgement call, so saying it once in
passing may not clear the bar; say it plainly. To check what landed:

```powershell
curl.exe -s "http://localhost:5179/api/persona?profileId=daniel"
```

That endpoint and Identity read the same object, so what it prints is what
Intent was told.

`memory.jsonl` also holds Impulse's drive states, keeping a window per path
rather than only the newest — the superseded ones are the only record of how
the persona has been moving. Reflection reads that window and is told the
direction in words ("engagement rising, warmth steady"), never the numbers,
which would only invite the persona to quote its own telemetry back.

**Recall can publish facts without a substrate call.** When the selected
pairs hold `<= MaxPickedPerWorker` rows the picking call is pure overhead and
gets skipped; the rows go straight to Intent. Recall's log fires from
`Publish` so this path is visible. Librarian had the same skip on the pair
index and no longer does — it calls the selector on every turn the index is
non-empty, so a `Librarian substrate call` on a three-pair archive is
expected.

**`Archivist nothing` is about writing, not reading.** The extraction call
found no new fact worth storing this turn. It says nothing about what the
archive already holds.

**On the mock tier, the reply is an echo.** `MockSubstrateProvider` answers a
numbered question with `0` and everything else with `[mock:<class>] ` plus
the turn as it reached that agent. It stops at the first bracketed aside,
because the `[Impulse: …] [Identity: …] [Recall: …]` run that follows is the
same every time and buried the one part worth reading. To see the asides, use
`--Logging:LogLevel:EciCas=Debug`, or the Debug drawer, which gives each its
own slot.

**There is no conversation history.** Intent's prompt is this turn's text
plus the bundle, and nothing else.
`OpenAiCompatibleSubstrateProvider` posts a single `user` message with no
conversation or agent id. A provider's prompt caching is a prefix cache for
cost and latency; it cannot add content the request did not carry.

**Paths are a fingerprint.** Intent renders recalled facts as JSON keyed by
the full five-part address, so a reply quoting
`person/interaction/nationality/french guy/perceived nationality` verbatim
was reading a row, not reminiscing. Librarian's index only ever carries
`Category/Topic`, so anything deeper than two segments came from Recall.

**`prompt >>>` is a log line, not an envelope.** It prints what goes into the
HTTP call — instructions plus context, since the rules have to reach the
model somehow. Nothing puts them on the bus: Intent publishes
`intent.context`, built by `BuildContext`, which is the turn's text and the
bundle's contributions and deliberately never the standing rules. Seeing an
instruction file in the console at `Debug` is not a leak.

## Passage retrieval is off by default

```
warn: No embedding model at ...models/embedding/model.onnx — passage
      retrieval is off until it is downloaded
```

Expected, not a failure. The weights are ~90MB and deliberately uncommitted.
Without them Hindsight wakes nothing and Librarian gets no passage leads;
everything else runs as it did before vectors existed, which is why this is a
warning and not `substrate.degraded`.

```
./scripts/get-embedding-model.ps1
```

Downloads them to `<repo>/models/embedding/` — outside `bin/`, so
`dotnet clean` won't delete them and every configuration shares one copy —
and prints the absolute paths for `Embedding` in `appsettings.json`. Relative
paths resolve against the build output, not the repo root.

Changing model later is a startup error rather than a silent swap: the corpus
stamps which model wrote it and the host refuses to search it with another.

## Known flaky tests

`ChannelBusTests.Publish_WithSlowSubscriber_ReturnsImmediately` asserts a
publish completes in under 5ms and can fail on cold-start JIT. The claim is
worth keeping; the margin is what's thin.

`PassageMemoryTests.PassageStore_RoundTripsPairsAndVectors_...` occasionally
fails with `UnauthorizedAccessException` from `File.Move`. The torn-write fix
writes a temp file and moves it over the real one; on Windows a virus scanner
holding the destination open for a few milliseconds turns that move into a
denial. Rerun before believing it.
