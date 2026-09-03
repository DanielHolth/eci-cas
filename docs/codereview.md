# ECI-CAS — Code review

A full pass over `src/`, `tools/` and `tests/` at `89085f7`, plus the
twelve commits since `032c1f1`. Findings first, ordered by what they cost
if left alone; ideas after, in their own chapter.

Two of these are live on a running host today. Everything from Medium
down is a landmine rather than a fire.

---

## Findings

### 1. The persona reads its own documentation out loud — live

`a72e6b4` moved every spoken sentence into `instructions/*.txt`, which
was right. But the files explain themselves to the reader in the same
block they hand to the caller, and nothing separates the two.
`InstructionFile.Parse` puts *everything* before the first `## ` marker
into `main` and everything under a marker into that section. Commentary
included.

Verified by dumping what the store actually returns:

```
=== Impulse reflex reply (published straight to the user) ===
Impulse makes no substrate call. This is the reflex reply it publishes
directly when a turn trips a critical trigger — the one sentence the person
sees before any thinking has happened, so it promises attention and nothing
else.
I can see this is urgent. I'm on it.
```

`ImpulseAgent` publishes that whole string as `IntentAgent.ReplyKey` on
any critical trigger. Four files are affected:

| File | Section | Where it surfaces |
| --- | --- | --- |
| `impulse.txt` | main | the reflex reply, spoken before any thinking |
| `governance.txt` | all four | every degraded/blocked notice |
| `identity.txt` | main | seeded persona, then `[Identity: …]` in every Intent prompt |
| `identity.txt` | stranger | same, when the store is empty |

`governance.txt`'s `reasoning-down` names `{cause}` **twice**, so a timed-out
substrate says:

> Replaces the reply outright: Intent never produced one. timed out is
> filled in with what went wrong — "unreachable", "timed out", "refused".
> I can't think that through right now — my reasoning substrate is timed out.

Why nobody caught it: `GovernanceAgentTests` asserts `Assert.Contains`
on the spoken text, which passes with arbitrary prose in front. And on
Daniel's machine the persona was seeded from the old C# constant before
the refactor, so `assistant/persona` in the state store is still clean —
the boot log even says `Identity read from … instructions/identity.txt
seeds a new persona only`. **An existing brain is fine; every new one is
poisoned.** The other agent's PC and any fresh clone get the commentary.

The fix has to be structural, not editorial, or it grows back the next
time someone documents a file. Either a convention the parser enforces —
commentary is `#`-prefixed lines and gets stripped — or a `## text`
section holding only what is spoken, with the prose outside it. Then
assert equality in the tests, not `Contains`.

### 2. Reflection is silently dead on any comma-decimal locale — live

`ReflectionAgent.ParseCandidates`:

```csharp
double.TryParse(scoreText, out var score)
```

No `CultureInfo`, so it uses the current culture. This machine is `nb-NO`:

```
nb-NO  '0.9' -> parsed=False
nb-NO  '0.7' -> parsed=False
nb-NO  '0,9' -> parsed=True
```

`reflection.txt` asks for `score|subtopic|idea` with score `0.0-1.0` and
shows `0.7|hypothesis|…`. The model complies, every line is dropped,
`candidates.Count == 0`, and Reflection returns after `PublishReflected`
having proposed nothing and written nothing to `assistant/reflection` —
ever. It looks exactly like a persona that never has an idea worth
keeping, which is a very plausible-looking failure.

This is also the whole of the "two pre-existing test failures". They are
not flaky and not environmental noise:

- `AtBatchSize_WithHighEagerness_PushesBestIdeaAndArchivesLosersInternally`
  — nothing pushed, because there were no candidates.
- `AtBatchSize_WithLowEagerness_WritesQuietlyAndDoesNotPush` — archive
  empty, same cause.

They would pass on an `en-US` machine, which is presumably where they were
written. Fix is `CultureInfo.InvariantCulture` — the model emits a `.`
whatever the operator's regional settings say. Worth a grep-wide rule:
this is the only culture-sensitive parse in `src/`, and
`ParquetArchiveStore` already gets it right with
`DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture)`.

### 3. `ParquetPassageStore` mutates the list it hands out — High

`WriteAsync` takes the lock, then:

```csharp
var rows = await ReadUnlockedAsync(cancellationToken);   // returns _cache itself
rows.RemoveAll(p => p.Id == replacedId);                 // mutates the cache
rows.AddRange(added);                                    // mutates the cache
…
_cache = rows;                                           // assigns what it already was
```

`ReadUnlockedAsync` returns `_cache` directly when warm, so the write path
edits the live cache in place. Two consequences:

- **A read can throw.** `LoadAsync`'s fast path returns `_cache` *without
  taking the lock*, and `SearchAsync` enumerates it. Hindsight and
  Librarian both call `SearchAsync` on every perception; Reflection calls
  `WriteAsync` from its own consumer loop. Different agents, different
  threads, no shared lock — `InvalidOperationException: Collection was
  modified`. The window is small and grows with corpus size.
- **The cache outlives a failed write.** Rows are added before
  `SerializeAsync` runs. If it throws, memory and disk disagree until
  restart, and the passages that were never persisted keep being searched.

The sibling store already does this correctly and is worth copying
verbatim: `ParquetArchiveStore.Merged` builds `new List<ArchiveRecord>(existing)`
and `AppendAsync` assigns the fresh list only after the file write
returns. Same shape, one allocation, no race.

### 4. `MaxSelectedPairs` is advisory, and duplicates are not filtered — High

`LibrarianAgent.ParsePairs` accepts every parseable in-range index the
model returns:

```csharp
if (int.TryParse(token, out var i) && i >= 0 && i < index.Count)
{
    selected.Add(index[i]);
}
```

No cap, no dedup. `{max}` is a sentence in a prompt, and `Merge` only
dedups `remembered` against `selected`, never `selected` against itself.
So:

- `"3, 3, 3"` opens `person~family.parquet` three times concurrently and
  hands Intent the same rows three times, weighted three times.
- `"0,1,2,…,40"` fans phase one out over 41 concurrent file reads.
  `MaxConcurrentRecalls` does **not** cover this — it caps the *chunks*
  in phase two, after every selected pair has already been read.

`.Distinct().Take(_options.MaxSelectedPairs)` closes both. The tell that
it is happening is `Librarian selected N pair(s)` with N above the
configured max — which the Debug line now prints, so it is at least
visible.

Previously deferred deliberately ("run with the flaw until something
breaks"); re-raised only because the duplicate half is a correctness
issue, not just a fan-out budget one.

### 5. `CachingEmbeddingProvider` throws above 8 distinct texts — Medium

Verified with a throwaway test: nine distinct strings in one call gives
`KeyNotFoundException: The given key 'x' was not present in the dictionary`.

`Store` evicts down to `Capacity` (8) as it fills, so by the time the
projection runs the earliest entries are gone:

```csharp
for (var i = 0; i < missing.Count; i++) { Store(missing[i], fresh[i]); }   // evicts as it goes
…
return [.. texts.Select(t => _cache[t].AsSpan().ToArray())];               // reads them back
```

Harmless today — every caller embeds one or two texts — so this is a
landmine, not a fire. It arms itself the moment anyone batches: a corpus
backfill, a re-embed after a model change, or wiring `RetrievalProbe`
through the DI-registered provider instead of constructing
`OnnxEmbeddingProvider` directly. Building the result from `fresh` plus
cache hits, rather than reading everything back out of the cache, removes
the coupling between capacity and correctness entirely.

Related, same type: the lock is held **across** `inner.EmbedAsync`. On
ONNX that costs nothing (the session serialises anyway), but on
`OpenAiCompatibleEmbeddingProvider` it converts concurrent HTTP calls into
serial ones. A per-key in-flight `Task` map would both deduplicate and
keep distinct texts parallel.

### 6. The state store keeps lines it cannot read, then throws on them — Medium

`JsonlAgentStateStore.TrimAsync` is deliberate and well-argued:

```csharp
catch (JsonException) { keep[i] = true; continue; }   // "no place to decide something should stop existing"
```

`LookupAsync` scanning the same file is not:

```csharp
var record = JsonSerializer.Deserialize<AgentStateRecord>(lines[i])!;
```

Unguarded, so a line the trimmer preserved on principle takes down the
read that meets it — `IdentityAgent.GetIdentityAsync` and Reflection's
drive history both go through here. The `!` is also load-bearing on a
literal `null` line, which deserialises to null and then dereferences.
The two halves should agree: skip what cannot be parsed, keep it on disk.

### 7. Governance's bundle map only shrinks on the happy path — Medium

`_bundles.TryRemove` is reached at the end of `OnVerdictAsync`. Any turn
that completes on timeout and never receives a verdict leaves its
`BundleState` — and its `CancellationTokenSource` — in the dictionary
forever. Nothing sweeps it.

`TimeoutCts` is never disposed on *any* path, including the normal one.
For a process designed to run for years beside one person, an unbounded
`ConcurrentDictionary` of undisposed CTSs is the wrong shape. A `using`
on the CTS plus removal in a `finally` covers both.

Minor, same file: `OnVerdictAsync` does `GetOrAdd`, so a stray second
verdict for an already-removed correlation creates a state with
`Perception == null`, and the Yellow branch dereferences `state.Perception!`.
Caught by `AgentBase` and logged, but it is an avoidable
`NullReferenceException`.

### 8. Parquet writes are not crash-atomic — Medium

`ParquetArchiveStore.WriteRecordsAsync` serialises straight over the
destination, and `ParquetPassageStore.WriteAsync` does `File.Create(_path)`
first. A crash, a full disk or a killed process mid-write truncates a pair
file — or the entire passage corpus, which is one file.

`JsonlAgentStateStore.TrimAsync` already solves this in the same codebase
and says why:

> Through a temp file: a crash midway leaves the original intact, where an
> in-place rewrite would leave the persona's state half written and
> unparseable.

That reasoning applies with more force to an archive whose stated design
goal is to outlive the software. Write to `.tmp`, `File.Move(overwrite: true)`.

### 9. Smaller things

- **Passage timestamps parse under the current culture.**
  `ParquetPassageStore.FromRow` uses `DateTimeOffset.TryParse(r.Timestamp, …)`
  and falls back to `MinValue` silently. ISO-8601 survives most cultures,
  so this is theoretical — but the fallback means a failure shows up as
  every note being two millennia old in Hindsight's `Age()` and
  `LatestAsync` picking the wrong row, rather than as an error.
  `ParquetArchiveStore` uses `CultureInfo.InvariantCulture` on both sides;
  match it.
- **`InstructionFile.Fill` substitutes into substituted text.** Replacements
  run in sequence over the accumulating string, so a value containing a
  later placeholder gets expanded. Recall fills `{rows}` — archive values,
  written by a model — before `{text}`. A fact whose value is the literal
  `{text}` injects the turn. Harmless in practice, trivially avoided by
  building the output in one pass.
- **`reflection.txt` still ships a worked example.** `9fbd7aa` removed
  Archivist's because a real substrate copied it and filed it as a fact
  every turn. `0.7|hypothesis|trip dates vs deadline` is the same shape of
  bait, one prompt over. The echo guard only catches a *whole-prompt* echo.
- **Dead doc comment.** `Program.cs` lines 29–36 still describe the
  `MorrowIdentity` constant that `a72e6b4` deleted, now floating above
  `var builder = …`.
- **`ChannelBus.Publish` counts before it writes.** `_activity.OnEnqueue()`
  runs ahead of `TryWrite`, so a dropped write would leak a count from
  `BusActivityTracker`. Unreachable while the channels are unbounded.
- **Intent cannot tell "recall found nothing" from "recall never ran".**
  `AppendRecalledFacts` and `AppendNotes` both return early when empty, so
  the prompt is byte-identical either way, and the model fills the silence.
  See idea 6 — this is the mechanism behind *"I know your name, but you
  haven't told me yours yet."*

### Test suite

167 of 169 pass. Both failures are finding 2, not flakiness — fix the
culture and they go green. `ChannelBusTests.Publish_WithSlowSubscriber_ReturnsImmediately`
asserts under 5 ms and can still lose to a cold-start JIT on the first run
of a session; it passed on this pass.

The `ShippedInstructions` decision — tests load the real files, not
doubles — is the right call and already earned itself: it is why finding 1
is one assertion away from being caught rather than invisible.

---

## Ideas

Two batches. Latency is engineering with known answers; the second batch
is design, and deliberately constrained by the rule `architecture.md`
already sets: *surface interiority only where something actually happened
to cause it.* Ideas that merely make the persona talk about itself more
are excluded — that is the failure mode, not the goal.

Anything already in `roadmap.md` (two-layer vector retrieval, aliases,
escalate-on-low-confidence, the episode corpus, async deep recall, the
capsule) is left out.

### Latency

The floor today is three serial substrate calls before a word reaches the
person: **Librarian → Recall → Intent**, roughly 700 ms + 500 ms + 900 ms.
Impulse, Identity and Hindsight run beside them and cost nothing. So
everything below is about the three.

**1. Stream Intent's tokens.** The single biggest win available, and it
changes perceived latency rather than actual — time to first token is a
few hundred milliseconds against a second or more for the full body.
`OpenAiCompatibleSubstrateProvider` posts non-streaming and waits.

The obstacle is real and worth stating plainly: Security gates the reply,
and *Red must never reach Action*. Three ways through, in increasing
order of nerve:

- Stream into the SSE surface marked provisional, and retract on Red. The
  person may see a few words vanish. Honest, but it breaks the invariant.
- Run `SecurityRuleSet` incrementally over the accumulating text. The
  rules are deterministic and cheap, so this is affordable; the question
  is whether a rule that matches on a whole sentence can be evaluated on a
  prefix without false negatives.
- Stream only after the first sentence has cleared the rules. Gives up the
  first ~200 ms and keeps the invariant intact.

The third is the one to build first — it is strictly better than today
and decides nothing permanently.

**2. Prefetch Recall's file reads from Hindsight's leads.** Librarian's
LLM call and its cosine sweep both produce pairs, but Recall waits for
the whole envelope. The passage sweep is local, finishes in
microseconds, and its leads are usually a subset of the final selection.
Reading those pair files while the selection call is still in flight
costs disk that is free and idle, and warms `ParquetArchiveStore`'s cache
so phase one is a dictionary hit. Zero substrate cost, no new bus
message, and it fails safe — a wrong prefetch is a wasted read.

**3. Put the standing text first in every instruction file.** Both
`librarian.txt` and `recall.txt` open with volatile data:

```
Known knowledge-base topics (index: category/topic):
{options}
…rules…
Turn: {text}
```

Mistral and OpenAI both cache on prompt *prefixes*. Leading with
`{options}` means the cacheable prefix is empty, and every archive change
invalidates everything after it. Reordering to **rules → index → turn**
makes rules-plus-index a stable prefix that only moves when a pair is
added. `intent.txt` already has this shape and should keep it. This is a
text edit with no code change and it cuts input cost as much as latency.

**4. Pre-warm the HTTP connections at boot.** Each named `HttpClient` pays
DNS, TCP and TLS on its first call, which lands on the first turn a person
types. A throwaway request per provider during startup moves that cost to
where nobody is waiting. Pair it with an explicit
`PooledConnectionLifetime` so the handler rotation is a decision rather
than a default.

**5. Reconsider Recall's skip, in the other direction.** `a0b43c9` removed
Librarian's fast path so the selector's judgment gets exercised. Recall
keeps its equivalent, on reasoning that holds. But the roadmap's
"below a size threshold, send everything" argument says the *interesting*
version is skipping harder, not less: with `MaxPickedPerWorker` at 6, an
archive of 40 rows still costs a full picking round. Raising the skip
threshold to "everything a prompt can comfortably hold" removes a serial
call from most turns for a long time, and degrades into today's behaviour
when it stops being true. It is a config change, measurable with
`RetrievalProbe`.

**6. Overlap Archivist with the reply.** Already off the critical path —
it subscribes to conclusion. Noted only so it does not get re-raised.

### Interiority that is actually grounded

**1. The persona has no sense of elapsed time.** Nothing anywhere knows
whether the last turn was ninety seconds or three weeks ago, and this is
the largest single gap between the system as built and a mind that feels
continuous. It is also nearly free: Perception knows `DateTimeOffset.UtcNow`
and the store knows the last conclusion's timestamp.

Concretely — stamp the gap on the perception envelope; let Impulse map it
to a drive nudge (fatigue decays across a long absence, social drive
rises); pass it to Intent the way `DriveTrend` is passed, as words rather
than a number: *"[Since: three weeks]"*. Then "it's been a while" is a
claim about something that measurably happened, which is exactly the bar
`DriveTrend` sets. Every current mechanism describes state; none describes
*time*, and time is most of what makes a relationship feel like one.

**2. Notice when a fact changes.** `ParquetArchiveStore.Merged` already
detects the collision — a new row at an existing `subtopic/subject/key`
replaces the old one, deliberately and silently:

```csharp
if (positions.TryGetValue(key, out var at)) { merged[at] = record; }
```

That is the persona changing its mind about the world, and it is thrown
away. Carrying the superseded value forward — into the archive as a prior,
or as a one-line note to Reflection — buys *"you said Oslo before"*
without any new retrieval, any new call, or any invention. It is the
cheapest grounded interiority in the codebase, because the event is
already detected and merely not reported.

**3. Say "nothing on file" out loud.** See the last bullet of finding 9.
Recall logs `nothing on file` to the console but contributes nothing to
the prompt, so an empty archive and a Recall that never ran produce
identical inputs and the model bridges the gap by inventing. An explicit
`[Recall: nothing on file]` costs six tokens and converts a confabulation
into *"I don't think you've told me."* This is the same argument
`less-grounded` already won for degraded advisors, applied to the case
where the faculty worked and found nothing — which is more common and
currently invisible.

**4. Let the corpus grow while nobody is talking.** `ReflectionAgent`
already publishes self-triggered perceptions (`TriggeredByKey = "self"`,
generation-capped), and the roadmap parks the Watchdog's idle timer
pending a platform decision. But the valuable half needs no platform:
firing Reflection on silence so it *thinks* rather than *speaks*. Coming
back after a week to a persona that has had thoughts in the meantime is a
different thing from one that resumes mid-sentence, and the mechanism is a
timer plus the existing loop-back seam. Speaking unprompted is the part
that needs the platform decision; thinking unprompted does not.

**5. Let salience decay without deleting anything.** The archive's
"nothing is ever deleted" rule is right and should not move. But
`Importance` is fixed at write time and never revisited, so a fact that
mattered once outranks a fact that matters now, forever — and Recall's
picking budget is spent on it. Decaying importance with age unless a row
is re-touched, purely as a *retrieval* weight, gives forgetting-shaped
behaviour with no data loss: the row stays on disk, readable by DuckDB in
forty years, and simply stops crowding the prompt. The capsule cares about
what is stored; the persona cares about what surfaces. They are allowed to
differ.

**6. Make the echo depth do something.** `Hindsight` computes
`EchoDepth` and threads it through Intent and Governance, and nothing
reads it. It was built to detect the persona resonating with its own past
thoughts rather than the person's present one — which is a real failure
mode for a system that feeds its own notes back in. Using it as a damper
(above some depth, weight notes down or decline to wake them) turns a
diagnostic into a corrective, and is the difference between a mind with a
memory and one talking to itself.

---

## What I would do first

1. Finding 1 — a fresh install currently speaks its own comments. One
   parser change and four files.
2. Finding 2 — one `CultureInfo.InvariantCulture`, and the suite goes
   green for the first time in a while.
3. Finding 3 — one `new List<>(…)`, copied from the store next door.

Then latency idea 3, because it is a text edit that costs nothing and
pays on every turn.
