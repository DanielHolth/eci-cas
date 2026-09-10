# ECI-CAS — Roadmap history

Archive companion to [`roadmap.md`](roadmap.md): design records for what
shipped, measured conclusions, and rejected approaches with why they were
rejected, so nobody re-tries them. Organized by the section of roadmap.md
each item came from before this split.

---

## Latency — done

**Pre-warm the HTTP connections at boot.** `SubstrateWarmup` sends one
throwaway completion per distinct provider+model before the REPL prompt
appears, so DNS, TCP, TLS and (locally) the weights coming off disk are paid
where nobody is waiting. Deduplicated by provider+model, not by substrate
class — the minimal tier points all eight classes at one 4B. Bounded by
`Substrates:WarmupMs`, 0 disables, cannot fail a boot. Paired with an
explicit `PooledConnectionLifetime`: the factory otherwise rotates handlers
every two minutes and throws the warmed connection away, so a persona idle
for three minutes paid the handshake again anyway.

## Reading the archive back — the pair-addressed store (superseded)

This entire line of work was written against the pair-addressed archive
(`subtopic/subject/key = value`, filed under category/topic). *The archive
inverted* (below) supersedes it; kept for the reasoning and the measured
numbers, since a lot of general lessons about retrieval came out of it.

**A sentence column, and a local embedding beside it — shipped as "Two
layers of cosine."** Recall and the Librarian both read rows rendered
telegraphically (`renewal / passport expiry = 2027-03`), which matches
almost nothing a question actually says. The fix: a second column carrying
the same fact as a plain sentence ("Daniel's passport expires in March
2027"), produced by the Archivist call already happening — no extra call at
write time. That sentence is also what makes an embedding worth having: a
vector over the telegraphic rendering embeds badly, one over the sentence
doesn't. Local, no API — a bge-small/all-MiniLM class model (30-130MB,
CPU-only, ms/row) runs in-process via ONNX with no server, cheaper than one
Archivist call, so Minimal tier needs it most.

Two things it does not fix: it can't recover a fact the Archivist never
extracted or mangled (writable ceiling 29/35 on v3), and it does nothing for
pair selection, where whole-category already reached 83% — it's a
within-file narrower.

Batch 12 reordered this ahead of vocabulary work, for two reasons. First,
the rows-column mechanism: every shelf result was explained by
facts-per-file (1.7 shipped shelf, 2.0 lean, 3.0 terse), and Recall chunks
by `RowsPerWorker`, so rows-a-turn is LLM-calls-a-turn — the within-category
narrowing is the only stage still doing its narrowing with a model, and it
doesn't scale (2000 model calls for a 100k-row file). Second, and sharper:
"a within-file narrower" turned out to be where the loss actually is. Same
shelf, same archive, only difference whether Recall filters: 78% with
`nopick` vs. 60% with the lenient bar. Recall discarding rows costs 18pp —
more than selection, more than any shelf choice measured. So the embedding
is not just a cost fix, it's a replacement for the stage that loses most,
by a mechanism (cosine) that can't repeat the mistake of a model judging
which rows are worth keeping.

**Shipped.** Row vectors stamped by an `EmbeddingArchiveStore` decorator so
every writer gets them; Recall narrows a pair only when fully covered, skips
picking when every pair narrowed (`PickAfterVector` false, `VectorCandidates`
per tier); Librarian unions cosine-matched pairs into its LLM selection
(`VectorPairs`, `VectorMinScore`) and publishes the query vector for Recall
to reuse. `ArchiveTool embed` backfills an existing archive. Unmeasured at
ship time: fatness — the bench corpus (24 statements, 35 rows) is three
orders of magnitude too small to see what a fat file does.

Five constraints on building it, each from a failure already logged:

- **Never sweep a partial file** — a pair narrows by cosine only if every
  row carries a vector from the current `ModelId`; otherwise the whole pair
  falls back to the whole-file path for that turn. A mix of embedded/
  unembedded rows in one file otherwise means a cosine sweep that silently
  skips half of it and returns a plausible answer either way — the failure
  shape of `unfiled/unfiled` (batch 9) and the silently dropped arm name
  (batch 12). An earlier draft wanted a non-nullable vector with a startup
  refusal-to-boot check; wrong twice over — it conflicts with
  `IEmbeddingProvider`'s posture that unavailability is normal (the model
  file may not be downloaded), and it confuses an unavailable embedder
  (runtime condition, whole-file path is the fallback) with a partially
  embedded pair (the real hazard, enforced at read per pair). Old archives
  need no migration — uncovered until rewritten, healed on next write;
  `ArchiveTool` gets an explicit backfill for the impatient.
- **The vector stays nullable through to `ArchiveRecord`** — the one field
  with no friendly default. `ParquetArchiveStore.cs:432` writes `Sentence`
  from a non-nullable field defaulting to `""`, so a null the row class
  preserves collapses to `""` on first rewrite. Harmless for the sentence
  (both mean "no sentence"); fatal for a vector, since "no vector" and
  "empty vector" aren't the same thing and the coverage rule keys off that
  distinction exactly.
- **The hash of the embedded text sits beside the vector**, mismatch counts
  as missing. `Merged` replaces a row outright at the same address ("lives
  in Oslo" → "lives in Bergen" keeps the address, swaps the value); a
  vector inherited across that swap would point at Oslo forever and score
  confidently — worse than partial coverage, which is at least detectable.
  Deriving the check from the text makes a restated fact self-invalidate
  regardless of whether the writer remembered to clear it.
- **The pair keeps its job.** Inference-over-facts questions ("am I old
  enough to rent") aren't reachable by an embedding, so the symbolic cut
  isn't replaced by the arithmetic one. Also the argument against growing
  the shelf past 170 — once the vector narrows, more hand-written pairs buy
  nothing and each is a curation decision on a schema rows now point at.
- **Fatness has never been measured** — the bench corpus is too small; a
  larger corpus (files with 20+ rows) is a prerequisite, not a follow-up.

**Write side (Archivist/Cataloger), measured against a key of defensible
pairs.** 82% of rows filed reasonably over four interleaved reps (spread
67/100/92/69 on the *same* twelve statements — almost all extraction
variance, not filing; "My brother Lars lives in Tromso" → `user living
situation = travels with brother` loses the fact before any drawer is
chosen). Two rules fixed on the strength of it:

- `archivist.txt`: **key is a property of subject** (a car's colour has
  `subject=car`, not `subject=user`), and a fact about another person files
  **under that person's name, not the relation**. Value sufficiency 81% →
  91%; fabrication on eight null messages fell 0.4 rows/rep → 0.0 (usually
  extraction-completeness rules make fabrication worse, not this one).
  Tested and *not* shipped: "the key names what was stated, not what it
  means" — wins 3 reps, loses 1, ties 1 (noise floor).
- `cataloger.txt`: a tie rule ("a person goes in relations, a fact about the
  job goes in work") was already present and already losing — "My manager
  is called Petter Aas" went to `work` nearly every rep, to a different
  folder each time. Naming the actual tie case (a person known *only*
  through the job) took drawer accuracy 88% → 96%. Lesson: a tie rule that
  states the distinction but not the case it applies to is a comment.

**Retrieval re-measured, worse than the original 58%** (which was
selection-alone with padding pairs empty). On
`tools/retrieval-bench/read_bench.py` (35 questions, all 170 pairs
populated): category 79%, select 36%, pick 76%, answer 25% — stable across
3 reps.

The loss is one stage, one level: `pick` at 76% says Recall keeps the row
once the file is open. Of 19 selection misses, **14 opened the right
category, wrong folder** (licence at `admin/renewal` vs. reader opening
`admin/licence`; cabin at `household/address` vs. `household/property`;
degree at `learning/university` vs. `learning/course`) — counting
category-level hits, the reader is right 79% of the time. So "the right
drawer is not guessable" (a reading recorded for the hierarchical dead end
below) doesn't survive: the drawer *is* guessable; the folder inside it
isn't, because 15-17 one-word topics per category contain 2-3 meaning the
same thing to a 4B.

**Fix measured: synonyms beside topic names.** A few words per topic in the
*question's* vocabulary (`admin/renewal (expires, expiry, renew, runs out,
valid until)`), shown only to the selector so filing stays byte-identical.
3 reps: select 40%→51%, answer 32%→43%, better in 3/3 both. Category flat at
80% — the gloss doesn't help find the drawer, which is right, since the
drawer was never the problem. Four of the five real category misses (of
five) were write-side, not read-side ("I gave up smoking" filed to
`leisure/hobby`, the 07:20 bus to `travel/trip`, the spare key to
`household/address`).

**Dead ends** — all measured on the 4B, all worse than the flat list they
were meant to beat:

- **Hierarchical read** (category, then topic inside it) — the same
  two-step that won +17pp on write loses 35pp on read (58/67/58 flat vs.
  25/17/25). The only read result clearing its noise floor. Reason
  originally recorded ("the drawer isn't guessable") is now known wrong —
  drawer is guessable 79% of the time. Why the two-step loses anyway is
  open — plausibly committing to one category discards the second-best
  drawer.
- **Wider fan-out** — opening 2, 3, or 5 files scored identically; misses
  are wrong picks, not too few.
- **Two archives merged / redundant filing** — +0%; 1.83 addresses per
  statement mostly buys stale ghosts on the next update.
- **Handing the vocabulary to the bundled agent as `{known}`** — 0 for 4,
  -15%. A model given a field it wasn't asked for fills it anyway.
- **A second pass over `other`** — re-asking with `other` removed drops
  `other` from 1.0 to 0.2 rows/rep but buys nothing (82% filed either way,
  better in 1/4 reps); re-asking the *category* instead scored 80% —
  rescued rows land in a plausible sibling, not the right one. `x/other`
  was never the disaster it looks like — the read path opens `other`
  alongside its parent category, so `relations/other` is *more* retrievable
  than a fact forced into `relations/neighbour`. Not shipped; left in
  `tools/retrieval-bench/review_other.py`.
- With only 12 statements against a 10pp noise floor, "identical" and "+0%"
  results above are *undetectable*, not disproven — only the 35pp
  hierarchical loss clears the floor.

**Corpora.** `tests/corpora/retrieval_v3.py` (24 statements, 35 direct + 8
oblique questions) replaced the burned `retrieval_v2.py` (12 tune / 12
holdout, scored repeatedly until the holdout no longer held out). Oblique
tier (questions that don't name the fact needed) scored separately, 2/8,
never folded into the headline.

## One instance per person (symbiosis) — settled design

**The intended shape is one Morrow-ECI per person**, not one shared persona
tracking who it's talking to. The relationship is symbiotic — a persona
developing against a single person over a long time, which only works if
drive state, self-derived ideas, and personal archive all belong to that
one relationship. A family of four is four instances, not one with four
hats.

This is why **speaker identification is cut** — it only ever answered
"which user is this" on a device with one shared persona, a question that
doesn't arise when the instance already belongs to someone.

**Accessibility is a primary driver.** A companion that knows one person
deeply is most valuable to someone who needs it, and that value comes from
depth against one person, not breadth across several.

Multi-user profiles keep their place as the **shared-device path**, not the
primary design — nothing shipped in iteration 1 is invalidated; profile-
scoped storage is the accommodation, a dedicated instance gets the whole
archive by construction.

## The archive inverted — perception is the record (shipped, flagged)

Everything below assumed the pair-addressed store: a fact extracted into
`subtopic/subject/key = value`, filed, read back by selecting a pair.
Batches 12-16 took that apart piece by piece; what's left standing is
smaller and differently shaped.

**The inversion.** There is no data model the write side has to satisfy —
just a log of what was perceived, and an Intent that makes sense of it on
the way past. The archive stops asserting truth and starts holding
evidence.

**Status: shipped, behind `Utterances:Enabled` (default false).**
`ParquetUtteranceLog` holds the log as one parquet per month; `ThreadWeaver`
threads at write time against one frozen earliest representative per
thread; `UtteranceConsult` reads it with the two-pass split;
`UtteranceBackfill` rebuilds vectors and threads at boot. With the flag on,
`InvertManifest` drops Librarian, Archivist and Cataloger from both
manifests and puts Recall and Scribe on Perception — flag is one boolean,
roster follows in code, no second topology to drift. Measured settings
(benches 22-25): threshold 0.86, top-5 consolidator candidates, MMR lambda
0.7, lexical weight 0.15, ReadMinScore 0.55. Consolidator is off by default.

### Ground truth, and everything else

**Ground truth** is the original utterance, timestamp, speaker, keywords —
appended, never rewritten, readable in 2126 with a parquet reader and no
model, vocabulary, or code of ours.

**Everything else is derived and disposable**: vectors, score columns, any
partition, index, shelf — all rebuildable from ground truth by a background
job (`ArchiveBackfill` already grants vectors this status).

That line is what makes a dynamic shelf safe: an open vocabulary was
rejected before because the shelf *was* the index (folders invented by a
4B become a schema a stronger model must live with, no migration path).
Once retrieval doesn't route through it, a bad shelf in 2030 is a
recomputation, not a legacy.

**Store the message, not the parse.** Batch 15 measured the retrieval
ceiling by what's kept: 91% for the address line, 97% adding the sentence,
100% adding the message. `pet/dog/name = rex` is a lossy compression of
something already in hand — also destructive for Characterise, since word
frequencies can't be counted over parsed key-values.

### Keywords are the lexical half, not a lightweight drawer

Embeddings are weak exactly where a query is a token: names, dates,
numbers, rare words. Keywords cover that, fused with cosine rather than
replacing it. Extraction is deterministic — tokenise, drop stopwords, keep
what's rare in the corpus, with capitalisation/digits as a complement — no
model, so it can't reintroduce a write-side call.

Ordering measured (batch 22): casing+digits alone keep a third of what v4's
answers need (*penicillin*, *seasick*, *choir* are neither capitalised nor
numeric, and are most of what an archive is about); rarity carries the
extractor, casing recovers names/compounds stated often enough to stop
being rare. Together they lose nothing against keeping every non-stopword.
Threshold tuning is a v5 question — at 78 statements almost everything is
rare.

### Two ways to consult a memory

**Find.** Hybrid vector + lexical, top-k. This is what batches 12-16
measured; flat cosine over the whole archive was the best arm in the log
at 78%.

**Characterise.** An aggregate over a filtered subset ("what was my uncle
interested in", "what do I always eat", "what changed after the divorce").
Top-k can't answer these. A scan with counts over keywords, speakers,
timestamps — SQL, not cosine, no model on the read side either. Exists
only because raw text was kept. (Full specification and pre-registered
bench: see roadmap.md, still open/unmeasured.)

Three query shapes decided during specification, not one: *Trait* (filter
by participant/subject, count terms, rank by distinctiveness); *Habit*
(filter, count threads and members, rank by recurrence — threading already
computes this); *Change* (split on time axis, diff two term
distributions — superseded rows are in scope here). The filter itself is
not free of a model: resolving "after the divorce" to a timestamp is *Find*
then aggregate, so deciding a question is a Characterise at all, and what
its filter is, is a call added to Intent's existing prompt rather than a
new hop. **Contrast, not frequency** — log-odds/tf-idf against the archive
as a whole, not raw term counts (raw count returns the corpus's own
background: *think*, *really*, *today*). What reaches Intent is always an
aggregate (ranked terms, thread reps with date ranges/recurrence, a bounded
number of exemplars), never the subset — keeps the prompt constant in
archive size. Degrades to a *Find*-style row-dump under some floor (an
aggregate over nine rows is noise wearing a number); "what am I like" has
no honest answer and should say so; an unresolvable anchor is a question
back to the person.

### Time shards, not importance tiers

Shard by **time** (one file per year/month) — bounded size, appends touch
only the newest shard, old shards immutable, incremental encrypted backup
is uploading each year once. Nothing routes on a shard — with the
Librarian gone there's no file selection anywhere in the read path; a
query sweeps every shard's vectors.

**Rejected: tiering by importance** (hot/cold/archive). A row untouched for
180 days is as likely to be a grandmother's last recipe as a restaurant
visited once, and a product promising decades must never make a memory
harder to reach for having been quiet. Recency/frequency survive as
**score columns** (recoverable) rather than locations (a wall).

Frequency is a rate (`hit_count / (turns_now - turns_at_first_seen)`),
computed at read time from a single global turn counter kept outside the
shards — one integer written per turn, not a column rewritten across every
untouched row. User-set pins ("I care about skiing") sit beside the rate as
an explicit boost outranking any metric.

### What this deletes

The 170-pair shelf as a schema (survives as a seed for clustering and a
browsing view), the closed vocabulary as a routing mechanism, Cataloger's
two calls per fact, Recall's chunk-and-pick, and the Librarian entirely —
all properly benched, all beaten by the flat baseline in the same results
file.

The Archivist shrinks rather than vanishing — extraction/splitting go, but
the judgment (is this worth keeping, does it supersede something) stays,
since an append-only log never overwrites and cosine will hand Intent a
dead dog five years on. Timestamps recover most of it: "dog named Buddy
(2019)" beside "new dog, Rex, after Buddy died (2024)" resolves the
contradiction the way a person does — only works if retrieval hands both
rows (see threading, below).

### Threads — accumulation is the inversion's own failure mode

The pair store deduplicated for free (a restatement landed on the same
address). A log has no addresses, so forty repetitions of a fact become
forty rows competing for the same five slots. Two problems, one costume:
**redundancy** (same value restated — a top-k problem, repetition is
itself signal worth keeping) vs. **supersession** (a different value at the
same subject — the log is right to hold both, but retrieval must not hand
Intent one row and call it the present).

**A thread id, minted on write.** Embed the row (already happening),
sweep, inherit the nearest thread representative's id if it clears
threshold, else mint a new one — deterministic, no model, off the critical
path. Doing this at read time would be O(k²) cosine every turn and
structurally blind (a top-k collapse can't know about a 2016 Subaru it
didn't return); doing it on write scans one representative per thread, and
that set grows with distinct subjects, not utterances.

What it buys: repetition costs one slot, not forty (retrieval groups by
thread, "said 40 times, Mar 2025-now" reaches Intent as one row carrying
its own frequency); "what car do I drive" is the newest row in a thread
(recovers the one thing the pair store was genuinely good at — addressing
current state); disagreement inside a thread is one operation (newest plus
oldest-that-differs, with dates).

**Superseded rows do not compete, but are one hop away.** *Find* means
*now* and drops anything carrying `superseded_by`; *Characterise* is
history by definition and doesn't. Dereferencing from the live row keeps
"you said Oslo before" reachable and is also the answer to a query that
asks *for* the dead row by name.

**Compare against the thread's representative, not any member** — chaining
(A~B, B~C, A≁C) walks a thread across subjects over twenty years, and
bounded drift matters more here than anywhere. **Bias the threshold high
and bench it** — a false split just restores today's duplicate behaviour
(recoverable); a false merge glues two subjects together and makes *now*
wrong (not recoverable at read time). Start near 0.9 rather than 0.85.

### The consolidator — judgment where cosine has none

Cosine says two rows are about the same thing; it can't say which of "my
new car is a Tesla" and "my *wife's* new car is a Volvo" supersedes the
other (near-identical sentences, the difference is what cosine throws
away).

**Gated by the sweep, not run per fact.** Most new rows have no candidate
above threshold — nothing to disambiguate, no call. When the sweep does
return candidates, the top five go to one call per turn (not per fact).

**Cardinality is the wrong gate; disagreement is the right one.** Skipping
the call on a single candidate optimises away exactly the case the
consolidator exists for. The keyword extraction already detects
disagreement without a model: cosine clears and keywords match → threaded
deterministically, no call (this is where the volume sits, and the
saving); cosine clears and keywords differ on a rare token (Tesla/Subaru,
Oslo/Bergen) → the call is made regardless of candidate count. A false
merge costs in proportion to how much the two values disagree — spend the
call where the disagreement is.

**It links, it never replaces.** The verdict writes a thread id and a
`superseded_by`; the utterance itself is untouched. A consolidator with
permission to overwrite the log would be the one feature able to falsify
the hundred-year claim.

**The tiering falls out rather than being designed.** Threading is
deterministic (every tier); the consolidator is a substrate call (a paid
tier buys it). Both write only derived columns, so an upgrade is a
backfill, not a migration — the same job shape `ArchiveBackfill` already
uses for vectors.

## Renaming Morrow / persona bugs — context

(See roadmap.md for the still-open write-path fix; kept here only for
cross-reference — the gap predates the 512-pair shelf and neither caused
nor could be fixed by it.)

## Memory architecture — shipped and concluded pieces

### Two-layer vector retrieval

Two vectors at two granularities: a **pair layer** (one vector per
`category/topic`, loaded at boot from JSON, replacing Librarian's substrate
call with an in-memory cosine sweep) and a **row layer** (one vector per
`ArchiveRecord`, written by Archivist).

The row vector covers `subtopic/subject/key`, excludes `category/topic`
(pair layer already encodes it) and the value (a query never contains it —
matching "what's my name?" against a vector encoding `...name = Daniel`
pulls the row toward the token *Daniel*, contributing nothing). Rule
underneath: **embed what the query will look like, not what the data looks
like.**

### Aliases

The embedded text and the stored path differ — `assistant/identity` stays
that on disk; what's *embedded* is a retrieval-facing gloss written as
questions it should answer. Fixes the question-vs-label asymmetry on the
document side (cheaper than the query side), and is why unconditionally
including `assistant/*` was rejected — it makes the persona faintly
self-absorbed every turn, since facts in the prompt get used. Few, read
once at boot from plain JSON, derived and one-way — never a second name for
the pair, never written into a path, never shown to Intent.

### The assistant is a scope, and hindsight is not a lookup — done

The retrieval vocabulary widened to ~512 pairs (batch 18), covering the
**user's domain only** — no drawer for the ECI's own reflections, and
adding one was measured, not assumed. **Routing a reflection question by
similarity does not work**: given assistant drawers summarised the best way
found, `refl_v4` put 1 of 16 addressed-to-the-assistant turns in an
assistant drawer while 6/6 human controls stayed correctly out. A
bi-encoder has nowhere to encode *whose* fact this is — ownership is one
weak pronoun, content is what other drawers compete on and win. Restricting
to assistant drawers only (no human competition) still routed only 5/9 on
topic — `reflection`/`opinion`/`memory` blur because those are distinctions
of stance, not in the sentence.

So `assistant` stays **a scope decided before ranking**, like
`profiles/{id}`, not a category ranked against `household`. A bare
second-person gate gets 21/22 hand-written turns right (its one miss has
its subject in the previous turn — conversation state, not retrieval).

**512 is a lookup; Hindsight is generative** — no row answers "do you have
any thoughts about that?"; sending it through similarity search returns a
household fact with confidence instead of thinking. Reflection and
Hindsight stay untouched by the vocabulary work.

**Done.** `EciCas.Core.AssistantScope` declares the scope and its three
coarse drawers (what the persona is, has thought, runs on); the four
literals now name it (was previously an undeclared eleventh category,
known to `ParquetArchiveStore`, written by two agents, absent from
`cataloger.txt`, spelled as a bare string in four files). One prediction
corrected rather than dropped: it said a declared scope would delete
"defensive workarounds" in `LibrarianAgent`/`RecallAgent` — there are
none; both sites carry the turn's own text into picking for their own
reasons and stay.

### The summary is the retrieval, not the taxonomy

Batch 19: same 512-pair shelf, changing only how each pair was summarised
for the embedder: 10/20 probes on bare paths, 18/20 on one-sentence
glosses (`leisure` alone: 1/11 → 10/11). Consequence: any retrieval number
measured against a shelf summarised by bare paths is a floor, not a
result. `docs/vocabulary/v512-gloss.txt` holds the 512 sentences,
`tools/retrieval-bench/build_gloss.py` validates them. Lessons: aspect-
shaped topics work only when the aspect words are words people actually
say (`culture`/`office` land, `session`/`kit` don't); a gloss carrying a
common temporal phrase becomes a magnet regardless of subject ("last
autumn" pulled unrelated rows) — concrete nouns, no calendar words.
Glossing made write distribution slightly less flat (a good gloss is a
stronger magnet), which is fine — flatness was always a proxy.

### Union, not replacement — superseded

Selected pairs were the union of `vector top-K` and `LLM selection`,
because inference-chain questions ("Am I old enough to rent a car?" →
`person/profile/birthdate`) aren't reachable by cosine. Superseded by the
batch-15 finding (below) that the LLM selection call earns nothing
measurable and should be deleted outright.

### The episode corpus — superseded by the inversion

A proposed second store: semantic memory (the archive — curated,
structured) vs. episodic (the episode corpus — everything else Archivist
discards: circumstance, moods, plans, themes). An episode would carry a
short vectorized **summary** plus a ~150-token **exchange**, reusing the
Parquet store under `episode/<year-month>/...`. Never built — the archive
inversion's utterance log (store the message, not the parse) captures the
same motivation (keeping raw text) more directly, without a second store.

### Nothing is ever deleted — settled principle

Decay was proposed and withdrawn. An exchange is ~600 bytes — a hundred
turns/day is 22MB/year, sixty years under 1.5GB. Storage was never the
constraint; the only thing that strains is brute-force cosine over millions
of vectors, a distant, well-understood problem. Corpora partition by year
so no index is ever large and reindexing on an embedding-model change
touches one year at a time. **Digests index upward, they never carry
forward** — a distillation of 2026 does not move into 2027 (that's decay
wearing a new hat); Reflection reads digests to find which month to open,
then pulls real episodes, and **a digest may summarise but must cite** —
every digest row carries the addresses it came from.

### The recency lane — reasoning (see roadmap.md for open deletion question)

Batch 20: v512 wins on a centroid of what landed in a drawer (+11.5pp at
the cheap end) but loses on the written gloss, because a 480-drawer shelf
takes a long time to reach the ten rows a derived gloss needs — that gap
*is* the young archive, and a young archive's cache is the entire archive
(one flat scan, no cold start). Cost: 10,000 rows at 384 dims is 15MB,
milliseconds to scan; dual write is one append. A second, fatter lane (a
100k-row `fatMemory.parquet`) was considered and dropped — the pair files
already are the long-term store, so it would be a third copy and two flat
cosine scans at two sizes is one mechanism twice. Bounded by a year rather
than a row count on purpose — a row cap only approximates "lately" at a
fixed conversation rate. Trimming happens at boot, not on write, so a
long-running session never pays for it.

## The capsule — settled principles

The archive is meant to outlive the software. **Text is the artifact;
everything else is a rebuildable index** — Parquet is open and columnar,
DuckDB/pandas will read it in forty years without a line of this C#.
Vectors will strand on a dead embedding model, and that's fine because
they're derived. What a backup cannot add later is **legibility** — a
plain-text README belongs *in the archive directory itself* (column
meanings, path convention), costs nothing now, can't be retrofitted onto
media already written. Physical durability is deliberately not solved
here. (Open: inheritance — see roadmap.md.)

## Still open on the surface — shipped item

**Live tier switching — shipped.** `TierCatalog` binds every tier file at
boot; the Debug panel's dropdown swaps classes, agent assignments and
Recall/Librarian sizing on a running host. An unset `--Tier` now layers
Mock rather than leaving `appsettings.json` showing as a nameless sixth
state. Comparing Minimal against Default no longer costs two restarts and
the conversation. Does not close the asymmetry: a tier is still validated
for shape, never for whether its providers answer.

## Does the vocabulary earn its two calls? — answered (batch 15/16)

Original question: Cataloger on write and Librarian on read each cost one
model call to produce a cut a cosine sweep might do better and for free.
Batches 3-12 had only ever compared shelves to each other, never to the
absence of one.

**Answered, batch 15: no.** Measured against absence on the v4 corpus (87
questions, 1559 rows, near-miss clusters, strict scoring):

*The read call loses outright.* Librarian selects 47%/answers 42%; ranking
the same files by a row centroid selects 74%/answers 72%; letting every row
vote reaches 87%. Given a correct pair, cosine ranks inside it as well as a
flat sweep ranks across the whole archive — narrowing never improved the
ranking, it only sometimes handed the wrong pile. Even embedding the file
name a 4B would otherwise rank 170 of is worth 3pp. Delete the call.

*The write calls tie.* Re-filing by nearest gloss (mean of ten sampled rows
already in a file) scores 77%/73% against Cataloger's 74%/72%, agreeing on
only 11% of rows — paired bootstrap: +1.1pp, 95% CI [-9.2, +12.6], a tie.
(The same bootstrap cleanly separates a one-row gloss at -12.6pp and a
three-row gloss at -11.5pp, so the instrument can see a real effect where
one exists.) Two calls per row bought nothing over arithmetic disagreeing
with them 9 times in 10, and a gloss costs calls per *file* once vs. two
calls per *row* forever.

So the vocabulary earns zero of its two calls, surviving only for three
non-retrieval reasons: `Merged()` addressing (survives either way — the
address is subtopic/subject/key, not the pair); a fallback when no embedder
is available; a person browsing their own archive. Likely landing: vectors
as primary index, pairs demoted to file layout and a degraded path — see
`tools/retrieval-bench/README.md`, v4. Two caveats carried forward: filing
by gloss concentrates gold into 31 pairs vs. Cataloger's 54 (a density
sweep shows both readers degrading in parallel, not the vector filer
degrading faster — downgrades but doesn't close the worry); 11% agreement
means an archive filed where a person wouldn't look.

**Batch 16 closes the cold start.** The batch-15 gloss needs ten rows
already in the file, which a day-one archive doesn't have (why gloss-1/3
lose 12pp). Both shelves already ship a *written* gloss per pair, never
embedded before this — filing by the written line reads 70% strict at
three files, matching the derived centroid's 70%. Shippable write-side
version: embed the lines already in `bench.CAT["gloss"]` once at build
time, file against them — no sampling, no bootstrap off a young archive, no
per-row calls.

Same run answered the terse-vs-shipped consolidation question again: terse
leads at equal file count (75% vs 70%) only because 3 files of 34 hands it
214 rows where 3 of 171 hands the shipped shelf 63; matched on rows
reached, shipped wins (-18.4pp for terse at the cheap end, CI [-32.2,
-4.6], P 0%). Terse's smallest openable unit is 80 rows — can't express a
cheap read at all. Written gloss also beats the derived one on the two
caveats: agreement 38% vs. 11% (archive lands nearer where a person would
look), spread 60 pairs vs. 31 (wider than Cataloger's own 54, so the
concentration worry inverts rather than shrinks).

The apparent coverage gap (unglossed pairs) was withdrawn on inspection —
all 10 unglossed shipped pairs and all 8 terse ones are `x/other`, which
matches nothing by definition, so a vector filer never files into it
anyway (consistent with `other` already being write-side only). No
blocker: the written gloss covers every pair a vector could be asked to
choose.

One measurement bug found underneath all of this, worth carrying: the
bench scorer had been matching answer keys against the address line only,
never the sentence field the embedder actually reads. Fixing it moved the
write-side ceiling from 77/87 to 85/87 — the write side was never as lossy
as batches 3-14 reported.

## Open against the inverted archive — answered arm

**Does retrieval show Intent the contradiction? Answered, and shipped.**
Batches 24 and 25 measured this directly. A flat top-5 returns 1.47
distinct facts out of five — worse than the predicted failure. Collapsing
by thread and taking each thread's *newest* row (rather than nearest)
reaches 4.13 distinct at 0.911 current; the two-read split (pass A
`superseded_by IS NULL`, pass B unrestricted) takes stale to 0.000, and MMR
at lambda 0.7 adds 0.14 distinct on top. Identity dedup *alone* makes the
answer worse than flat (oracle: 4.80 distinct but only 0.667 current) — the
one surprising result here. All of it is in `UtteranceConsult`. Caveat
carried forward: this was a synthetic corpus with `superseded_by` modelled
perfectly, so read A is a ceiling, not a forecast.

## Should the shelf be clustered rather than authored? — moot as asked

If nothing routes through the shelf, folder names could be labels on
discovered structure (cluster vectors, name clusters offline, ship the 170
written glosses as a cold-start seed). **Moot** — the inversion deleted the
shelf rather than reauthoring it; there's no folder for a cluster to name
and nothing routes through one. (The live question this leaves — naming
threads — is carried forward in roadmap.md.)

---

# Design records — shipped

Kept for the reasoning, not as outstanding work. Where one contradicts
[`architecture.md`](architecture.md), architecture.md wins.

**Minimal tier on a local model.** One Qwen3.5 4B under `llama-server`
behind all eight substrate classes, so the free tier thinks instead of
echoing. Two new class knobs (`MaxTokens`, `Thinking`) and one provider knob
(`MaxConcurrent`, a semaphore so the Recall fan-out queues where a
cancelled turn can abandon its place); no new provider class, since
`OpenAiCompatibleSubstrateProvider` already speaks what llama.cpp serves.
The old all-mock tier moved to `--Tier=Mock`. 4B is deliberately undersized
— fails wherever an instruction leans on the reader being clever, making it
a probe for weak instruction files rather than a compromise. Details and
tuning table in [`appendix.md`](appendix.md#qwen35-4b-on-the-minimal-tier).

**Knowledge-swarm retrieval.** Replaced deterministic retrieval (literal
≥5-letter word extraction, exact-string path matching, newest-N truncation)
with semantic selection at both stages: Librarian selects pairs from the
index, Recall fans out one picking call per chunk. Introduced the five-part
record schema (`category/topic/subtopic/subject/key=value`) plus
`Timestamp`, `Domain` (Archivist-external vs. Reflection-internal) and
`Importance`, the last used to pre-trim deterministically so a huge topic
is not merely truncated by recency. Both writers share the schema and are
shown existing pairs to stop topic-name drift. One deliberate cut:
**Recall does not split results into External/Internal arrays for
Intent** — merging into one importance-sorted list lets a genuinely
important self-derived insight outrank a trivial external fact instead of
being quarantined.

**Pair-addressed archive.** Librarian's prompt showed the entire triple
index, unbounded and ever-growing. Rejected: sharding into buckets with
parallel selector calls (bucketing is lossy — `system/identity` vs.
`person/identity` disambiguates only if both are visible to one call), and
a hierarchical second selector (a whole new selector kind to resolve
subtopic). Shipped instead: drop subtopic from the index and let Recall
read it off the rows — a lossless dimensionality reduction, since every
cross-category distinction stays visible in one call. Storage became one
file per pair, `{esc(cat)}~{esc(topic)}.parquet`, so **the file name is the
index**: `index.parquet` deleted, `rebuild-index` removed rather than
reimplemented, per-file locks instead of one global one. `~` over `|`
because `|` is illegal in a Windows filename. No per-pair row cap — a
scientist may discuss one subtopic at enormous length, and truncating them
is exactly the wrong failure; `RowsPerWorker` chunks instead, and the only
ceiling is `MaxConcurrentRecalls`, trimmed breadth-first. Every worker
starts at once in one flat `WhenAll`; a per-pair `WhenAll` inside a loop
would serialize pairs behind the slowest.

**Reflection redesign (drive-gated, batched).** The old agent fired on
every conclusion and unconditionally reposted an idea, doubling cost per
real message. Now: buffer a batch, rank candidates and surface at most the
best one, write the rest quietly as `Domain=Internal`. Whether the best is
pushed or written depends on drive state — which is why this depended on
the drive-vector work landing first.

**Passage corpus (the first vector layer).** Deliberately neither designed
layer: nothing in the archive is embedded. What is embedded is a 5-15 word
note Reflection writes about **what the last batch failed to retrieve** —
a code review of its own recall. That is "embed what the query will look
like" pushed one step further: a miss is already phrased in the shape of
the question that caused it, and the note carries pair pointers as row
metadata, so a cosine hit is a **lead**, not an answer. Keeps three
properties cheaply — union not replacement, no second copy of a fact
(pointers resolve against the live index), no new per-turn substrate call.
**The revisit** ships with it: a stored note is quoted into the next
Reflection prompt and may be rewritten in place. Which note gets quoted was
at first the newest, making the corpus a chain; it now picks the note
*nearest* the batch, making it a trail — something written months ago
becomes revisable the day the persona circles back.
`Reflection:RevisitMinScore` floors it, falling back to the newest rather
than nothing.

**Hindsight — what it is for.** Recall reads facts; Hindsight reads what
the persona made of them. A thought note is written for no one, about what
a batch of turns made the persona notice. Hindsight wakes one when a prompt
brushes against it, months later if that's when it fits, and hands it to
Intent as its own voice rather than as a fact — something the key:value
archive structurally can't produce: a direction the persona chose, a
flavour nobody wrote for it. Three constraints: **a hit is a lead** (the
cosine floor is deliberately low, 0.25 — notes restating the prompt tell
the persona what it knew, the sideways ones are the point); **prose and
facts stay separate substances** (two bundle slots, never re-ingested as a
fact); **the ring has no external grounding** — Hindsight → Intent →
Reflection → new note closes on itself, the pairs field being the only
part reality can contradict.

Built in `c69a34e` as its own agent rather than living inside Librarian, so
Intent weighs archive facts and the persona's own prose as two independent
bundle slots instead of one arriving as a passenger on the other's
envelope. Librarian kept the *pointer* half. Passages stay out of
Archivist's extraction scope by omission.

**The corpus had no model identity (closed).** `Cosine` returns 0.0 on a
width mismatch, so a dimension swap silently retired every note; a swap at
the same width was worse (old vectors still scoring, no longer meaning
anything). Closed by stamping a model id on every row (`onnx:<weights
path>` or `openai:<model>`) and refusing to boot on disagreement —
re-embedding would rewrite the corpus on a config change an operator may
have made by accident, exactly the change that should rewrite nothing. An
empty id skips the check; pre-stamp rows are excluded, not counted as
disagreeing.

**A misspelled provider disabled the corpus in silence (closed).** Docs
said `Embedding:Provider = "api"`; the switch matched `"onnx"`/`"openai"`
and silently defaulted to `NullEmbeddingProvider`, byte-identical to the
normal "weights aren't downloaded" state. `"api"` is now the documented
alias, `"none"` means none explicitly, anything else throws at startup.
General rule: **a corpus nothing can search must be either deliberate or
loud, never plausible.**

**The turn was embedded twice (fixed).** Librarian and Hindsight both embed
the same capped `perception.text`, and `OnnxEmbeddingProvider` held a
semaphore across inference, so the second call waited and recomputed a
bit-identical result. Fixed in the provider: `CachingEmbeddingProvider`
wraps whichever embedder config selects. The faster alternative — embed
once in Perception and forward the vector — was rejected: a float array
would be the largest thing riding the bus, to save an in-process
recomputation. Related and already fixed (`f34b5a8`): debug lines passing
a `string.Join` ran the join at every log level regardless of whether it
was enabled.

**Multi-user profiles, iteration 1.** Personal knowledge scoped by
*directory*, not filename or a new column: `archive/` shared,
`archive/profiles/{id}/` personal, same naming convention inside each, so
"the file name is the index" holds in both. Profile is a **parameter** on
`IArchiveStore`, not a scoped view or store factory — `null` is exactly the
pre-profile behaviour. Reads union both tiers with the profile winning;
writes land in the profile directory unless the category is on
`Archive:SharedCategories` (`["assistant"]` — the persona's identity
belongs to nobody on a shared device). Surface side: `GET/POST
/api/profiles`, a full-screen picker persisting to `localStorage`,
`profileId` on every perceive and stream subscription, avatars as an
identity ring around the Impulse-coloured circle. Two things surfaced:
switching profiles is a **remount**, not a state reset (`Conversation`
keyed by profile id); `/api/stream` writes an SSE comment immediately on
connect, since browsers hold `onopen` until the first body byte.

**Expression is chosen on the backend.** Impulse appraises it from its own
drive vectors and publishes it on the advisory; Governance captures it
(the verdict envelope never carried advisories) and forwards it on every
Action/Conclusion. The block path re-reads the face *after* the frustration
nudge, the whole point of nudging. Tuning: instant nudges ported verbatim
from the prototype were an order of magnitude too small for
`DriveVectors`' bucket edges (a critical event moved alertness to 0.105
against a 0.35 low edge, six faces unreachable in practice) — resized
against the edges. One departure from the Python bucket order: raised
alertness outranks warmth, since both can be high at once and a smiling
face through an emergency reads wrong.

**The turn was opaque.** A person saw an avatar and a reply, nothing about
which faculties ran, what was read/written, what it cost. Fix is a
projection, not a renderer: `TurnProjection` folds a turn's envelopes into
one `TurnRecord`, `TurnLog` serves it via `/api/log`, `/api/log/stream`,
and any `ITurnLogSink`. `SubstrateResult` (latency, tokens, cost — all
previously logged and dropped) now publishes one envelope per call on
`system.telemetry`. Two things surfaced: Reflection's flush spans a batch,
so deriving telemetry from a concluded turn would misattribute a shared
cost to one person's window (it creates its own correlation instead); a
record can't be written when the reply lands, so sinks get one after
`TurnLog:SettleMs` of quiet. Deliberately not done: embedding calls are
invisible (`IEmbeddingProvider` reports neither latency nor cost); latency
total is wall-clock, not summed, since a parallel fan-out summed would
claim more time than the turn took.

**Reflection colours Impulse (slow colouring).** Reflection's batch call
also returns a `mood|<label>` line from a closed five-label vocabulary,
parsed separately so it survives a batch with no ideas, riding the
`Reflected` control envelope. Lives on Reflection, not Archivist (a dumb
per-turn fact writer with no batch view). **Impulse owns every number**:
`SlowColoring` maps label → delta, unmapped label is a no-op. Deltas
~0.01-0.03 against instant nudges' 0.05-0.15, once per batch.

**Drive-state history as grounded interiority.** `memory.jsonl` was
append-only, every read asking for the newest line per path, growing
forever to hold lines nothing could return (had also accumulated ~135
archive-shaped rows from the pre-Parquet design, 97% fossil, since purged).
"One line per path" was recommended then withdrawn — it's the worse bug:
superseded vectors are the only record of how the persona has been over
time; a scalar is a gauge, the series is a history. Store keeps a
**window** per path (`Reflection:DriveHistory` deep), `DriveTrend.Describe`
turns it into words. **Words, never numbers** — a test asserts no decimal
reaches the prompt ("Curiosity: 0.83" invites the persona to quote its own
telemetry, the register of a status page). Still open: whether the trend
colours the note at all is unmeasured — ignored is acceptable, the persona
announcing its own engagement rising is the failure.

**Degraded-substrate notice.** A dropped connection used to produce a
fluent, confident, entirely ungrounded answer with no signal the persona
was thinking with half its faculties missing. `SubstrateHealth` holds the
vocabulary (a meta key, three causes, `Classify`/`Mark`), every caller
stamps its advisory. Governance emits **deterministic native text**: a
degraded Intent's notice *replaces* the reply (its fallback sentence isn't
an answer, dressing it up as one is the lie); a degraded advisor appends a
parenthetical; a Red verdict gets neither. Native matters because an
LLM-authored apology can't be produced by the LLM that isn't answering.
`UseSubstrate: false` is deliberately not a degradation. Reflection
retains a failed batch at the head of `_pending`; Archivist gets no
equivalent (facts were never extracted, a retained turn is just a second
guess). `TimeoutMs`/`CircuitOpenMs` are per provider, so five agents don't
each rediscover the same dead endpoint at full timeout cost. **Decided
against a startup reachability probe** — only catches "network down at
boot," gives false confidence when it passes, makes startup depend on the
internet.

*Still open from it:* `LibrarianAgent` overrides `HandleAsync` and
reimplements the base try/catch/log/publish nearly line for line, so
marking had to be written twice — folding it back is bigger than the
marking was. And an asymmetry worth naming: manifest drift fails loud
before the bus starts, but a `Tier` pointing at live providers never
verifies them — the most strictly validated config is the one that
silently degrades at runtime.

**Skipping the selection call — since reversed.** Librarian used to
short-circuit when the whole index fit under `MaxSelectedPairs`. `a0b43c9`
removed that fast path — the selector's judgment was never exercised until
the archive was too big to check by eye, and near-duplicate pairs (the
thing selection exists to tell apart) appear long before that. Recall
keeps its equivalent skip, because its budget is per-worker rather than
per-turn — an under-budget chunk is genuinely nothing to choose from.

**Normalize archive writes to English.** Writers used to record in
whatever language the turn was in, so switching languages produced
separate entries for the same fact. Solved as a prompt constraint, not a
translation pass, with **proper nouns carved out explicitly** — translating
a name would corrupt the record, worse than the duplication it prevents.

**Writes dedup by address.** The store appended even after normalization,
so an archive grew with every restatement. `AppendAsync` now replaces a
row at an existing subtopic/subject/key outright — the latest statement is
the true one. Deliberately *not* a field merge: one rule, explainable in a
sentence; a wrong overwrite is fixed by restating the fact.

**Archivist's path reuse is load-bearing by omission.** Reusing an
existing `category/topic/subtopic` keeps a restated fact on one address;
Archivist gets there by being shown Librarian's selected pairs as bare path
labels *only*. The bundle also carries `recall.facts` (the rows Recall
actually read), which Archivist never reads, so recalled values can't be
echoed back as freshly stated ones. Now has a test.

## The four-point plan — lean bus, instructions in config

Daniel's constraints: (1) the bus carries as little as possible, no
instruction text; (2) every substrate agent's instructions live in config;
(3) one block per agent, never shared; (4) Daniel revises them all by
hand.

**Stage 0 — `intent.prompt` was a confirmed bug.** `BuildPrompt` returned
instruction + contract + content, and the whole string was published, rode
into Reflection, rendered through a 240-char cap; the standing instruction
alone was 840 characters. **Reflection had never seen a turn** — not the
message, not advisories, not facts — just byte-identical boilerplate ten
times a batch, then the reply. Fixed by publishing the assembled *context*
and never the standing rules. Went first — no instruction rewrite could be
evaluated until it landed.

**Stage 1 — audit the bus.** Twenty-five meta keys traced to publisher and
readers (table now in architecture.md). Two keys had no reader and are
gone: `control.epoch_id` and `perception.source_type` — neither could ever
have failed a test, which is how both survived: **a key nobody reads
cannot be observed to be wrong**, the argument for auditing rather than
waiting for a symptom. Three `governance.*` keys have no in-process reader
and stay — `SseBroadcaster` fans whole envelopes, so they're the display
layer's contract.

**Stage 2 — instructions to config.** Plain text files, not JSON strings
(multi-paragraph prose in JSON means escaped newlines). Assembly stays in
C#; only text identical on every call moved.

*The cost of point 3, named.* `ArchiveWriteStyle` was one fragment shared
by Archivist and Reflection; splitting it makes drift possible, accepted
deliberately — a shared fragment can't be hand-revised for one agent
without silently revising the other. Mitigation is a test asserting both
files still state the rule, not that they match.

*The second coupling: the path convention.* `system/` was load-bearing for
three agents, named consistently by none. **The category itself was
wrong** — `system` held 60 rows of CAS architecture and 12 of persona
identity, and Intent's reading rule was true of the 12, false of the 60.
Renamed to **`assistant`**, keeping both topics under it (both instruction
texts already said "the assistant" in prose; architecture rows *are*
self-description too; no reader needs the distinction). `self` went the
same way — dropping it was considered and was wrong (`FixedCategory` filed
pushed ideas under `self/reflection`), settled by moving the data instead.
Cost, named: `assistant` is the role token, so every recalled row carries
the helpful-assistant prior into Intent's prompt — judged weak next to
Identity's persona instruction.

*Cut first, and let the prototype find the flaws.* The rename removed the
reason three clauses existed; all three were deleted rather than kept
behind a fixture — a rule kept "in case it's load-bearing" is never tested
and can never be removed later (`ResponseContract` grew back after
`407e5f1` trimmed it, this way). One asymmetry: **reads are free to break,
writes leave residue** — a bad reply is one visible turn, a bad Archivist
write is a row Recall serves back and Hindsight eventually wakes.

*A validator may reject a row, never edit one.* `ArchivistAgent` truncated
every archived value at 240 characters mid-word with an ellipsis via
`PromptCap.Apply`, sound on the way *in* (stopping a hop's text from
compounding), wrong on the way *out* into an append-only store — a
too-long value should be rejected, not stored corrupt forever. Removed
from both write paths; rejection stays for genuinely malformed output.
Length is asked for in the model's own terms, loosened to "1-5 keywords,
or one terse sentence with no filler."

*The rest of Archivist became a grammar.* Sorted by whether an instruction
states a *format* or coaches *behaviour*: the six-field line, per-field
word counts, known-pairs list, worked examples stayed; the meta-commentary
paragraph and anti-symptom patches ("do not infer, guess, or embellish,"
etc.) all went. 2065 characters to roughly 450.

**Stage 3 — closed, no changes.** Reviewed 2026-09-03; the five files were
read and none revised, since the cut-first method was applied during
authoring rather than saved for this stage. Four observed symptoms survive
as things to watch: Intent is theatric; advisories arrive unweighted;
Librarian/Recall select too narrowly (`MaxSelectedPairs` raised across
every tier, `MaxConcurrentRecalls`/`MaxPickedPerWorker` moved to per-tier
config); Archivist needs handholding.

**Stage 4 — the agents that never call a substrate.** Stage 2 missed
Identity's persona, Impulse's reflex reply, and Governance's three notices
— all C# constants, so none was in the folder reviewed, and the persona's
own self-description went unreviewed for months because changing it meant
a rebuild. Test is not "does a model see it" but **"is this a writing
decision."** `ArchiveWriteStyle.EnglishFields` was deleted outright — its
whole job (deciding whether a sentence appeared in a prompt) is now the
instruction file's job directly. **Identity is a seed, not a setting** —
the file writes to `assistant/persona` only when empty, the store wins
thereafter.

**Stale references and milestone tags** (`556bc43`). Nine `plan §X`
cross-references repointed at architecture.md; milestone tags describing
shipped work as pending were dropped. One survivor: `Archive:Path`
resolves to `memory.jsonl` and feeds the *agent state* store, not the
archive — a rename with a config migration attached, left alone.

**What the SSE stream ships.** `EnvelopeDto.From` serialised the whole
MetaBag, so the largest bus value went down `/api/stream` three times a
turn, read by nothing (free on the bus itself, waste purely at the HTTP
edge). `Sse:ExcludedMetaKeys` denies it there — a deny-list rather than an
allow-list, since forgetting to update an allow-list silently drops a
feature, while forgetting a deny-list is merely visible bloat.
