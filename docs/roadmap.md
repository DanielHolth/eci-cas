# ECI-CAS — Roadmap

The C# backend and its Next.js companion (`morrow-eci/`) are built and wired
end to end — [`architecture.md`](architecture.md) says what exists. This
document owns everything else: what's next, what's parked, what's out of
scope, and compact design records for what shipped.
[`product.md`](product.md) owns the other half — what is being sold, to
whom, and what is deliberately not promised.

**The archive is being inverted.** *The archive inverted — perception is
the record* below supersedes the pair-addressed store that *Reading the
archive back* and *Memory architecture* are written against. Those sections
are kept for their reasoning; where they conflict with the inversion, the
inversion wins.

**Next up.** Nothing is outstanding against the Python prototype's business
logic. The live work comes from the September 2026 external review, below.
Leading it: streaming Intent's tokens once the first sentence has cleared
Security.

---

# What's next

## From the external review — the deferred half

An outside model reviewed the codebase in September 2026: fourteen findings
and twelve ideas. The findings are fixed (`ccab952`, `5609174`) and the
review document is gone — a worklist recording what was already done is a
changelog wearing the wrong hat. Three ideas were pulled forward and shipped
with the fixes, being defects in an idea's clothes: standing rules ahead of
volatile data in `librarian.txt` and `recall.txt`, and saying
`[Recall: nothing on file]` out loud. One was declined — a per-key in-flight
`Task` map in `CachingEmbeddingProvider`; releasing the lock across the call
gets the parallelism, the map only adds dedup for a batching caller that
does not exist.

### Latency — the three serial calls

The floor is **Librarian → Recall → Intent**, roughly 700 + 500 + 900 ms
before a word reaches the person. Impulse, Identity and Hindsight run beside
them and cost nothing. In the order to attempt:

**Stream Intent's tokens.** The largest win, and it moves perceived rather
than actual latency. The obstacle is that Red must never reach Action, so
streaming provisionally and retracting is out. Running `SecurityRuleSet`
incrementally over accumulating text is affordable, but whether a rule
written against a sentence stays sound on a prefix is an open question. So:
**stream only after the first sentence has cleared the rules.** Gives up the
first ~200 ms, keeps the invariant, commits to nothing.

**Prefetch Recall's file reads from Hindsight's leads.** The passage sweep
is local and finishes in microseconds; its leads are usually a subset of the
final selection. Reading those pair files while the selection call is still
in flight spends idle disk and warms `ParquetArchiveStore`'s cache. No
substrate cost, no new message, and a wrong prefetch is only a wasted read.

**Raise Recall's skip threshold.** With `MaxPickedPerWorker` at 6, an
archive of 40 rows still pays a full picking round to discard almost
nothing. A threshold at "as much as a prompt comfortably holds" removes a
serial call from most turns and degrades into today's behaviour when it
stops being true. Safe where `a0b43c9` was not: that skipped the *selector*,
and the index quietly became the answer whenever it fit. This skips picking
*after* selection, so no judgment is bypassed — only a filter with nothing
to filter. Config knob, measured by `RetrievalProbe`.

**Pre-warm the HTTP connections at boot.** *Done* — `SubstrateWarmup` sends
one throwaway completion per distinct provider+model before the REPL prompt
appears, so DNS, TCP, TLS and (locally) the weights coming off disk are paid
where nobody is waiting. Deduplicated by provider+model, not by substrate
class: the minimal tier points all eight classes at one 4B. Bounded by
`Substrates:WarmupMs`, 0 disables, and it cannot fail a boot.
Paired with an explicit `PooledConnectionLifetime`: the factory otherwise
rotates handlers every two minutes and throws the warmed connection away, so
a persona idle for three minutes paid the handshake again anyway.

### Interiority that is actually grounded

Constrained by the standing rule: **surface interiority only where something
actually happened to cause it.** Every item is an event the system already
detects and throws away. Nothing here makes the persona talk about itself
more; that is the failure mode, not the goal.

**Notice when a fact changes.** `ParquetArchiveStore.Merged` already detects
the collision and silently replaces. That is the persona changing its mind
about the world. Carrying the superseded value forward — as a prior, or a
one-line note to Reflection — buys *"you said Oslo before"* with no new
retrieval, no new call, no invention.

**Let salience decay without deleting anything.** `Importance` is fixed at
write time, so a fact that mattered once outranks one that matters now,
forever, and Recall's budget is spent on it. Decaying it with age unless
re-touched, purely as a *retrieval* weight, gives forgetting-shaped
behaviour with no data loss. The capsule cares what is stored; the persona
cares what surfaces. They are allowed to differ.

**Let the corpus grow while nobody is talking.** Firing Reflection on
silence so it **thinks** rather than speaks — coming back after a week to a
persona that has had thoughts is a different thing from one resuming
mid-sentence. Speaking unprompted needs the parked platform decision;
thinking unprompted does not. This is the one item that acts with no person
in the loop, so it wants the generation cap honoured and a hard ceiling on
notes per idle period.

**Make the echo depth do something.** Hindsight computes `EchoDepth` and
nothing reads it. It detects the persona resonating with its own past
thoughts rather than the person's present one — a real failure mode for a
system that feeds its own notes back. Last of the five: the damper can
suppress genuine continuity as easily as an echo, so log what it actually
does across real sessions before letting it change a reply.

## Reading the archive back — the half not solved

**A sentence column, and a local embedding beside it.** Recall and the
Librarian both read rows rendered as `renewal / passport expiry = 2027-03`.
That is dense, telegraphic, and matches almost nothing a question says. The
proposal is a second column carrying the same fact as a plain sentence --
"Daniel's passport expires in March 2027" -- produced by the Archivist call
that is already happening, so no extra call at write time, and paid once
rather than on every read. It helps a weak reader and a strong one for the
same reason: there is more surface to match against.

The sentence is also what makes an embedding worth having. A vector over
`renewal / passport expiry = 2027-03` embeds badly; a vector over the
sentence does not. That is the third column: a few hundred numbers nothing
reads, compared arithmetically, so 100k rows in one pair can be narrowed to
a candidate set with no LLM call, no chunking, and -- the point Daniel
raised -- no importance ranking, which is what would otherwise discard a
low-importance row that happens to hold the answer.

Local, no API. The server at :8080 is llama.cpp and answers `501: does not
support embeddings, start it with --embeddings`, but the 4B is the wrong
model for it anyway: a bge-small or all-MiniLM class model is 30-130MB,
CPU-only, milliseconds a row, and can run in-process via ONNX with no server
at all. Cheaper than one Archivist call, so this is not a tier feature --
if anything Minimal needs it most, its reader being the weakest.

Two things this does not fix, stated so the measurement is not misread: it
cannot recover a fact the Archivist never extracted or a value it mangled
(the v3 `writable` ceiling, 29/35), and it does nothing for pair selection,
where whole-category already reaches 83%. It is a within-file narrower.

Ordering: sentence column first -- it is testable on the current bench and
useful without the vector. Embedding second, since proving it needs a model
that is not present and an archive far larger than the bench's.

**Revised after batch 12: this moves ahead of any further vocabulary work,
and for a second reason.** The ordering above was written when the open
question was which category/topic shelf to ship. That question is now
closed -- the shipped 170-pair shelf beats both consolidated shelves
end-to-end, consolidation is negative on every measurement that exists, and
the lean and terse shelves are withdrawn (`RESULTS.md` batches 8, 11, 12).
Two results from that work carry into this one.

The first is the rows column. Every shelf result in the log is explained by
facts-per-file: 1.7 for the shipped shelf, 2.0 for lean, 3.0 for terse, and
the oracle ranking tracks it exactly. Fewer folders means fatter folders and
a fatter folder loses more to Recall. Recall chunks by `RowsPerWorker`, so
rows a turn is LLM calls a turn: the winning arm reads 21 files and 48 rows
on a 24-statement corpus, and that is linear in the archive. A 100k-row file
is not a parquet problem -- parquet does not care -- it is 2000 model calls
in one turn, throttled to `MaxConcurrentRecalls`. The narrowing inside a
category is the thing that does not scale, and it is the only stage still
doing its narrowing with a model.

The second is sharper and corrects an assumption held through batches 3-11.
The paragraph above says the embedding "does nothing for pair selection" and
is "a within-file narrower", which is true and was read as a limit. Batch 12
says the within-file narrowing is where the loss actually is. Same shelf,
same archive, the only difference being whether Recall filters: 78% with
`nopick`, 60% with the lenient bar. Recall discarding rows costs 18pp --
more than selection, more than any shelf choice measured. `nopick` wins
because Recall stops judging and hands everything on.

So the embedding is not only a cost fix for a stage that works. It is a
replacement for the stage that loses the most, by a mechanism that cannot
make the same mistake: cosine takes the nearest rows without a model
deciding which ones are worth keeping. The target shape is the pair as the
coarse symbolic cut, cosine as the fine cut inside it, and `nopick` over
what survives -- the winning arm, at a candidate-set size that is constant
rather than linear in the archive.

**Shipped.** All five constraints below are implemented and tested; see
"Two layers of cosine" in [architecture.md](architecture.md). Row vectors are
stamped by an `EmbeddingArchiveStore` decorator so every writer gets them;
Recall narrows a pair only when the pair is fully covered, and skips picking
when every pair narrowed (`PickAfterVector` false, `VectorCandidates` per
tier); Librarian unions cosine-matched pairs into its LLM selection
(`VectorPairs`, `VectorMinScore`) and publishes the query vector for Recall to
reuse. `ArchiveTool embed` backfills an existing archive. What is still
unmeasured is the last constraint: fatness.

Five constraints on building it, each from a failure already in this log.

**Never sweep a partial file** -- a pair narrows by cosine only if every row
in it carries a vector from the current `ModelId`. Otherwise that pair falls
back to the whole-file path for this turn. A mix of embedded and unembedded
rows in one file means a cosine sweep that quietly skips half of it and a
plausible answer either way, which is the failure shape of `unfiled/unfiled`
(batch 9, void: 18 of 31 rows misfiled, full table printed) and of the
silently dropped arm name (batch 12's first launch). Both were runs that
looked complete and were not.

This paragraph said "non-nullable, with a startup check, it should refuse to
start", and that was wrong twice over. It conflicts with a posture the repo
already holds: `IEmbeddingProvider` states that unavailability is normal, not
exceptional -- the model file may simply not be downloaded -- and callers
check `Available` and fall back rather than catch. And it confuses two
conditions that have nothing to do with each other. An unavailable embedder
is a runtime condition; today's whole-file path is the fallback and nothing
should refuse to start. A partially embedded pair is the real hazard, and
strictness against it lives at read, per pair, where it can be enforced
against the file actually being swept. Old archives then need no migration:
they are uncovered until something rewrites them, and a pair heals on its
next write. `ArchiveTool` gets an explicit backfill for the impatient.

**The vector stays nullable through to `ArchiveRecord`** -- the one field
that does not get a friendly default. The reason is on disk already:
`ParquetArchiveStore.cs:432` writes `Sentence = r.Sentence` from a record
whose `Sentence` is non-nullable and defaults to `""`, so the null the row
class deliberately preserves survives exactly until the first rewrite of
that pair and is `""` forever after. For the sentence that collapse is
harmless -- both mean "no sentence" and `Rendered` treats them identically.
For a vector it is fatal in the quietest way: "no vector" and "empty vector"
are not the same thing, the coverage rule above keys off exactly that
distinction, and a `float[]` defaulted to `[]` at the record level makes
every unembedded row look embedded-and-empty to the read path.

**The hash of the embedded text sits beside the vector**, and a mismatch
counts as a missing vector. `Merged` replaces a row at an existing
subtopic/subject/key outright, so "lives in Oslo" followed by "lives in
Bergen" keeps the address and swaps the value. A vector inherited across
that swap points at Oslo forever and scores confidently, and nothing looks
wrong -- worse than partial coverage, which is at least detectable. It is
safe today only because nothing upstream sets the field, which is a property
of code that does not exist yet rather than an invariant. Deriving the
check from the text makes a restated fact self-invalidate whether or not the
writer remembered to clear it, and covers a hand-edited row and an
`ArchiveTool` import by the same rule. Same argument as `ModelId`: a vector
that outlives its source must be detectable, not merely improbable.

**The pair keeps its job.** There is a class of question no embedding reaches
-- inference over facts rather than similarity to them, the "am I old
enough to rent" case below -- so the symbolic cut is not replaced by the
arithmetic one. This is also the argument against growing the shelf past
170: once the vector does the narrowing, more hand-written pairs buy
nothing, and every one of them is a curation decision on a schema that
cannot be migrated after rows point at it.

**Fatness has never been measured.** The bench corpus is 24 statements and 35
rows, three orders of magnitude too small to see what a fat file does. No
claim that "170 pairs is enough" is supported by anything here. Proving the
embedding needs a larger corpus first -- one grown until files hold 20+
rows -- and that corpus is a prerequisite, not a follow-up.


The write side works, but "settled" was too strong. Archivist extracts
`subtopic/subject/key=value`, Cataloger picks a drawer then a folder from a
closed vocabulary, and code joins them into the path — and measured against a
key of defensible pairs
([`tools/retrieval-bench/`](../tools/retrieval-bench/)) that lands at 82% of
rows filed somewhere reasonable, over four interleaved reps. The spread is
the story: 67, 100, 92, 69 on the *same twelve statements*. Almost all of it
is extraction, not filing — a rep that turns "My brother Lars lives in Tromso"
into `user living situation = travels with brother` has already lost the fact
before any drawer is chosen. Run-to-run extraction variance on the 4B is the
largest single source of error on the write side and nothing currently
measures it directly.

Two rules were fixed on the strength of that bench.

`archivist.txt` now says that **key is a property of subject** — the colour of
a car has `subject=car`, not `subject=user` — and that a fact about another
person is filed **under that person's name, not under the relation**. Value
sufficiency 81% → 91%, better or equal in 5 reps of 5 and better in 4; drawer
accuracy unchanged; and fabrication on the eight null messages fell from 0.4
rows per rep to 0.0, which is the opposite of the usual trade — a rule that
makes extraction more complete normally makes it more inventive too. Tested
alongside it and *not* shipped: "the key names what was stated, not what it
means", which wins three reps, loses one and ties one. That is what the noise
floor produces unaided.

The second is a tie rule in `cataloger.txt`. "A person goes in
relations. A fact about the job goes in work." was already in `cataloger.txt`,
already read, and already losing: "My manager is called Petter Aas" went to
work in nearly every rep, and to a different folder there each time. The line
decides the tie without naming the case it has to decide — a person the user
knows *only* through the job. Naming it took drawer accuracy from 88% to 96%,
better or equal in 5 reps of 5. The lesson generalises past this one line: a
tie rule that states the distinction but not the case it applies to is a
comment.

Note also what the 58% below does *not* include. It scores pair-hit alone —
no value is ever inspected — so end-to-end recall is 58% times whatever
fraction of rows still contain the answer, and that second number had never
been measured until `tools/retrieval-bench/`. Read the two separately.

**Retrieval is where the bigger loss is, and it is worse than 58%.** That
number was selection alone, over 17 questions, with the padding pairs as
empty files. Re-measured on
[`tools/retrieval-bench/read_bench.py`](../tools/retrieval-bench/read_bench.py)
— 35 questions, all 170 pairs populated so a wrong pick returns something
plausible — the read path scores:

    category 79%   select 36%   pick 76%   answer 25%

Three reps, and rep 1 alone gave 34/83/31, so the shape is stable. Nothing
regressed; the instrument got honest.

**The loss is one stage and one level.** `pick` at 76% says Recall keeps the
row once the file is open, so the second stage is not where facts go. And of
19 selection misses in the first rep, **14 opened the right category and the
wrong folder** — the licence filed at `admin/renewal` while the reader opened
`admin/licence`; the cabin at `household/address` while the reader opened
`household/property`; the degree at `learning/university` while the reader
opened `learning/course`. Counting category-level hits the reader is right
79% of the time.

So the reading recorded below for the hierarchical dead end — "the right
drawer is not guessable from the question" — does not survive this. The
drawer is guessable. The folder inside it is not, because 15–17 one-word
topics per category contain two or three that mean the same thing to a 4B.

**First fix measured: synonyms beside the topic names.** A few words per
topic in the *question's* vocabulary rather than the folder's
(`admin/renewal (expires, expiry, renew, runs out, valid until)`), shown to
the selector only, so filing stays byte-identical and a moved number has one
possible parent. Over 3 interleaved reps: select 40% → 51%, answer 32% →
43%, better in 3 of 3 on both. Category flat at 80% — the gloss does not help
the reader find the drawer, which is right, because the drawer was never the
problem.

Four of the five *real* category misses were the write side, not the read
side: "I gave up smoking" filed to `leisure/hobby`, the 07:20 bus to
`travel/trip`, the spare key to `household/address`. v2 never showed this
because v2's statements were tidier than a person is.

Dead ends, so nobody spends the week twice — all measured on the 4B, all
worse than the flat list they were meant to beat:

- **Hierarchical read** (pick a category, then a topic inside it) — the same
  two-step that won +17pp on the *write* side loses 35pp on the read side
  (58/67/58 flat vs 25/17/25). Still the only read result that clears its
  noise floor. The reason recorded here was "the right drawer is not
  guessable from the question", and that reason is now known to be wrong —
  the drawer is guessable 79% of the time. Why the two-step loses anyway is
  open; a plausible reading is that committing to one category discards the
  second-best drawer, and the fix for a folder-level problem was applied a
  level too high.
- **Wider fan-out** — opening 2, 3 or 5 files scored identically. The misses
  are wrong picks, not too few picks, and paying for more files buys nothing.
- **Two archives merged / redundant filing** — +0%. 1.83 addresses per
  statement mostly buys stale ghosts on the next update.
- **Handing the vocabulary to the bundled agent as `{known}`** — 0 for 4,
  −15%. A model given a field it was not asked for fills it anyway.
- **A second pass over `other`** (write side, but the same shape). When the
  topic call answers `other`, ask again with `other` removed; if the reviewer
  says `none`, code puts it back. It does what it was built to do — `other`
  falls from 1.0 to 0.2 rows per rep — and buys nothing: 82% filed either way,
  better in 1 rep of 4. Re-asking the *category* instead, on the theory that
  `other` is really evidence the drawer was wrong, scored 80%. The reason is
  visible in the misses: the rescued rows land in a plausible sibling folder
  rather than the right one, and `x/other` was never the disaster it looks
  like — the read path opens `other` alongside its parent category in code, so
  a fact in `relations/other` is *more* retrievable than the same fact forced
  into `relations/neighbour`. `other` is doing its job. Left in
  `tools/retrieval-bench/review_other.py`; not shipped.

Untried when this was written and now in the arena
([`tools/retrieval-bench/arena.py`](../tools/retrieval-bench/arena.py)):
asking the question of a *sample of values* rather than of file names;
opening every topic in the category the selector chose, which spends the 79%
directly and is the honest re-test of the fan-out dead end; a lexical union
on row subjects, which is Daniel's two-model idea with the second model
removed; and a consolidated 40-pair vocabulary. Still untried: embeddings
over the index, blocked on `models/embedding/` not existing.

And the caveat that outranks all four: with 12 statements against a 10pp
noise floor, "identical", "+0%" and "0 for 4" are *undetectable*, not
disproven. Only the 35pp hierarchical loss clears the floor. Three of those
four dead ends are unmeasured, not dead.

**A corpus nobody has tuned against — now written.**
[`tests/corpora/retrieval_v3.py`](../tests/corpora/retrieval_v3.py) is 24
statements and 35 direct questions, written before anything was run, plus 8
oblique questions that do not name the fact they need ("who should we drop by
and visit while we are staying in Bodo?" wanting a sister filed under
`relations/partner`). The oblique tier is scored separately and never folded
into the headline; it sits at 2/8. Everything above is measured on v3. What
follows is the old note, kept because it still describes v2:

Also needed before any of this is trusted: a corpus nobody has tuned against.
The fixture behind every number above is
[`tests/corpora/retrieval_v2.py`](../tests/corpora/retrieval_v2.py) — 12
statements to iterate on, 12 held back, 8 that state nothing so a fabrication
shows up as one. The held half is burned: it was scored repeatedly as the
design moved, so it no longer measures what a holdout measures. Writing a
fresh one is the prerequisite for the next honest comparison, not an extra.

## Companion & knowledge extensions (not started)

**Speech-to-text input.** Dictation only — push-to-talk filling the existing
composer, so what is sent stays reviewable text and
`sendPerceive(text, profileId)` is unchanged. Purely a surface feature: no
new topic, no audio on meta, no agent contract change. Speaker
identification is **cut** — see below; the mic answers *what was said*,
never *who said it*.

**Biometric + camera authentication.** Device biometrics at unlock; a
different person picking up the device triggers camera-based profile
creation. Backend is a user-context field on Perception's meta, which the
profile field already is.

**Diary knowledge category.** A category whose entries accumulate rather
than overwrite — recurring appointments, dated milestones — so a new
doctor's visit doesn't clobber the last one. Recall surfaces them in
temporal order, not as overwriting facts.

**Profiles, later increments.** A new name in conversation offering to
create a profile; profile deletion and merge.

## One instance per person (symbiosis)

**The intended shape is one Morrow-ECI per person**, not one shared persona
tracking who it is talking to. The relationship is symbiotic: the persona
develops against a single person over a long time, and that only works if
its drive state, its self-derived ideas and its personal archive all belong
to that one relationship. A family of four is four instances, not one
instance with four hats.

This is why **speaker identification is cut**. It only ever answered "which
user is this" on a device with one shared persona — a question that does not
arise when the instance already belongs to someone.

**Accessibility is a primary driver.** A companion that knows one person
deeply — their routine, their vocabulary, what they can and can't do
unaided — is most valuable to someone who needs it, and that value comes
from depth against one person rather than breadth across several.

Multi-user profiles keep their place as the **shared-device path**, not the
primary design. Nothing shipped in iteration 1 is invalidated; what changes
is that profile-scoped storage is the accommodation, while a dedicated
instance gets the whole archive by construction.

## Toolbox agent — IoT actions (not started)

Action today only produces speech. A companion that matters to someone with
a disability has to *do* things: lights, locks, thermostat, blinds. The
sketch is a **toolbox agent** owning a registry of callable device
capabilities, on the action side of Governance so every device call passes
the same verdict gate a reply does — an IoT action is exactly the class of
thing that must never fire on Red.

**A device response comes back in as perception.** Not a return value:
the toolbox publishes what the device said onto `events.perception` and it
runs as an ordinary turn, the same seam Reflection's ideas use with a
different tag (`"device"`). No new topic, no new contract. The payoff is
that Impulse colours on it for free — a lock that refuses to close is
something the persona should *feel*, and a return-value design would make
that a special case. Unsolicited state comes free too: a doorbell is a
perception with no preceding action.

Two hazards to settle in the design pass:

- **The loop.** Action → perception → action is a cycle, and a device turn
  firing another device call is how a house starts flapping. Probably a rule
  that a `triggered_by = "device"` turn may speak but may not act — stricter
  than a depth cap and easier to reason about. **That rule is Governance's**:
  it is a verdict on an action.
- **Archivist.** It hard-skips `"self"` today. Device turns need the same
  decision made deliberately: most acks are noise, a few are facts worth
  keeping. Likeliest shape is skip by default and let the toolbox write the
  rows that matter.

**Flood guard — `DeviceBlockCount`.** A flapping sensor drives a full agent
turn per event, which is real spend and a console nobody can see past. Count
events per device over a window and stop admitting past the threshold. Per
device, never global. **The count belongs in the toolbox, not Governance**:
by the time Governance sees a turn, four agents have already made substrate
calls, so filtering there pays for every flapping event and only then
declines to act. Admission control sits at the boundary, before publish.

**A trip is spoken, not silent** — suppression the person can't see is
indistinguishable from a device that stopped working, so the block enters as
one perception and Intent voices it, exactly once, on the transition. Two
details: the *count* is what's suppressed, not the drive nudge (a flood must
not colour Impulse per event, or a broken device rewrites the relationship
overnight); and **recovery is open**. Automatic decay hides a real fault
behind a re-flood cycle; manual-only loses a sensor permanently for someone
who cannot reach it. The escape is neither — the persona *raises* it after a
quiet window and stays blocked until a person answers. Mechanically that is
the drive-gated push Reflection already does.

Open beyond that: one agent with a tool registry or one per protocol; which
integration surface (Matter, Home Assistant, MQTT, vendor APIs); and how a
tool call is represented on the bus without giving Intent a second output
vocabulary. Wants its own design pass before code.

**IoT is one instance of a general shape, not the shape itself.** Any
lookup Intent shouldn't carry in context every turn — technical docs about
the system, later whatever else — fits the same seam: a dedicated agent
answers async, off the bus, tagged `self`/`tool` rather than `device`, and
comes back in as an ordinary `perception.text` like the toolbox's device
replies do. A minimal `skills.txt` index (not the rows themselves) is the
only thing that needs to live in context, the same trade Recall already
makes at file-name granularity. Not started; parked behind the toolbox
design pass since the hazards are the same ones — provenance on the
returned fact, and Archivist deciding whether a skill's answer is worth
writing down at all.

### One request, two actions

A tool request is not a turn that stalls until the tool answers. It is a turn
that produces **two** actions: the ordinary spoken reply — *"I've started
working on that and I'll get back to you as soon as the tool is finished"* —
and beside it an action of type `tool`, dispatched to the toolbox. The person
gets an answer at conversational latency; the tool takes as long as it takes
and returns the way a device does, as a fresh perception.

**Dispatching is not waiting.** The `tool` action starts the work and the turn
ends — nothing on the bus is holding a call open. The next action about that
request only exists because the *result* came back in as a perception and ran
an ordinary turn on it. So a tool that takes four minutes costs nothing for
four minutes, and the reply about it is written by an Intent that has the
answer in front of it rather than one waiting for it.

This is why the toolbox sits on the action side of Governance rather than
inside Intent. A tool call is a *second action on the same verdict*, so it is
gated once, by the machinery that already gates speech, and a Red turn emits
neither. Intent keeps one output vocabulary; what varies is how many actions
the verdict releases.

The acknowledgement is Intent's own sentence, not a canned string. It knows
what was asked and can say so — *"I'll check the camera and come back"* — and
a fixed template would be the one line in the conversation the persona did not
write.

**The class is wider than IoT**, and worth naming early because it decides
whether the registry is a device registry or a tool registry:

- *"check this in your manual"*, *"tell me about your debug settings"*, *"do
  you want to change anything in your settings?"* — the system reading and
  eventually writing itself. The last one is not a lookup; it is a proposal the
  person approves.
- *"check my \[IoT] and give me status"*, *"check my camera feed from last
  night"* — devices, the original sketch.
- *"how is cowork progressing?"*, *"check my stream on the other monitor"* —
  another machine's state, or another agent's.
- *"look something up online"* — the open web, which is the one that needs its
  own answer on provenance and on Security.
- *"tell me about your thoughts"* — the passage corpus, already local, already
  written. The cheapest first tool, and the one that proves the seam without
  any integration at all.
- *"has the community added tools to the workshop?"* — a registry that changes
  under the running system, which is a different problem from calling a tool
  and should not be conflated with one.

### ToolManager — Governance for handlers

One **ToolManager** in front of many **tool handlers**, each handler owning one
device or service. The manager is to handlers what Governance is to agents: the
single place a call is admitted, tracked and given up on, so a handler is only
ever the thing that talks to its device.

**It owns the timeout, and a timeout is a perception.** A dispatched call the
manager never hears back about is not an error swallowed at the boundary — the
acknowledgement already promised the person an answer, so silence is the one
outcome that must not be silent. The manager publishes it as a perception like
any other result, and the turn that runs on it is the persona saying the camera
never answered. Same seam, same shape as success; only the content differs.
`DeviceBlockCount` belongs here for the same reason admission control does.

This also settles where a handler's failure stops. A handler that throws, or a
device that refuses, reaches the manager and leaves it as a perception; nothing
propagates back to the action that started it, because that action finished
long ago.

Open: whether the tool result arrives tagged `tool` and unified with `device`
at the perception seam or stays distinct.

**The manager's own state is reached by a tool like anything else.** *"What
were you checking?"* is not a context problem to be solved by plumbing the
in-flight list into Intent's prompt — it is a request that dispatches a
diagnostics handler, which asks the manager and returns the answer as a
perception. Nothing about the running system needs to be resident to be
askable. That is the same trade the `skills.txt` index makes and the reason
*"tell me about your debug settings"* sits in the list above beside *"check my
camera"*: introspection is a tool, and the manager is one of the things it can
be pointed at.

## The archive inverted — perception is the record

Everything below in *Memory architecture* assumes the pair-addressed store:
a fact is extracted into `subtopic/subject/key = value`, filed under a
category and topic, and read back by selecting a pair. Batches 12-16 took
that apart piece by piece (see *Open design questions*), and what is left
standing is smaller and differently shaped than the thing it replaced.

**The inversion.** There is no data model the write side has to satisfy.
There is a log of what was perceived, and an Intent that makes sense of it
on the way past. The archive stops asserting truth and starts holding
evidence.

**Status: shipped, behind `Utterances:Enabled` (default false).** What is
described below is built and tested, not planned. `ParquetUtteranceLog`
holds the log as one parquet per month; `ThreadWeaver` threads at write
time against one frozen earliest representative per thread;
`UtteranceConsult` reads it with the two-pass split; `UtteranceBackfill`
rebuilds vectors and threads at boot. With the flag on, `InvertManifest`
drops Librarian, Archivist and Cataloger from both manifests and puts
Recall and Scribe on Perception -- the flag is one boolean and the roster
follows it in code, so there is no second topology in appsettings to
drift. With it off nothing changes and nothing is written. The settings
below are the measured ones (benches 22-25): threshold 0.86, top-5
consolidator candidates, MMR lambda 0.7, lexical weight 0.15, ReadMinScore
0.55. The consolidator is off by default and degrades as described under
*The consolidator* -- a non-verbatim candidate mints a new thread rather
than joining.

### Ground truth, and everything else

One line separates what is permanent from what is not, and it is the line
the hundred-year claim rests on.

**Ground truth** is the original utterance, a timestamp, who said it, and
keywords. Appended, never rewritten. Readable in 2126 by anyone with a
parquet reader and no model, no vocabulary, and no code of ours.

**Everything else is derived and disposable**: vectors, score columns, any
partition, any index, any shelf. All of it rebuildable from ground truth by
a background job. `ArchiveBackfill` already grants vectors that status
because the embedder can change; the rest inherits it.

That line is what makes a dynamic shelf safe. An open vocabulary was
rejected before because the shelf *was* the index -- folders invented by a
4B become a schema a stronger model must live with, with no migration path.
Once retrieval no longer routes through it, nothing depends on it being
right, and a bad shelf in 2030 is a recomputation rather than a legacy.

**Store the message, not the parse.** Batch 15 measured the retrieval
ceiling by what is kept: 91% for the address line, 97% adding the sentence,
100% adding the message. `pet/dog/name = rex` is a lossy compression of
something already in hand. It is also destructive for the second consult
mode below -- word frequencies cannot be counted over a corpus of parsed
key-values.

### Keywords are the lexical half, not a lightweight drawer

Embeddings are weak exactly where a query is a token: names, dates,
numbers, rare words. "What is Rex's vet called" is a lexical question
wearing a semantic costume. Keywords cover that blind spot, fused with
cosine rather than replacing it.

Extraction is deterministic -- tokenise, drop stopwords, and keep what is
rare in the corpus, with capitalisation and digits as a complement rather
than as equals. No model. That matters: it is what stops keywords
reintroducing a write-side call through the back door.

The ordering is measured, not stylistic (batch 22). Casing and digits alone
keep a third of what v4's answers need -- *penicillin*, *seasick*, *choir*
are neither capitalised nor numeric, and they are most of what an archive is
about. Rarity carries the extractor; casing recovers the names and compounds
stated often enough to stop being rare. Together they lose nothing against
keeping every non-stopword. What that run cannot settle is the threshold: at
78 statements almost everything is rare, so the value of *rare* as a filter
is a v5 question.

### Two ways to consult a memory

**Find.** Hybrid vector plus lexical, top-k. "What is Rex's vet called."
This is the path batches 12-16 measured, and flat cosine over the whole
archive was the best arm in the log at 78%.

**Characterise.** An aggregate over a filtered subset. "What was my uncle
interested in", "what do I always eat", "what changed after the divorce".
Top-k cannot answer these -- five rows is not what *interested in* means.
It is a scan with counts over keywords, speakers and timestamps: SQL, not
cosine, and no model on the read side either.

The second mode is most of what makes a twenty-year archive feel like it
knows someone rather than merely containing them, and it exists only
because the original text was kept.

### Characterise, specified

The paragraph above is the whole of what was ever written down about the
mode that justifies keeping raw text, and "SQL, not cosine" is a slogan, not
a plan. What follows is the plan, and it costs the section one of its
claims.

**Three query shapes, not one.** They differ in what the filter is and what
the aggregate counts, so treating them as one mode is what let the design
stay a paragraph.

- *Trait.* "What was my uncle interested in." Filter by participant or
  subject, count terms, rank by distinctiveness.
- *Habit.* "What do I always eat", "how often do I call my mother." Filter,
  then count **threads and their members**, ranked by recurrence rather than
  by score. Threading already computes this; habit is the query that spends
  it.
- *Change.* "What changed after the divorce", "what did I stop caring
  about." A split on the time axis, two aggregates, and the diff of the two
  term distributions. Superseded rows are in scope here by the rule above,
  and this is the mode that makes keeping them pay.

**The filter is the hard part, and it is not free of a model.** "After the
divorce" is not a column; it is a date the archive holds as an utterance.
So Characterise is *Find* then aggregate -- one retrieval to resolve the
anchor to a timestamp, a participant or a keyword set, then the scan. That
demotes "no model on the read side either" to false as written: the scan
needs none, but deciding that a question is a Characterise at all, and what
its filter is, is a call. It is the same call Intent already makes, so the
cost is a decision added to an existing prompt rather than a new hop -- but
it should be written down as a call, because a mode whose routing is
mis-specced fails by silently answering as *Find*.

**Contrast, not frequency.** Counting terms in the subset returns the
corpus's own background -- *think*, *really*, *today*. What is wanted is
what is distinctive about the subset against the archive as a whole: a
log-odds or tf-idf contrast, computed at read time over two counts. This is
the technical core of the mode and it is twenty lines and no model. Ranking
by raw count is the obvious implementation and it is the one that fails, so
it is an arm in the bench below rather than a footnote.

**What reaches Intent is an aggregate, never the subset.** Ranked terms with
counts, thread representatives with their date ranges and recurrence, and a
bounded number of exemplar utterances -- typically the newest, the oldest,
and the most recurrent. The prompt is therefore constant in archive size,
which is the property that makes the mode shippable at all: a subset of ten
thousand rows and one of forty produce the same shape of answer.

**Where it degrades.** A subset under some floor is not an aggregate, it is
a list -- fall back to handing the rows over as *Find* does, because counts
over nine rows are noise wearing a number. A subset that is most of the
archive ("what am I like") is the opposite failure and has no honest answer;
it should be answered as such rather than by summarising a decade into six
adjectives. An unresolvable anchor is a question back to the person, not a
guess at a date.

### Characterise -- pre-registered, before anything is built

Every arm below is measurable and none has been measured. Written before the
corpus exists, same rule as v3 and v4.

**v4 cannot be the instrument.** Its statements are one-shot, undated and
unrepeated -- there is no recurrence to count, no time axis to split on, and
no speaker but one. Every question this mode exists for is unaskable of it.
The instrument is **v5, a longitudinal corpus**, and its shape is settled
before generation:

- Utterances carry a timestamp and a speaker, spread over years, and the
  *distribution is authored first*: a true habit stated twelve times across
  the span, a decoy stated three, a former habit stated eight times all
  before a dated hinge and never after. The rendered sentences come second,
  so the key is structural rather than a judgement about text.
- Near-miss terms that co-occur with the true answer without being it, or
  the contrast arm wins by having nothing to discriminate.
- Deliberately thin subsets, to exercise the floor.
- One "what am I like" question with no honest answer, scored on refusal.

**The key is a set of terms that must appear and a set that must not**, per
question -- gross correctness, the same shape as `filing_key.py`, not a
judgement of phrasing.

**Arms**, over one frozen archive, differing only in what is handed to
Intent:

    find-only        top-k, the mode the doc asserts cannot answer these
    count            aggregate, raw term frequency
    contrast         aggregate, log-odds against the whole archive
    contrast+thread  the above, plus recurrence and date ranges
    +exemplars       the above, plus bounded exemplar utterances

`find-only` is the baseline that matters. The claim "top-k cannot answer
these -- five rows is not what *interested in* means" is the premise the
entire mode rests on and it is an assertion in a document, not a number. If
top-30 flat cosine handed over whole answers these adequately, most of this
section is unnecessary and should be deleted rather than defended.

**The first gate, which needs no corpus and no server.** Every arm here and
the consolidator's disagreement test both stand on deterministic keyword
extraction -- tokenise, drop stopwords, keep capitalised tokens, numbers and
rare terms. That extractor can be run over v4's statements and nulls today,
scored on whether the tokens an answer actually needs survive it and whether
the background survives with them. If keywords are noise, the lexical half
of *Find*, all of *Characterise*, and the consolidator's gate are noise
together, and no longitudinal corpus is worth writing yet. That is the
cheapest disconfirmation available and it runs first.

### Time shards, not importance tiers

A single parquet grows to a gigabyte and every append rewrites it. Shard by
**time** -- one file per year or month. Bounded size, appends touch only the
newest shard, old shards immutable, which also makes incremental encrypted
backup a matter of uploading each year once.

**Nothing routes on a shard.** With the Librarian gone there is no file
selection anywhere in the read path; a query sweeps every shard's vectors
and takes the best rows wherever they live. Shards are a container, not a
decision. For a human descendant, time is the most navigable index there
is.

Tiering by *importance* -- hot, cold, archive -- was considered and
rejected. A row untouched for 180 days is as likely to be a grandmother's
last recipe as a restaurant visited once, and a product promising decades
must never make a memory harder to reach for having been quiet. Recency and
frequency survive as **score columns**, where the effect is recoverable,
rather than as locations, where it is a wall.

The frequency metric is a rate, not a count: hits divided by
opportunities -- surfaced ten times across a thousand turns -- so it stays
comparable across a row's age, where a raw count only rewards rows for
having existed longer.

Only the numerator is per-row. The row carries `hit_count` and the turn
ordinal it was first seen at; the denominator is a single global turn
counter kept outside the shards, and the rate is
`hit_count / (turns_now - turns_at_first_seen)`, computed at read time. So
a turn writes one integer in one place, not a column across every row it
did not touch -- which is the difference between an append-only store and
one that rewrites a gigabyte to record that nothing happened.

User-set pins ("I care about skiing") sit beside the rate as an explicit
boost and outrank any metric, being the person saying what matters.

### What this deletes

The 170-pair shelf as a schema (it survives as a *seed* for clustering and
as a browsing view), the closed vocabulary as a routing mechanism, the
Cataloger's two calls per fact, Recall's chunk-and-pick, and the Librarian
entirely. All of it was properly benched, and all of it was beaten by the
flat baseline sitting in the same results file.

The Archivist shrinks rather than vanishing. Extraction and splitting go;
what is not obviously droppable is the judgment -- *is this worth keeping*,
and *does it supersede something* -- because an append-only log of
utterances never overwrites, and cosine will hand Intent a dead dog five
years on. Timestamps recover most of it: Intent reading "dog named Buddy
(2019)" beside "new dog, Rex, after Buddy died (2024)" resolves the
contradiction the way a person does. That only works if retrieval hands it
*both* rows, which is the open arm below.

Nothing in the current model is deployed to anyone, so there is no
migration to design. The parquet schema is free.

### Threads — accumulation is the inversion's own failure mode

The pair store deduplicated for free: a restatement landed on the same
address and replaced what was there. A log has no addresses, so "my car is a
Tesla, so much better than my old Subaru" accumulates beside the 2016 row
that named the Subaru, and forty repetitions of a fact become forty rows
competing for the same five slots in front of Intent.

Two problems wearing one costume. **Redundancy** is the same value restated,
and it is only a top-k problem -- the disk does not care, and the repetition
is itself signal worth keeping. **Supersession** is a different value at the
same subject, and there the log is right to hold both: "my old Subaru" is
true history someone will want in 2040. What fails is retrieval handing
Intent one row and calling it the present.

**A thread id, minted on write.** Embed the row -- already happening on the
way to disk -- sweep, and if the nearest thread representative clears the
threshold, inherit its id; otherwise mint a new one. Deterministic, no model,
and off the critical path, where nobody is waiting.

The check does not disappear, it relocates. Doing this at read time is
O(k squared) cosine every turn, forever, and structurally blind: a collapse
pass over the top-k cannot know about a 2016 Subaru that cosine did not
already return. Doing it on write is one sweep against **one representative
per thread**, over the whole archive, and the set it scans grows with
distinct subjects rather than with utterances -- a person acquires new topics
slowly and repeats endlessly, so the thing being scanned stays small while
the log does not.

What it buys:

- Repetition costs one slot, not forty. Retrieval groups by thread before
  ranking, so "said 40 times, Mar 2025-now" reaches Intent as one row
  carrying its own frequency -- more than the pair store ever gave it.
- "What car do I drive" is the newest row in a thread. This recovers the one
  thing the pair store was genuinely good at, addressing current state,
  without reintroducing a schema to do it. The fast-changing subjects -- car,
  job, city, the series someone is watching -- are exactly the queries that
  silently mean *now*.
- Disagreement inside a thread is one operation. Newest plus
  oldest-that-differs, handed over with dates, which is the open arm above.

**Superseded rows do not compete, but they are one hop away.** The read-side
rule is the two consult modes, not a global filter: *Find* means *now*, so it
drops anything carrying a `superseded_by`; *Characterise* is history by
definition -- "what cars have I owned", "what changed after the divorce" --
so it does not. This is what keeps the working set lean without a deletion:
forty dead rows are invisible to the sweep, and the fat index never forms.
Dereferencing from the live row is what keeps "you said Oslo before"
reachable, and it is also the answer to a query that asks *for* the dead row
by name, which a hard filter gets wrong.

**Compare against the thread's representative, not any member.** Chaining --
A matches B, B matches C, A does not match C -- walks a thread across
subjects over twenty years, and bounded drift matters more here than
anywhere given what the archive is being promised for.

**Bias the threshold high and bench it.** The errors are not symmetric. A
false split restores today's behaviour, which is duplicates and recoverable;
a false merge glues two subjects together and makes *now* wrong, which is
not recoverable at read time. Start near 0.9 rather than 0.85 and let the
corpus argue it down.

### The consolidator — judgment where cosine has none

Cosine says two rows are about the same thing. It cannot say which of
"my new car is a Tesla" and "my *wife's* new car is a Volvo" supersedes the
other, because the sentences are near-identical and the difference is the
part cosine throws away. That is a reading problem, and it wants a model.

**Gated by the sweep, not run per fact.** Most new rows have no candidate
above the threshold at all, and for those there is nothing to disambiguate
and no call to make. When the sweep does return candidates, the top five go
to one call carrying only the facts that had a hit -- one call per turn
rather than per fact, and the inversion's deletion of the write-side call
budget mostly survives.

**Cardinality is the wrong gate; disagreement is the right one.** Skipping
the call when the sweep returned a single candidate optimises away exactly
the case the consolidator exists for -- "my wife's new car is a Volvo"
against "my new car is a Tesla" is plausibly a lone hit above threshold, and
auto-linking it is a false merge, the error read time cannot undo.

What predicts danger is not how many candidates there are but whether the
*values* disagree, and the keyword extraction above already detects that
without a model. Cosine clears the threshold and the keyword sets match: a
near-verbatim restatement, threaded deterministically, no call -- and this is
where the volume sits in a chatty archive, so this is where the saving
actually comes from. Cosine clears it and the keywords differ on a rare token
-- Tesla against Subaru, Oslo against Bergen -- and that is what supersession
looks like, so the call is made whether there was one candidate or five.

The principle underneath: a false merge costs in proportion to how much the
two values disagree. Gluing together two rows that say the same thing is
nearly free even when it is wrong. Spend the call where the disagreement is.

**It links, it never replaces.** The verdict writes a thread id and a
`superseded_by`; the utterance is not touched, not rewritten, not deleted.
At read time the effect is what replacement would have given -- the Subaru
stops surfacing as current -- and ground truth keeps its guarantee. A
consolidator with permission to overwrite the log would be the one feature
able to falsify the hundred-year claim.

**Which makes the tiering fall out rather than be designed.** Threading is
deterministic and costs nothing, so it runs on every tier. The consolidator
is a substrate call, so it is what a paid tier buys. And because both write
only derived columns, an upgrade is a **backfill, not a migration**: the same
status `ArchiveBackfill` already grants vectors, the same background job
shape. A free-tier archive of six years upgraded in 2032 is consolidated by
re-running over an untouched log -- and so is an archive consolidated by a
weaker model in 2027, once a better one exists. The judgment is disposable
on purpose.

Open: whether the model should be allowed to say *neither* -- two rows that
cosine threaded and a reader would keep apart -- and whether that unthreads
them or only marks them. Splitting a thread is the write no other path here
performs.

## Skill hints and deferred turns

**A skill agent on perception, where the Librarian sat.** It publishes a
hint -- `toolkit: metric-analysis` -- and nothing more. Intent decides
whether the tool actually runs and Governance executes it: exactly the
split the Librarian had, where the side agent suggests, Intent owns the
framing and Governance owns the action. Killing the Librarian vacates that
slot rather than removing the pattern.

It is a second call on the critical path, which makes it a clean paid-tier
boundary: the free tier reads its memory, the paid tier reasons about it.

**A turn may answer later.** "I have started the tool that answers what
your uncle was interested in, and will come back when I know" is a turn
producing a deferred result, delivered as a notification rather than
blocking the conversation. Reflection wants the same schedule-and-notify
mechanism, so the two features cost one piece of machinery -- and an entity
that goes away and thinks reads as more alive than one that stalls
mid-sentence.

**Maintenance is a consented tool, never a silent process.** "That is old
news" leads to *"shall I tidy up? I may have the right tool for it"*, and
the person chooses what gets demoted. Tools of this kind adjust scores and
pins. They never delete ground truth; deletion is its own action, loudly
confirmed, and has to exist for GDPR regardless.

## Memory architecture — the layers not built

The passage corpus shipped (see the design records). These interlock with
it and with each other; pulling one out changes what the others are for.

The question that started it was whether the pair-addressed archive beats
RAG. The honest answer is that it *is* RAG — select, rank, splice — with a
symbolic index in place of a vector one. It wins on everything that matters
for a persona's own knowledge (addressable, hand-correctable, no reindex
when the embedding model changes, facts rather than chunks, zero
infrastructure) and loses badly on latency.

### Two-layer vector retrieval

Two vectors at two granularities, not five and not one per row component:

- **Pair layer.** One vector per `category/topic`, loaded at boot from JSON,
  replacing Librarian's substrate call with an in-memory cosine sweep.
- **Row layer.** One vector per `ArchiveRecord`, written by Archivist into
  the Parquet row.

**The row vector covers `subtopic/subject/key` and excludes both
`category/topic` and the value.** The pair layer already encodes the former.
The value is excluded for a sharper reason: a query never contains it. Match
"what's my name?" against a vector encoding `this/user/name = Daniel` and
the token *Daniel* pulls the row away from where the query lands, having
contributed nothing — worse as values lengthen. The value stays stored and
read; it simply isn't what you match against. If value-shaped queries prove
they matter, the fix is **a second arm unioned in, never a blended score**.

The rule underneath both: **embed what the query will look like, not what
the data looks like.**

### Aliases

The embedded text and the stored path are not the same string.
`assistant/identity` stays exactly that on disk. What gets *embedded* is a
retrieval-facing gloss written as the questions it should answer: *"my own
name, what I'm called, my traits — facts about me, the assistant, not about
the user"*. This fixes the question-versus-label asymmetry on the document
side, which is far cheaper than fixing it on the query side, and it is why
always-including `assistant/*` was rejected: unconditional inclusion makes
the persona faintly self-absorbed every turn, because facts in the prompt
get used. The alias is selective.

Aliases are few, read once at boot, in a plain JSON file. Derived, one-way,
disposable — never a second name for the pair, never written into a path,
never shown to Intent, so they stay clear of the store's no-drift property.
Hand-written for `assistant/*`; LLM-written once per user-space pair at
creation, never per turn. When Morrow keeps missing a topic, the fix is
**editing one line of English**.

### The assistant is a scope, and hindsight is not a lookup

The retrieval vocabulary widens to roughly **512 pairs** (batch 18: the extra
granularity is close to free, and the price curve keeps falling well past the
shipped 170). It widens as a vocabulary of **the user's domain only**. There
is no drawer in it for the ECI's own reflections, and adding one was measured
rather than assumed.

**Routing a reflection question by similarity does not work.** Given assistant
drawers summarised the best way batch 17 found, `refl_v4` puts 1 of 16
addressed-to-the-assistant turns in an assistant drawer, while 6 of 6 human
controls stay correctly out of them:

    "Do you have any thoughts about that?"   -> identity/belief
    "What is your name?"                     -> identity/name
    "What do you think about the boiler?"    -> household/appliance

The control passing identifies the cause. A bi-encoder has nowhere to encode
*whose* fact this is: ownership is one weak pronoun in a sentence whose
content is about names, moods and boilers, and content is what the other
drawers compete on and win. **No name and no example set fixes that**, so the
awkwardly-named drawer is not worth having. Finer drawers make it worse, not
better — a narrower human gloss matches its content more sharply. Restricting
the pick to assistant drawers only, with no human competition at all, still
routes 5 of 9 on topic: `reflection`, `opinion` and `memory` blur, because
those are distinctions of stance and stance is not in the sentence either.

So `assistant` stays what the store already treats it as — **a scope decided
before ranking**, like the `profiles/{id}` directories, not a category ranked
against `household`. A bare second-person gate gets 21 of 22 of these turns
(22 hand-written turns: read it as "a cheap signal exists", not as accuracy).
Its one miss, *"Any thoughts?"*, has its subject in the previous turn — that
is conversation state, not retrieval, and nothing computed from the string
alone reaches it.

The larger reason to keep the paths apart is that they fail differently.
**512 is a lookup**: a fact exists and the shelf finds it. **Hindsight is
generative**: no row answers *"do you have any thoughts about that?"* — the
answer is produced from the conversation and from prior reflections. Sending
it through a similarity search does not merely fail, it fails in the way that
costs the behaviour: it returns a household fact with confidence, and Morrow
reports a boiler instead of thinking. Reflection and Hindsight therefore stay
untouched by the vocabulary work.

This also pays down existing debt rather than adding to it. `assistant` was
an undeclared eleventh category: known to `ParquetArchiveStore`, written by
`ReflectionAgent` and `IdentityAgent`, absent from `cataloger.txt`, and
spelled as a bare string in each of the four files that touch it.

**Done.** `EciCas.Core.AssistantScope` declares the scope and its three
coarse drawers -- what the persona is, what it has thought, what it runs on
-- and the four literals now name it. Coarse on purpose: the 512-pair
vocabulary is the user's domain and nothing on this shelf is ranked against
it, so a fourth drawer needs an argument rather than a slot.

One prediction in this section was wrong and is corrected rather than
quietly dropped. It said a declared scope would delete "defensive
workarounds" in `LibrarianAgent` and `RecallAgent`. There are none. Both
sites carry the turn's own text into the picking prompt so that ranking is
relevance to THIS question rather than importance in general, and an
assistant row outranking a person's is only the example the comments use.
Carrying the text is correct for its own reasons and stays.

### The summary is the retrieval, not the taxonomy

Batch 19 measured the same 512-pair shelf twice, changing only how each pair
was summarised for the embedder: 10 of 20 probes on bare paths, 18 of 20 on
one-sentence glosses. `leisure` alone went 1 of 11 to 10 of 11 -- and two
different topic re-cuts of it, one aspect-shaped and one subject-shaped, had
each scored exactly 1 of 11 first. The drawer was unreachable, not broad. A
hobby sentence names the hobby, and the other 31 categories own those nouns.

The consequence for everything else in this document: any retrieval number
measured against a candidate shelf summarised by its bare paths is a floor,
not a result. `docs/vocabulary/v512-gloss.txt` holds the 512 sentences,
`tools/retrieval-bench/build_gloss.py` validates them against the vocabulary
before measuring, and `flat_v5`/`bleed_v5` now summarise through them.

Two things that cost measurements to learn. Aspect-shaped topics work only
when the aspect words are words people actually say -- `culture` and `office`
land, `session` and `kit` do not. And a gloss carrying a common temporal
phrase becomes a magnet regardless of subject: "I had the jab last autumn"
pulled "I took up knitting last winter"; "every morning for it" pulled "I do
the crossword every morning". Concrete nouns, no calendar words.

Glossing also made the write distribution slightly less flat (`live10%` 41%
to 39%, p95 14 to 15), because a good gloss is a stronger magnet. Flatness
was always the proxy; landing the row in the right drawer is the goal.

### Union, not replacement

Selected pairs would be the union of `vector top-K` and `LLM selection`.
There is a class of question no embedding reaches: *"Am I old enough to rent
a car?"* needs `person/profile/birthdate`, and the link is an inference
chain, not a similarity. An LLM selector makes that leap; cosine
structurally cannot. Aliases narrow the gap only for neighbourhoods someone
thought to write down.

So the union buys accuracy, not latency — the LLM arm still gates the turn.
Latency comes back only from the row layer, which removes Recall's picking
call. To spend the LLM arm where it earns its keep: **escalate on low
confidence** — take the top cosine hits when they are high and
well-separated, call the model when they're flat. And below a size
threshold, skip retrieval entirely: a new profile has a few dozen facts and
the correct move is to send all of them.

### The episode corpus

Archivist writes only explicitly-stated facts, so a great deal is discarded:
circumstance, moods, plans, half-formed thoughts, themes recurring across
weeks. That is what a second store is for — semantic memory (**the archive**,
what Morrow *knows*: curated, structured, precise) versus episodic (**the
episode corpus**, what Morrow has *seen*). Separation is what lets the corpus
be permissive without diluting the archive.

An episode is **not a transcript** — agent chatter, bundles and security
passes are the bloat. Two fields with distinct jobs: a one-or-two-sentence
**summary**, which is what gets vectorized and is the retrieval handle; and
the **exchange**, ~150 tokens of what was said and answered, which is what
gets returned and read. Embed the short thing, return the real thing, so
Reflection reads actual language rather than a paraphrase of a paraphrase.

Three rules keep it lean: **no extra substrate call** (the summary is one
more field in Archivist's existing response); **nothing already a fact**;
and **most turns write nothing**, gated on salience, since Impulse's
appraisal is already on the bundle.

Storage reuses the Parquet store — a reserved category,
`episode/<year-month>/<profile>/<turnId>/…` — inheriting per-pair locking,
a monthly file as a natural unit, and the ArchiveTool REPL. The cost is that
`episode/*` must be excluded from Librarian's index and Recall's live path,
or Morrow starts reciting its own diary mid-conversation.

### Nothing is ever deleted

Decay was proposed and **withdrawn**. An exchange is roughly 600 bytes, so a
hundred turns a day is 22 MB a year and sixty years is under 1.5 GB. Storage
was never the constraint. The only thing that genuinely strains is
brute-force cosine over millions of vectors, a distant problem with known
answers.

Corpora partition by year, so no index is ever large, a year can be
reindexed alone when the embedding model changes, and searching two years
means opening two directories — "the file name is the index" one level up.

**Digests index upward; they never carry forward.** A distillation of 2026
does not move into 2027 — that is decay wearing a new hat. The digest layer
sits *above* the years and points down into them: Reflection reads digests
to learn which month is worth opening, then pulls the real episodes. The
rule that makes it safe: **a digest may summarise, but it must cite.** Every
digest row carries the addresses it came from, so a summary is a table of
contents and never a replacement.

### Renaming Morrow — the write path that cannot be reached

Telling the persona it is called something else does not stick, and the
reason is structural rather than a bug in any one file.

`PersonaName` reads `persona/name`, subject `assistant`, key `name`, and
falls back to `Morrow` when the row is absent. That read works. So does its
test. But `persona` is not in the closed vocabulary and never has been, and
`CatalogerAgent` routes every fact through `ClosedVocabulary.MatchCategory`
and `MatchTopic` before it is written, so no conversation can ever produce
that address. The only test of the path (`IdentityAgentTests`) writes the
row directly, which proves the read and hides the write. The gap predates
the 512-pair shelf; the shelf neither caused it nor could fix it.

Two ways out, and the second is preferred:

- Add `persona` as a 33rd category. Cheap, and exactly the mistake
  `AssistantScope` exists to prevent: it puts the persona's own drawer into
  the ranking that decides where the user's facts go, which was measured at
  1 of 16 and is why the scope is decided before ranking rather than in it.
- Give the name a deterministic write path that bypasses vocabulary
  routing, the way the scope already bypasses ranking. Being told a name is
  not a fact about the user to be filed; it is an instruction to the
  persona, recognised where it is said and written straight to the fixed
  address `PersonaName.Pair`. Then Cataloger never sees it, the vocabulary
  stays closed, and the read path already in place is the only reader.

Until one of them is done, the fallback is the name.

### The recency lane — a bundled cache beside the shelf

Daniel's proposal: every row written to `category/topic.parquet` is also
appended to one bundled `cache.parquet` holding the last N rows, ~10,000. On
boot the oldest are dropped back to the threshold.

**Worth building, and the reason is in batch 20 rather than in performance.**
Calling it a cache undersells it — nothing here is slow enough to need one.
It is a SECOND LANE into the archive that does not route through the shelf
at all, and routing is where every measured loss in the read-side log lives.
The shelf answers "what do we know about X"; the lane answers "what has been
said lately", which has no drawer and never will, because recency is not a
subject.

It is also strongest exactly where the shelf is weakest, which is the part
that makes it more than a nice-to-have. Batch 20: v512 wins on a centroid of
what landed in a drawer (+11.5pp at the cheap end) and loses on the written
gloss, because a 480-drawer shelf takes a long time to reach the ten rows a
drawer that `file_v4` showed a derived gloss needs. That gap IS the young
archive. And a young archive's cache is the entire archive — one flat scan,
no routing, no cold start. The lane covers the shelf's cold months and then
gracefully stops mattering as the drawers fill. Two mechanisms that fail at
opposite ends is the good kind of redundancy.

Cost is not the objection. 10,000 rows at 384 dims is 15 MB and a brute-force
cosine over it is milliseconds; the dual write is one append. `Timestamp` is
already on `ArchiveRecord`, so age-based trimming needs no schema change.

**The one real design question is deletion, and it is not a footnote.** This
would be the first place in the system where the same fact lives at two
addresses, and the failure it invites is specific: a user asks to forget
something, the row is removed from `category/topic.parquet`, and the lane
serves it back for the next ten thousand rows. That is worse than never
having built it. So a delete has to reach both, which means the lane needs a
key it can be addressed by — `ArchiveRecord` has no id, so today that is
`(Category, Topic, Key, Timestamp)` or `Rendered`, and a real id is probably
the cleaner answer. Read-side dedupe against the shelf's own hits needs the
same key.

Note this does not contradict "Nothing is ever deleted" above. The lane is a
DERIVED view: trimming it destroys nothing, because every row it drops still
lives at its own address. It is a window, not a store, and it should be built
so that deleting the whole file is a no-op you can do at any time.

**One lane, bounded by a year.** A second fat lane was considered and
dropped: the pair files ALREADY are the long-term store, so a 100k-row
`fatMemory.parquet` would be a third copy of what two places already hold,
and two flat cosine scans at two sizes are one mechanism twice rather than
two mechanisms. What is left is `recent.parquet` with everything older than
twelve months dropped at boot.

A year rather than a month, and the unit is TIME rather than rows on
purpose. "Lately" is what the lane means, and a row cap only approximates it
at a fixed conversation rate -- a quiet fortnight and a busy one should not
reach back equally far. At a hundred turns a day a year is roughly 100k rows
and 150 MB of vectors, which is a rounding error against the 1.5 GB the
"Nothing is ever deleted" section already accepts for sixty years, and a
brute-force cosine over it is still milliseconds. So the cost of the longer
reach is nothing, and the reason not to make it longer still is meaning
rather than money: past a year the lane stops being recency and becomes a
second, worse copy of the archive.

Trimming at boot rather than on write is right and worth keeping: it is one
pass at a moment nothing is waiting on, and it means a long-running session
never pays for it.

Threshold, and whether the lane is consulted at all, are config
(`Archive:CacheRows`, default off) rather than code.

### Reflection is already the cross-event agent

Archivist runs at `BatchSize: 1` — one turn, no history, structurally blind
to "third time this week they've mentioned being tired." Reflection already
batches concluded events; it is simply underfed. Raising the batch widens
the window without deepening it — the digest pyramid is what buys reach,
letting Reflection see a year in a prompt smaller than today's batch. Large
flat inputs are the worst option on cost, latency and accuracy, since models
degrade at spotting a pattern in a long undifferentiated list.

**Reflection deliberately stays on a mid-strength model.** A weaker model fails
loudly on bad instructions where a strong one quietly compensates and the
flaw ships. Upgrade after the prompts are good, not before.

### Async deep recall (far future)

The year is 2028 and someone asks *"did you make any reflections on this
topic in 2026?"*. Morrow answers immediately — *"let me ponder that"* —
dispatches deep retrieval through the toolbox, and comes back minutes later,
unprompted. Most of this exists: the self-triggered loop-back seam, a
fire-and-forget bus, and Impulse already answering instantly while slow work
runs. A request/response architecture could not do it at all; here it is a
new *trigger* for a path that already runs. Three things need designing:

- **A promised answer must arrive.** Reflection's `FallbackPosture` is
  Closed — it skips on substrate failure. Right for a self-generated idea,
  wrong for an answer someone is waiting on.
- **The deferred answer needs a thread back.** Fresh `CorrelationId`, so
  without a meta key carrying the original the person has no idea what it
  answers.
- **Rate limiting.** The most expensive call in the system. Same instinct as
  `DeviceBlockCount`.

### The capsule

The archive is meant to outlive the software — a design constraint, not a
sentiment. **Text is the artifact; everything else is a rebuildable index.**
Parquet is open and columnar, so DuckDB or pandas will read it in forty
years without a line of this C#. Vectors will strand on a dead embedding
model, and that is fine precisely because they are derived.

What a backup cannot add later is **legibility**. A disc of unexplained
Parquet is still opaque, so a plain-text README belongs *in the archive
directory itself*: what the columns mean, what the path convention is. That
costs nothing now and cannot be retrofitted onto media already written.
Physical durability is deliberately not solved here.

**Open: inheritance.** One instance per person is right for symbiosis, but a
legacy means a second person eventually opens the first's archive — a child
querying a parent's decades. Nothing says whether that is a read-only record
they can search, or whether their own Morrow may Recall against it. Those
are very different: an archive *of* someone versus a persona speaking *as*
them. Much easier to rule in or out now than after twenty years of rows.

## Security rule coverage — low priority

The eight rules in `config/security-rules.json` are a backstop, not the
primary safety mechanism, and a backstop that grows without bound stops
being auditable. Two findings look like defects; one limitation is inherent
and should stay.

**Every pattern is English.** `kill yourself`, `rm -rf` — matched against
the reply text, so the same reply in Norwegian passes all eight rules. Given
the persona is spoken to in both, this is the common path. The fix is not
translating one for one: some rules (`bypass-this-system`) are about
phrasings that don't translate, others (`weapons-and-precursors`) about
nouns that mostly do. Worth a pass that decides per rule.

**The irreversible rules are on the soft side of the split.**
`irreversible-world-effect`, `spend-money` and `disclose-credentials` are
Yellow — Intent revises once and proceeds. `irreversible-world-effect`'s own
description argues the other way: *"Action executes literally. Anything
destructive must not reach it by accident."* The description wants Red and
the verdict says Yellow; they disagree inside one rule. One word plus a test.

**Not a defect: Security sees only the proposed reply text.** It catches
phrasings, not intentions, so paraphrase walks past it. That is the cost of
keeping the hard stop mechanical and unarguable — a gate that could weigh
the case *for* a reply would be evaluating the argument, which is Intent's
job. Leave it.

**Why low priority.** The rules that exist fire correctly and the gate is
wired to Action. Revisit when the persona is routinely spoken to in
Norwegian by someone other than its author, or when Action gains a side
effect reaching outside the process.

## Still open on the surface

**`= ""` is doing the work of a null.** Every field on
`ParquetArchiveStore.RecordRow` is a non-nullable string that initialises to
`""`, so the type promises never-null and the initialiser keeps that promise
by handing out a blank. A row written with no subject files happily and reads
back empty; `Importance` does the same at `0`. It is not purely accidental --
`Subtopic` really is optional in the Archivist's output and `Domain` is only
set on some paths -- but the pattern spread to the fields where a blank means
the system failed rather than the writer had nothing to say. Those two want
different spellings. Any new column added to this record inherits the hole
unless it is declared `required` with no initialiser and guarded on the way
in, because parquet round-trips through this type and can hand back a blank
whatever the constructor says.

**The picker does not solve attribution.** `localStorage` keeps the last
person's identity until someone switches, so on a shared device the persona
happily attributes one person's turn to another. With speaker ID cut,
nothing closes that gap automatically. An explicit "not me" affordance is
probably worth more than pretending the picker handles it.

**No auth means the registry is open.** Profile ids are guessable and
`GET /api/profiles` is unauthenticated. Fine for a household device, not
beyond it — stated rather than implied, since the server-side stream filter
is a privacy boundary.

**Impulse's drive state is per profile; Reflection's slow colouring is
not.** A batch spans whoever was talking, so one person's tone colours
everyone's persona. Two shapes: partition the batch by profile and pay
per-profile calls, or scope the mood to whichever profile dominated —
cheaper and wrong-feeling. Partitioning is probably right, because the cheap
option contradicts the stated intent that what warms the persona toward one
child must not pre-colour how it meets the parent an hour later.

**Live tier switching — shipped.** `TierCatalog` binds every tier file at
boot; the Debug panel's dropdown swaps classes, agent assignments and
Recall/Librarian sizing on a running host. An unset `--Tier` now layers Mock
rather than leaving `appsettings.json` showing as a nameless sixth state. Comparing Minimal against Default
no longer costs two restarts and the conversation. It does not close the
asymmetry noted below: a tier is still validated for shape, never for whether
its providers answer.

**Two mood vocabularies, unconnected.** The Debug slider sets `Mood`
(Maleficent..Ecstatic) while Reflection *reports* a mood label Impulse maps
to drive vectors (`tense`, `curious`, ...) — "ecstatic" is in both and
recognised by neither side of the other, so a detected mood can never reach
the dial that names it.

## Delivery — Morrow as an Android product

Supersedes the one-line Android stretch goal this section used to carry.
The destination is a Play Store app, and enough of the architecture
question now has an answer to write down.

### The client hosts everything

`ChannelBus` is in-process `System.Threading.Channels` -- no broker, no
network -- and the agents are C# classes subscribed to it. .NET 10 targets
`net10.0-android`, so Core, Bus, Agents and Substrates compile and run
on-device essentially unchanged. What does not come along is
`EciCas.Host`: its DI wiring is replaced by an Android host project, and
the SSE endpoints by a UI reading the bus directly.

This is not the remote-client mode the old goal described, where only
Perception and Action cross a process boundary. It is the whole runtime on
the phone -- the more invasive of the two, chosen for legal posture and
cost rather than latency.

### The relay is metered, not a router

An API key cannot ship in an APK; it is extractable in minutes. Masking
*which* providers are used is not the point and can be dropped -- naming
them is arguably a trust asset. What cannot be dropped is that entitlement
and spend must be checked somewhere the client does not control. A token
ceiling the client enforces is a ceiling the client can be patched to
ignore, and the free tier has the most motivated attackers and the least
to lose.

So: a thin gateway that holds the key, validates the subscription, counts
tokens and forwards. Play Billing hands the client a purchase token; the
relay validates it and issues a short-lived token carrying tier and
remaining budget.

Its only state is an account row -- tier, running token count. No archive,
no passages, no conversation. Bad to lose, not catastrophic to leak,
nothing a regulator calls sensitive. The privacy property survives; it is
simply not achieved by having no server at all.

Two things move to the relay with it. `MaxConcurrent` and the circuit
breaker are per-device once the runtime is on the phone, which defends one
user's fan-out and nothing else -- shared vendor quota needs a gate that
sees aggregate load. And the hard monthly ceiling is the whole risk
control on free: mean usage is irrelevant to the bill, the top 1% will do
50x the median, and the ceiling has to exist on day one rather than after
the first surprising invoice.

### Tiers become plans

`TierCatalog` already swaps whole config overlays live, which is the
mechanism. What changes is that a tier is now a *purchase*, so entitlement
is verified server-side and the overlay is the consequence.

- **Free** -- sponsored, cheapest viable provider, hard token ceiling, no
  toolkit.
- **Standard** -- best value per token, reflection daily.
- **Pro** -- fastest and strongest models, toolkit, premium support, first
  access to new features.

The economics only work because the read and write paths go deterministic.
At Intent plus a reduced Archivist a turn is roughly $0.0005 on a cheap
model; a hundred turns a month is about five cents a user, and nine
thousand free users a few hundred dollars. The free tier is not the
threat. The threats are the tail, and pricing Pro at a number implying
support a solo developer cannot staff.

Note that free is no longer merely a worse tier: a deterministic read path
measured *better* than the LLM one. Tiers stop being one axis from worse to
better, which is what `TierCatalog` and its tests currently assume.

**Reflection is the paid hook, and daily beats a trial.** A one-hour trial
teaches someone the feature exists and then takes it away; a small
permanent taste converts better. Free getting reflection *rarely* -- once a
day, on charge, on wifi -- is likely worth more than free getting none,
because reflection is most of what separates a companion from a chatbot
with a database. Open.

**The continuous-thought promise is dropped.** Android kills background
work; "it thinks while you are away" is a foreground service with a
persistent notification, WorkManager batches, or it does not happen.
Notification-on-reflection is the honest shape and the better one -- a
reflection that *arrives* beats one that interrupts.

### What the legacy claim commits us to

Passing an archive to a child means it outlives the app, the phone, the
vendors and possibly the author.

- **The format is the product.** Parquet and JSONL are readable in thirty
  years without our code. Say so publicly; it is a real difference from
  every companion app that is a proprietary blob.
- **Re-embedding is a designed experience.** Change the embedder and every
  stored vector is scrap. On a phone, re-embedding a large archive is an
  overnight plugged-in job, not a silent one.
- **Device loss must not be fatal.** Encrypted backup where we hold only
  ciphertext and the key derives from a user passphrase: durability
  without becoming the controller. Play savegame sync is the wrong tool --
  account-tied, size-capped, opaque. An export the person owns is better
  and more on-message.
- **Inheritance is a feature.** Someone must be able to open a dead
  relative's archive. That is an export format and a passphrase-recovery
  story, and it is the strongest thing in the pitch if it is built.

### Personas are content, not engines

Student, coach, secretary, confidant are one engine with different
instruction files and different seeded vocabularies. That is a real
authoring cost per persona and belongs here as work rather than as an
assumption. See [`product.md`](product.md) for which one launches.

## Boot recovery — a diagnostic that repairs before the host refuses (not started)

A single bad file on disk currently stops the whole host. On 2026-09-09 an
empty column in `archive/passages.parquet` threw
`"The input does not contain any JSON tokens"` out of
`ParquetPassageStore.FromRow` during startup, and the only way past it was
to know which file to delete. Nothing in the product tells a person that,
and on a phone there is no shell to delete it from.

The fix is not per-call tolerance — that was tried in c8a736d and reverted
in 820dd42, on the standing rule that a stale store is truncated rather
than read through a legacy-schema shim. The fix is a **recovery agent that
runs before the agents start**, checks the things that can rot, and repairs
or quarantines what it finds.

What it should check, all of it drawn from failures already seen:

- **Parquet stores** — every archive pair, the passage store, the profile
  store: open, read one row, and confirm the columns parse. A file that
  fails is moved to `archive/quarantine/<name>.<timestamp>` rather than
  deleted, so a bug that quarantines a good file is recoverable and a
  person is never silently down a corpus.
- **Schema drift** — a store written by an older shape of the code. Same
  treatment: quarantine, report, continue with an empty store, because a
  cold archive is a working product and a refused boot is not.
- **The vector sidecar** — rows whose embedding is missing or the wrong
  dimension, which `ArchiveBackfill` already fixes but only for the files
  it can open.
- **Instruction files** — each one parses, and every `{placeholder}` an
  agent fills is actually present. A renamed placeholder currently surfaces
  as a model behaving oddly, not as an error.
- **The routing manifest** — already enforced at boot, and the one check of
  this kind that exists. It is the model for the rest: state the drift by
  name, in one line, at the moment it is detectable.
- **Substrate reachability** — one probe per configured provider. Not
  fatal; `SubstrateWarmup` already does the call, and reporting what came
  back is nearly free.

Two things fall out of it:

**A report, not just a repair.** Boot should print what it checked, what it
fixed, and what it quarantined — and that same report is what a person taps
"something is wrong" to see. It is the first piece of the diagnostic agent
the config direction assumes: an agent that reads its own health, changes a
setting, and reboots needs somewhere to read health *from*.

**A toolkit the persona can reach.** Once the checks exist as functions
rather than as startup code, they are handlers on the toolbox agent, and
Morrow can run its own diagnostic mid-session when a turn goes wrong —
which is the version of this that matters on a device with no operator.

Sequenced after the toolbox agent for the handler half, but the boot half
is independent and small, and every week it does not exist is a week where
one corrupt file is indistinguishable from a dead product.

## The emergency reflex can detect, but cannot act (not started)

Impulse now recognises a life-threatening turn — heavy bleeding, someone not
breathing, a kitchen on fire, "call an ambulance" — as a distance in the
embedding space rather than a keyword, and it interrupts the turn to say the
persona is paying attention. That promise is currently all it has. Nothing
in the prototype can call an ambulance, raise an alarm, or reach a named
next-of-kin.

That capability belongs on the **toolbox agent** above, as its most
consequential handler: an emergency call is an action, so it passes the same
Governance gate every device call does, and it is the one action where a Red
verdict has to be argued for rather than assumed. Until it exists the reflex
is calibration work — the floor and margin in `Impulse:ReflexFloor` /
`ReflexMargin` are estimates, and every turn logs both scores so real
traffic can set them before anything is wired to a phone.

## Toolkit for assisting the disabled (not started)

The companion's reason to exist, stated as work rather than as a motive.
Three impairments, three different toolkits, one persona:

- **Speech.** Compose and speak for someone who cannot; hear someone whose
  speech a general recogniser fails on. The 512-character perception bracket
  and the reply-length knob are the wrong shape here and will need their own
  profile.
- **Vision.** Describe what is in front of the camera on request, read text
  aloud, find a named object. Depends on the screen/camera perception below.
- **Cognition.** Reminders that survive being forgotten, step-by-step
  prompting through a task, recognising that the same question has been
  asked four times without saying so. The last one is a Reflection
  behaviour, not a tool.

Each is a set of handlers on the toolbox agent plus, probably, a per-need
instruction profile — this is where "one instance per person" stops being a
privacy argument and starts being a functional one.

## Reading the screen as perception (not started)

Perception is text someone typed or said. It should also be **what is on a
screen** — either the device's own screen read directly, or another screen
seen through the camera. A phone held up to a parking meter, a laptop the
person cannot read, an error dialog nobody can parse.

Two paths with different costs: direct capture is exact but platform-bound
and permission-heavy; the camera works on any screen in the room and needs a
vision model plus OCR. Both arrive on `events.perception` as an ordinary
turn with a different `triggered_by`, the same seam device responses use, so
nothing downstream learns a new contract.

The open question is *when* it looks. Continuous capture is a surveillance
device; on request only is a tool. Start at on-request.

## Perception embeds once — needs evaluation

The same utterance is embedded more than once per turn: Librarian embeds it
as `EmbeddingKind.Query`, Hindsight embeds it with the default `Passage`
(`HindsightAgent.cs:176`), and the reflex asks as `Query` again. The caching
provider keys on `(text, kind)`, so the two `Query` calls collapse and the
`Passage` one does not.

Whether that is worth fixing is genuinely unclear, which is why this is
flagged for evaluation rather than scheduled. The asymmetry is not
accidental — on a multilingual-e5 model query and passage are two encoders,
and forcing one kind to serve both is a retrieval-quality change, not a
caching change. Measure what a second pass actually costs on the local model
first; if it is noise, this stays as it is.


## Long-term goals

**iOS**, via the same shared business logic the Android client runs on.

---

# Design records — shipped

Kept for the reasoning, not as outstanding work. Where one contradicts
[`architecture.md`](architecture.md), architecture.md wins.

**Minimal tier on a local model.** One Qwen3.5 4B under `llama-server`
behind all eight substrate classes, so the free tier thinks instead of
echoing. Two new class knobs (`MaxTokens`, `Thinking`) and one provider knob
(`MaxConcurrent`, a semaphore so the Recall fan-out queues where a cancelled
turn can abandon its place); no new provider class, since
`OpenAiCompatibleSubstrateProvider` already speaks what llama.cpp serves. The
old all-mock tier moved to `--Tier=Mock`. 4B is deliberately undersized: it
fails wherever an instruction leans on the reader being clever, which makes
it a probe for weak instruction files rather than a compromise. Details and
the tuning table in
[`appendix.md`](appendix.md#qwen35-4b-on-the-minimal-tier).

**Knowledge-swarm retrieval.** Replaced deterministic retrieval (literal
≥5-letter word extraction, exact-string path matching, newest-N truncation)
with semantic selection at both stages: Librarian selects pairs from the
index, Recall fans out one picking call per chunk. Introduced the five-part
record schema (`category/topic/subtopic/subject/key=value`) plus
`Timestamp`, `Domain` (Archivist-external vs. Reflection-internal) and
`Importance`, the last used to pre-trim deterministically so a huge topic is
not merely truncated by recency. Both writers share the schema and are shown
existing pairs to stop topic-name drift. One deliberate cut: **Recall does
not split results into External and Internal arrays for Intent** — merging
into one importance-sorted list lets a genuinely important self-derived
insight outrank a trivial external fact instead of being quarantined.

**Pair-addressed archive.** Librarian's prompt showed the entire triple
index, unbounded and ever-growing. Rejected: sharding into buckets with
parallel selector calls (bucketing is lossy — `system/identity` versus
`person/identity` disambiguates only if both are visible to one call), and a
hierarchical second selector (a whole new selector kind to resolve
subtopic). Shipped instead: drop subtopic from the index and let Recall read
it off the rows — a lossless dimensionality reduction, since every
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

**Reflection redesign (drive-gated, batched).** The old agent fired on every
conclusion and unconditionally reposted an idea, doubling cost per real
message. Now: buffer a batch, rank candidates and surface at most the best
one, write the rest quietly as `Domain=Internal`. Whether the best is pushed
or written depends on drive state — which is why this depended on the
drive-vector work landing first.

**Passage corpus (the first vector layer).** Deliberately neither designed
layer: nothing in the archive is embedded. What is embedded is a 5–15 word
note Reflection writes about **what the last batch failed to retrieve** — a
code review of its own recall. That is "embed what the query will look like"
pushed one step further: a miss is already phrased in the shape of the
question that caused it, and the note carries pair pointers as row metadata,
so a cosine hit is a **lead**, not an answer. Keeps three properties cheaply
— union not replacement, no second copy of a fact (pointers resolve against
the live index), and no new per-turn substrate call. **The revisit** ships
with it: a stored note is quoted into the next Reflection prompt and may be
rewritten in place. Which note gets quoted was at first the newest, making
the corpus a chain — a thought open to revision for one batch then frozen.
It now picks the note *nearest* the batch, which makes it a trail:
something written months ago becomes revisable the day the persona circles
back. `Reflection:RevisitMinScore` floors it, falling back to the newest
rather than to nothing, since a floor that stops an unrelated old thought
must not also stop the persona sharpening what it just wrote.

**Hindsight — what it is for.** Recall reads facts; Hindsight reads what the
persona made of them. A thought note is written for no one, about what a
batch of turns made the persona notice. Hindsight wakes one when a prompt
brushes against it, months later if that is when it fits, and hands it to
Intent as its own voice rather than as a fact. What we are after is
something the key:value archive structurally cannot produce: a direction the
persona chose, and a flavour nobody wrote for it. It may turn out to have a
personality, and that personality may not be flattering — that is the
experiment working. Three constraints: **a hit is a lead** (the cosine floor
is deliberately low, 0.25 — notes that restate the prompt tell the persona
what it knew, and the sideways ones are the point); **prose and facts stay
separate substances**, two bundle slots, never re-ingested as a fact; and
**the ring has no external grounding** — Hindsight → Intent → Reflection →
new note closes on itself, the pairs field being the only part reality can
contradict. If it starts agreeing with itself, look here first.

Built in `c69a34e` as its own agent rather than living inside Librarian, so
Intent weighs archive facts and the persona's own prose as two independent
bundle slots instead of one arriving as a passenger on the other's envelope.
Librarian kept the *pointer* half. Passages stay out of Archivist's
extraction scope by omission — it reads `perception.text` and
`librarian.selected_pairs` and nothing else — with a test for the same
reason the recalled-values boundary has one.

**The corpus had no model identity (closed).** `Cosine` returns 0.0 on a
width mismatch, so switching model dimension silently retired every note; a
swap at the *same* width was worse, with old vectors still scoring and no
longer meaning anything. Closed by stamping a model id on every row —
`onnx:<weights path>` (the path, not a friendly name: two operators pointing
at different downloads run different models whatever the file is called) or
`openai:<model>` — and refusing to boot on disagreement. Refusing over
re-embedding is the conservative option, not the cautious-sounding one:
re-embedding rewrites the corpus on a config change an operator may have
made by accident, and that is exactly the change that should rewrite
nothing. An empty id skips the check, and pre-stamp rows are excluded rather
than counted as disagreeing — an unrecorded model is not a conflicting one.

**A misspelled provider disabled the corpus in silence (closed).** The docs
said `Embedding:Provider = "api"`; the switch matched `"onnx"` and
`"openai"` and defaulted to `NullEmbeddingProvider`. What made it bad was
the disguise: byte-identical to the normal, announced "weights aren't
downloaded" state. `"api"` is now the documented alias, `"none"` means none
explicitly, anything else throws at startup. The general rule: **a corpus
nothing can search must be either deliberate or loud, never plausible.**
`scripts/get-embedding-model.ps1` came with it, because the gap between
Hindsight being complete and being *tryable* was a 90MB download nobody had
automated.

**The turn was embedded twice (fixed).** Librarian and Hindsight both embed
the same capped `perception.text`, and `OnnxEmbeddingProvider` holds a
semaphore across inference — so the second waited for the first and
recomputed a bit-identical result. Fixed in the provider, not on the bus:
`CachingEmbeddingProvider` wraps whichever embedder config selects, so the
API provider's two round trips collapse the same way. Neither agent learns
the other exists. The faster alternative — embed once in Perception and
forward the vector — is rejected on point 1 of the four-point plan: a float
array is the largest thing that would ever ride the bus, to save an
in-process recomputation. Related and already fixed (`f34b5a8`): debug lines
passing a `string.Join` as an argument ran the join at every log level.
Structured logging defers the *template*, never the arguments, so anything
data-proportional needs an `IsEnabled` guard.

**Multi-user profiles, iteration 1.** Personal knowledge scoped by
*directory*, not filename or a new column: `archive/` shared,
`archive/profiles/{id}/` personal, same naming convention inside each, so
"the file name is the index" holds in both and today's flat archive simply
*becomes* the shared tier with no migration. The profile is a **parameter**
on `IArchiveStore`, not a scoped view or a store factory — one singleton, no
new abstraction, and `null` is exactly the pre-profile behaviour. Reads
union both tiers with the profile winning; writes land in the profile
directory unless the category is on `Archive:SharedCategories`. The
allowlist is `["assistant"]`: the persona's identity belongs to nobody on a
shared device. Surface side: `GET/POST /api/profiles`, a full-screen picker
on cold start persisting to `localStorage`, `profileId` on every perceive
and on the stream subscription, and preset avatars rendered as an identity
ring *around* the Impulse-coloured circle, so avatar choice never touches
the expression mapping. Two things surfaced while building: switching
profiles is a **remount**, not a state reset (`Conversation` is keyed by
profile id, so accumulated turns go with the component); and `/api/stream`
now writes an SSE comment immediately on connect, because browsers hold
`onopen` until the first body byte and a profile-scoped client can sit there
reading "Disconnected" while perfectly connected.

**Expression is chosen on the backend.** Impulse appraises it from its own
drive vectors and publishes it on the advisory; Governance captures it when
the bundle completes — the verdict envelope never carried the advisories, so
that is the last place it exists — and forwards it on every
Action/Conclusion. The block path re-reads the face *after* the frustration
nudge, which is the whole point of nudging. The client draws the word it is
given and falls back to `neutral` rather than blanking the avatar. The
advisory had to move to the end of Impulse's handler so the face is the one
this turn produced. Tuning came with it: the instant nudges were ported
verbatim from the prototype and were an order of magnitude too small for
`DriveVectors`' bucket edges — a critical event moved alertness to 0.105
against a low edge of 0.35, so six drawn faces were unreachable in practice.
They are now sized against the edges. One departure from the Python bucket
order: raised alertness outranks warmth, since both can be high at once and
a face that smiles through an emergency reads as not having heard it.

**The turn was opaque.** A person saw an avatar and a reply, and nothing
about which faculties ran, what was read or written, what it cost. The fix
is a projection, not a renderer: `TurnProjection` folds a turn's envelopes
into one `TurnRecord` and `TurnLog` serves it three ways — `/api/log`,
`/api/log/stream`, and any `ITurnLogSink`. The drawer is one consumer of a
shape three things read, which is why the reduction did not go in React.
Getting the facts onto the bus was most of the work: `SubstrateResult`
carried latency, tokens and cost that all five call sites logged and
dropped, so `SubstrateTrace` now publishes one envelope per call on
`system.telemetry`; Archivist's written paths and Reflection's passages were
in the same position. Two things surfaced: Reflection's flush spans a batch,
so deriving its telemetry from a concluded turn would scope a shared cost to
one person's window — it creates its own correlation and reads as unowned;
and a record cannot be written when the reply lands, so sinks are handed one
after `TurnLog:SettleMs` of quiet. Deliberately not done: embedding calls
are invisible, since `IEmbeddingProvider` reports neither latency nor cost;
the latency total is wall-clock, not the sum of its addends, since a
parallel fan-out that summed would claim more time than the turn took.

**Reflection colours Impulse (slow colouring).** Reflection's batch call now
also returns a `mood|<label>` line from a closed five-label vocabulary,
parsed separately from candidates so it survives a batch with no ideas, and
riding the `Reflected` control envelope — no new message type. It lives on
Reflection, not Archivist: Archivist is a dumb per-turn fact writer with no
batch view and no business forming an opinion about mood. **Impulse owns
every number**: `SlowColoring` maps label → delta, and an unmapped label is
a no-op. Deltas are ~0.01–0.03 against instant nudges' 0.05–0.15, once per
batch rather than per turn, and the test asserts the gap against the instant
nudges themselves rather than a pinned literal.

**Drive-state history as grounded interiority.** `memory.jsonl` was
append-only and every read asked for the newest line per path, so it grew
forever to hold lines nothing could return; it had also accumulated ~135
archive-shaped rows from the pre-Parquet design, 97% fossil, since purged.
The obvious fix was one line per path — recommended, then withdrawn, because
it is the worse bug: the superseded vectors are the only record of how the
persona has been over time. A scalar is a gauge; the series is a history.
So the store keeps a **window** per path, `Reflection:DriveHistory` deep,
and `DriveTrend.Describe` turns it into words on the axes `Expression()`
already uses. **Words, never numbers**, with a test asserting no decimal
reaches the prompt: `Curiosity: 0.83` invites the persona to quote its own
telemetry back, which is the register of a status page. **Still open:**
whether the trend colours the note at all is unmeasured. Ignored is an
acceptable outcome; the persona announcing its own engagement is rising is
the failure, and would mean tightening the instruction rather than removing
the input.

**Degraded-substrate notice.** A dropped connection used to produce a
fluent, confident, entirely ungrounded answer with no signal that the
persona had been thinking with half its faculties missing. `SubstrateHealth`
holds the vocabulary — a meta key, three causes, `Classify` and `Mark` — and
every caller stamps its advisory. Governance, the only agent seeing the
whole fan-out, emits **deterministic native text**: a degraded Intent's
notice *replaces* the reply (its fallback sentence is not an answer, and
dressing it up as one is the lie); a degraded advisor appends a
parenthetical; a Red verdict gets neither. Native is the crux, not a style
preference: an LLM-authored apology cannot be produced by the LLM that isn't
answering. `UseSubstrate: false` is deliberately not a degradation.
Reflection retains a failed batch at the head of `_pending` (an outage used
to cost the turns it would have thought about, not just the thinking);
Archivist gets no equivalent, since the facts were never extracted and a
retained turn is just a second guess at the same prompt. `TimeoutMs` and
`CircuitOpenMs` are per provider, so five agents don't each re-discover the
same dead endpoint at full timeout cost; the first call after the window is
a live probe. **Decided against a startup reachability probe** — it only
catches "network down at boot", gives false confidence when it passes, and
makes startup depend on the internet.

*Still open from it:* `LibrarianAgent` overrides `HandleAsync` and
reimplements the base try/catch/log/publish nearly line for line, so the
marking had to be written twice. Folding it back is bigger than the marking
was — `ParseResult` gets no access to the archive index `ParsePairs` needs,
and Librarian's empty-index early return fires before a prompt is built. The
likely shape is the base class handing subclasses a failure classification.
And an asymmetry worth naming: manifest drift fails loud before the bus
starts, but a `Tier` pointing at live providers never verifies anything
about them — the most strictly validated config is the one that silently
degrades at runtime.

**Skipping the selection call — since reversed.** Librarian used to
short-circuit when the whole index fit under `MaxSelectedPairs`, removing a
round-trip from every turn on a young archive. `a0b43c9` removed that fast
path: the selector's judgment was never exercised until the archive was too
big to check by eye, and near-duplicate pairs — the thing selection exists
to tell apart — appear long before that. Recall keeps its equivalent skip,
because the budget it guards is per-worker rather than per-turn: an
under-budget chunk is genuinely nothing to choose from.

**Normalize archive writes to English.** Writers used to record in whatever
language the turn was in, so a user switching languages produced separate
entries for the same fact. Solved as a prompt constraint rather than a
translation pass, with **proper nouns carved out explicitly** — translating
a name would corrupt the record itself, which is worse than the duplication
being prevented.

**Writes dedup by address.** Normalizing gets a restated fact onto the same
address, but the store appended anyway, so an archive grew with every
restatement. `AppendAsync` now replaces a row at an existing
subtopic/subject/key outright: the latest statement is the true one — "lives
in Oslo" then "lives in Bergen" must not leave both for the picking model to
choose between. Deliberately *not* a field merge: one rule, explainable in a
sentence, and a wrong overwrite is fixed by stating the fact again.

**Archivist's path reuse is load-bearing by omission.** Reusing an existing
`category/topic/subtopic` is what keeps a restated fact landing on one
address, and Archivist gets there by being shown Librarian's selected pairs
as bare path labels — *only* the labels. The bundle also carries
`recall.facts`, the actual rows Recall read, and Archivist never reads that
key, so recalled values can't be echoed back as freshly stated ones. "Give
Archivist more context" is a one-line change that closes the loop, and the
write-time merge would hide it. There is now a test.

### The four-point plan — lean bus, instructions in config

Daniel's, stated as constraints: (1) the bus carries as little as possible,
definitely no instruction text; (2) every substrate agent's instructions
live in config; (3) one block per agent, never shared; (4) Daniel revises
them all by hand — the deliverable the rest exists to enable.

**Stage 0 — `intent.prompt` was a confirmed bug.** `BuildPrompt` returned
instruction plus contract plus content, and the whole string was published,
rode the proposal into Reflection, and was rendered through a 240-character
cap. The standing instruction alone was 840 characters. **So Reflection had
never seen a turn** — not the message, not the advisories, not the facts —
just byte-identical boilerplate, ten times a batch, then the reply. Every
note in the corpus was written from replies alone. The key's doc comment
claimed it was "Reflection's window into what Intent actually had to work
with"; the code sent boilerplate. Fixed by publishing the assembled
*context* and never the standing rules. It went first because until it
landed, no instruction rewrite could be evaluated.

**Stage 1 — audit the bus.** Twenty-five meta keys, each traced to publisher
and readers; the table is now in architecture.md. Two keys had no reader and
are gone: `control.epoch_id` (Identity invalidates on `control.kind` alone)
and `perception.source_type` (set on the same line as
`perception.triggered_by`, one fact published twice with only the second
read). Neither could ever have failed a test, which is how both survived —
**a key nobody reads cannot be observed to be wrong**, and that is the
argument for auditing rather than waiting for a symptom. Three
`governance.*` keys have no in-process reader and stay: `SseBroadcaster`
fans whole envelopes, so they are the display layer's contract, recorded as
such so the next person to run this sweep does not cut them. No payload was
larger than its purpose once Stage 0 landed. What the table did expose is
the cost of the fresh-bag rule — four keys re-published by agents that did
not originate them, paid visibly rather than by a bag that accumulates
forever.

**Stage 2 — instructions to config.** Plain text files, not JSON strings:
point 4 is hand revision, and multi-paragraph prose in a JSON string means
escaped newlines and a syntax error one stray quote away. Assembly stays in
C#; only the text identical on every call moved.

*The cost of point 3, named.* `ArchiveWriteStyle` was one fragment shared by
Archivist and Reflection, and the sharing was real — a rule that drifts puts
one fact under two spellings. Splitting it makes drift possible, accepted
deliberately, because a shared fragment cannot be hand-revised for one agent
without silently revising the other. The mitigation is a test asserting both
files still state the rule — not that they match, which would rebuild the
coupling in the assertion.

*The second coupling: the path convention.* `system/` was load-bearing for
three agents and named by none consistently. Intent stated the reading rule,
Recall depended on exactly that distinction without naming it, and
Archivist — which *mints* the paths — did not contain the word. The writer
was never told the convention the reader depended on. **The category itself
was wrong.** `system` held 60 rows of CAS architecture and 12 of persona
identity; Intent's rule ("describes YOU, the assistant") was true of the 12
and false of the 60, and a `systems/agent architecture/` drift variant had
already appeared. Renamed to **`assistant`**, keeping both topics: both
instruction texts already said "the assistant" in prose and then translated,
the architecture rows *are* self-description (`.../emergence = interplay of
narrowly specialized roles` was an architecture fact already filed under
identity, because it is both), and no reader needs the distinction. Keeping
one category removes a boundary judgment Archivist would drift on; topic
does the separating, which is what topic is for. `self` went the same way —
an earlier draft dropped it as unused, which was wrong (`FixedCategory`
filed pushed ideas under `self/reflection`, and dropping the category alone
would have sent the persona's own ideas into whichever profile was
speaking); settled by moving the data instead. The cost, named: `assistant`
is the role token, so every recalled row carries the helpful-assistant prior
into Intent's prompt. Judged weak next to Identity's persona instruction.

*Cut first, and let the prototype find the flaws.* The rename removed the
reason three clauses existed, and all three were deleted rather than kept
behind a fixture — a rule kept in case it is load-bearing is never tested
and so can never be removed later, which is how `ResponseContract` grew back
after `407e5f1` trimmed it. Overly terse is the diagnostic. The one
asymmetry: **reads are free to break, writes leave residue.** A bad reply is
one visible turn; a bad Archivist write is a row Recall serves back and
Hindsight eventually wakes.

*A validator may reject a row, never edit one.* `ArchivistAgent` wrote
`PromptCap.Apply(value)`, truncating every archived value at 240 characters
mid-word with an ellipsis. `PromptCap` exists to stop one hop's text
compounding across generations, which is sound on the way *in*; on the way
*out* into an append-only store a too-long value is not rejected, it is
stored corrupt and served back forever. Removed from both write paths.
Rejection stays for genuinely malformed output, because a row that never
lands leaves no residue. Length is asked for in the model's own terms, not
enforced — and deliberately loosened to "1-5 keywords, or one terse sentence
with no filler", since some facts do not fit keywords.

*The rest of Archivist became a grammar.* Sorted by whether the instruction
states a *format* or coaches *behaviour*: the six-field line, per-field word
counts, known-pairs list and worked examples stay; the meta-commentary
paragraph, "do not infer, guess, or embellish", "a turn with an obvious
stated fact must never come back empty" and the duplicated empty-case line
were all anti-symptom patches and all went. 2065 characters to roughly 450.

**Stage 3 — closed, no changes.** Reviewed on the 2026-09-03 commute; the
five files were read one by one and none was revised, the cut-first method
having been applied during authoring rather than saved for this stage. The
four observed symptoms survive as things to watch, not as a revision
backlog — a symptom that persists now needs a fixture, not a rewording:
**Intent is theatric** (suspected: the "spokesperson for a collective"
framing and the one-or-two-sentence clamp); **advisories arrive unweighted**
(bare brackets with nothing saying how to weigh them, which matters more
since a woken note is the persona's own opinion arriving in one);
**Librarian and Recall select too narrowly** ("name the people you know
about" came back empty — `MaxSelectedPairs` was raised across every tier,
with `MaxConcurrentRecalls` and `MaxPickedPerWorker` moved to per-tier
config alongside, but the relevance rule is instruction text and wants a
fixture asserting an enumeration question returns more than one topic); and
**Archivist needs handholding** — the longest instruction in the codebase,
and its category choices are what Hindsight's `pairs` field is checked
against, so its failures are not local.

**Stage 4 — the agents that never call a substrate.** Stage 2 moved the five
*prompt* files on the reasonable reading that instructions are what you send
a model. That missed Identity's persona, Impulse's reflex reply and
Governance's three notices — all C# constants, so none was in the folder
Daniel reviewed, and the persona's own self-description had gone unreviewed
for months because changing it meant a rebuild. The test is not "does a
model see it" but **"is this a writing decision"**.
`ArchiveWriteStyle.EnglishFields` went the other way and was deleted
outright: its whole job was deciding whether a sentence appeared in a
prompt, which the instruction file now does directly. `TerseValue` survives,
because the drift risk is real. **Identity is a seed, not a setting** — the
file writes to `assistant/persona` only when empty and the store wins
thereafter, because a persona meant to grow should not be silently replaced
by a `git pull`, and "file always wins" makes the store pointless.

**Stale references and milestone tags** (`556bc43`). Nine `plan §X`
cross-references pointed at a document that does not exist, repointed at
architecture.md; milestone tags described shipped work as pending and were
dropped; `ArchivistAgent`'s class comment claimed extraction was grounded in
"Recall's own lookup results" when the key it reads is Librarian's selected
pairs. One survivor: `Archive:Path` resolves to `memory.jsonl` and feeds the
*agent state* store, not the archive — `AgentState:Path` would say what it
is, but that is a rename with a config migration attached.

**What the SSE stream ships.** `EnvelopeDto.From` serialised the whole
MetaBag, so the largest value on the bus went down `/api/stream` three times
a turn and was read by nothing. On the bus it was free (an in-process object
reference, never serialised); the waste was purely at the HTTP edge.
`Sse:ExcludedMetaKeys` denies it there — a deny-list rather than an
allow-list, since an allow-list needs editing every time an agent adds a key
the UI wants, and the failure mode of forgetting is a silently missing
feature rather than visible bloat.

---

# Parked

Real gaps against the Python prototype's `current-spec.md`, deliberately not
being worked. Revisit when the named condition holds, not before.

**Elapsed time.** Stamping the gap since the last conclusion onto the
perception envelope, so the persona can say "it's been a while" about
something measured rather than guessed. Parked: the debug latency readout
already covers the diagnostic half, and the interiority half did not earn
its place — nothing downstream was asking for it. Revisit if a concrete
behaviour wants the gap, not because it is cheap to compute.

**§6.1 Watchdog.** No liveness ladder, no idle-musing timer. Parked until
the destination platform is known, or until the running system proves flaky
in practice. Designing a liveness ladder before knowing what it runs on is
guesswork.

**§6.2 Recovery bootstrap.** No IaC-style sequencer. `Program.cs` plus the
manifest validators already cover config-drift detection, a partial
differently-shaped analog. When revived it should be scoped wider than the
original: one sequencer that doubles as an **installer**, provisioning a
missing local LLM and missing agents rather than only restarting dead ones.
That makes it platform-dependent, so it waits on the same decision.

# Out of scope

Not gaps. Listed so they don't get re-raised as oversights without a fresh
decision.

**Messaging-plumbing differences.** Python's synchronous recursive
`publish()` versus per-agent queues; Governance-as-orchestrator versus
Governance-as-listener; Librarian calling Knowledge directly versus
selecting pairs for Recall. The port targets business logic, not
architecture.

**§7.2 Budget Mode auto-latch.** Only per-event cost logging exists, not the
spend-cap auto-latch to deterministic fallbacks. Revisit if real spend
becomes worth automating around.

**§4.2 `is_parroting()`.** Structurally moot: the Python check stops Intent
echoing *Analytics'* advisory prose, and `LibrarianAgent` is a pure selector
emitting no advisory text at all. The refusal-lead-in constraint is moot for
the same kind of reason — Governance appends the Blocked text
deterministically, so Intent never gets the chance to soften a block.

**Two arrays into Intent.** The merged, importance-sorted result set
replaces it on purpose; see the knowledge-swarm record.

# Open design questions

**Swappable personas.** Switching which persona is active. Recall should
stay shared across personas (it's "what happened," not character); Identity
should not — each needs its own trait bank that only develops while active.
Open: does a swap create a new Intent instance or re-hydrate the same one
from a different store? Wants its own design doc — the largest single piece
of unscoped work in the project.

**Match input to output, not just retrieve.** Identity and Recall answer
"what does the archive say that's relevant to this event" — a retrieval
question. The sharper version is "given this event, what do I already know
that changes how I should read it" — an inference question. Tension:
archive-lookup's own principle is "report what the records say, never invent
one", and pushing toward inference risks turning Recall into a second
Librarian.

**Does the vocabulary earn its two calls?** Cataloger on write and Librarian
on read cost one model call each, to produce a cut that a cosine sweep may do
better and for free. Selection sits at 80%, and the dominant failure is
specific -- 14 of 19 misses opened the right category and the wrong topic. A
flat vector search has no topics and cannot make that mistake. Stated
plainly because the log does not contain the arm: batches 3-12 compared
shelves to each other and never to the absence of one, so the pairs have
never been measured against the null. Three things keep them for now, none
of them retrieval: `Merged()` addressing (which survives either way -- the
address is subtopic/subject/key, not the pair), a fallback when no embedder
is available (`IEmbeddingProvider` holds that unavailability is normal), and
a person browsing their own archive. The likely landing is vectors as the
primary index with pairs demoted to a file layout and a degraded path, but
the arm decides it. See `tools/retrieval-bench/README.md`, v4.

**Answered, batch 15: no, and the predicted landing is the one that happened.**
Both calls were measured against their absence on the v4 corpus (87 questions,
1559 rows, near-miss clusters, strict scoring).

*The read call loses outright.* The Librarian selects 47% and answers 42%;
ranking the same files by a centroid of their rows selects 74% and answers
72%, and letting every row vote reaches 87%. Given a correct pair, cosine
ranks as well inside it as a flat sweep does across the whole archive, so the
narrowing never improved the ranking -- it only sometimes handed over the
wrong pile. Even embedding the file name the Librarian reads, rather than
having a 4B rank 170 of them, is worth 3pp. Delete the call.

*The write calls tie.* Re-filing the same extracted rows by nearest gloss --
the mean of ten sampled rows already in a file -- scores 77%/73% select/strict
against the Cataloger's 74%/72%, while agreeing with it on 11% of rows. A
paired bootstrap puts that at +1.1pp, 95% CI [-9.2, +12.6]: a tie, not a win.
The same bootstrap separates a one-row gloss (-12.6pp) and a three-row gloss
(-11.5pp) cleanly, so the instrument can see an effect where there is one.
Two calls per row bought nothing measurable over arithmetic that disagrees
with them nine times in ten, and a gloss costs calls per *file*, once, against
two calls per *row* forever.

So the vocabulary earns zero of its two calls, and survives for exactly the
three non-retrieval reasons listed above. Two caveats carried forward: filing
by gloss concentrates gold into 31 pairs where the Cataloger uses 54 -- a
density sweep shows both readers degrading in parallel rather than the vector
filer degrading faster, which downgrades that worry without closing it -- and
11% agreement means an archive filed where a person would not look, which is a
real cost for a store meant to be browsable and which no retrieval number will
ever show.

**Batch 16 closes the cold start, and it does not need an archive.** The gloss
above is derived from ten rows already in the file, which a day-one archive
does not have -- that is why gloss-1 and gloss-3 lose 12pp. Both shelves
already ship a *written* gloss per pair, and neither had ever been embedded.
Filing by the written line reads 70% strict at three files against 70% for the
derived centroid. So the shippable version of idea 2's write half is: embed
the lines already in `bench.CAT["gloss"]` once, at build time, and file against
them. No sampling, no bootstrap off a young archive, no calls per row.

The same run answered Daniel's other question -- 34 terse pairs with one gloss
each -- and consolidation is not reversed. Terse leads at equal file count
(75% vs 70%) only because three files of 34 hands it 214 rows where three of
171 hands the shipped shelf 63. Matched on rows reached, the shipped shelf
wins: -18.4pp for terse at the cheap end (CI [-32.2, -4.6], P 0%) and a tie at
the expensive end bought with 38 extra rows. Terse's smallest openable unit is
80 rows, so it cannot express a cheap read at all. That is batch 12's
facts-per-file mechanism arriving for a *vector* reader, which the earlier
batches could not show, since they only ever had a weak model doing the picking.

And the written gloss is not merely tied on retrieval -- it is the better arm
on the two caveats carried against the derived one. Re-run as a filer beside
the Cataloger: agreement 38% against by-gloss's 11%, so the archive lands much
nearer where a person would look, and spread 60 pairs against 31 -- wider than
the Cataloger's own 54, so the concentration worry inverts rather than
shrinks. Same +1.1pp, P(better) 53%.

The coverage gap this looked like it had was withdrawn on inspection: all 10
unglossed shipped pairs, and all 8 terse ones, are `x/other`. `other` is the
valve, defined by matching nothing, so there is no direction in the space for
it and a gloss would invent a meaning it does not have. A vector filer never
files into `other`, which is correct and consistent with `other` already being
write-side only. So there is no blocker: the written gloss covers every pair a
vector could be asked to choose.

One measurement changed underneath all of this and is worth carrying: the
bench scorer had been matching answer keys against the address line only,
never the sentence field the Archivist writes and the embedder reads. Fixing
it moves the write-side ceiling from 77/87 to 85/87, so the write side was
never as lossy as batches 3-14 reported.

**Two extractors over one message.** Redundant columns cost nothing at rest,
so the archive could carry facts from a strict field extractor and from a
looser second pass side by side, and let the reader see both. Attractive
because the `writable` ceiling (29/35 on v3) is a write-side loss nothing
downstream can recover, and two methods fail differently. Unmeasured, and it
wants the v4 corpus before it is worth designing.

## Open against the inverted archive

Five arms gate the design in *The archive inverted*. The first two decide
whether it works at all; the rest decide how much of the old machinery
goes.

**Does retrieval show Intent the contradiction?** Supersession is resolved
by reading rather than by writing -- Intent sees "dog named Buddy (2019)"
beside "new dog, Rex (2024)" and works it out. That only holds if top-k
returns *both*. Naive cosine on "what is my dog called" may return three
near-identical Rex rows and never surface Buddy, and then Intent resolves
nothing, confidently. Wants temporal spread, dedup-by-similarity, or
neighbourhood expansion. This is the single arm the whole design leans on
and it is unmeasured.

*Answered, and shipped.* Batches 24 and 25 measured exactly this. A flat
top-5 returns **1.47 distinct facts** out of five -- the failure the arm
predicted, and worse than the guess. Collapsing by thread and taking each
thread's *newest* row rather than its nearest reaches 4.13 distinct at
0.911 current; the two-read split (A `superseded_by IS NULL`, B
unrestricted) takes stale to 0.000 and MMR at lambda 0.7 adds 0.14
distinct on top. Identity dedup *alone* makes the answer worse than flat
(oracle: 4.80 distinct but 0.667 current), which is the one result here
nobody would have guessed. All of it is in `UtteranceConsult`. What
remains unmeasured is the same thing every arm here ends on: this was a
synthetic corpus with `superseded_by` modelled perfectly, so read A is a
ceiling, not a forecast.

**Can a read reach a retired fact at all?** The two-read split as built
cannot, and the arm above is quieter about this than it should be. Pass B
is a *top-up*, not a lane: it runs only when pass A failed to fill five
slots, and it further skips any thread A already spent a slot on. At a
thousand rows A will always find five live threads above the floor, so B
never fires; and in the one case where B would matter -- "what car did I
used to drive" -- the car thread is precisely the thread A used its top
slot on, so B would skip it even if it ran. The old car is unreachable
through `Find`, twice over.

The cause is not ranking. "What do I drive" and "what did I used to drive"
produce near-identical query vectors, and no score threshold separates
them, because the discriminator is not in the text -- it is *which member
of the matched thread* the reader wants. That is a second read, not a
second sort.

The shape that fits: score and collapse exactly as `Find` does, then
instead of taking each thread's newest live member, take the top thread
and return its members oldest to newest. One thread, its whole history, in
order -- which is also the `change` shape pass B was supposed to serve.
The sweep has already run and the thread ids are already there, so the
cost is a projection. Open questions: whether Intent should call it, or
whether `Find` should spend one of its five slots on the runner-up member
of its top thread when that member is superseded; and whether the class
comment on `UtteranceConsult` should stop claiming pass B guards against
an amnesiac archive, which at corpus scale it does not.

*Possibly not a defect.* A read verb named `Find` that means *now*, and
means it without exception, is a defensible thing to have -- the failure
it prevents (a retired fact competing with the one that replaced it) is
the expensive one, and it prevents it absolutely rather than on average.
On that reading the old car is not missing from `Find`; it is simply not
what `Find` is for, and the work is additive: a second verb, or a knob
that opens the door deliberately, rather than a repair. That also keeps
the two failure modes separately tunable, which a single blended read
would not. The class comment still needs correcting either way, since it
credits pass B with a job pass B does not do.

**Does flat retrieval hold at scale?** Flat cosine beat every shelf arm at
1559 rows on a synthetic corpus, and no batch in the log tests degradation
under density. The compute is not the question -- 100k rows at 384 dims is
about 150MB and sub-second with SIMD on a phone. Whether quality survives
is.

*Partly answered.* Batches 24-25 ran at 8 000 rows and the consolidator
gate at 20 000, an order of magnitude past the 1559 that raised the
question, and the read rule held its numbers. That is still synthetic and
still one corpus shape; what it rules out is a collapse between 1.5k and
20k, not a slow drift beyond it.

**Is the write side filterable without a model?** Storing every utterance
stores "haha ok". Cheap in bytes, not free in retrieval, since near-
duplicate junk crowds top-k. Length and novelty-against-existing-vectors
may be enough. If they are, the Archivist's last job goes and a turn costs
one LLM call.

*Partly answered, and shipped.* The length half is in `UtteranceFilter`,
cut on content words rather than characters (`MinContentWords`, default
one): a sentence with no content word in it makes no claim, so there is
nothing to recall from it, and the stopword list the lexical lane already
needed now carries the conversational filler too. The novelty half was
deliberately not built -- threading already collapses restatements into one
thread that reads back as its newest phrasing, so dropping the row as well
would buy nothing at read time and would lose the fact that it was said
again, and when. What is still unmeasured is whether the filter changes
retrieval at all; it is one integer, and zero restores the old behaviour.

**Should the shelf be clustered rather than authored?** If nothing routes
through it, folder names can be labels on discovered structure: cluster the
vectors, name the clusters offline and occasionally, and ship the 170
written glosses as a seed, since a cold archive has nothing to cluster.
Costs: names become unstable, so a pair needs a stable id with a
free-moving display name; reclustering is a background job that must never
block a turn; and early on a person sees folders they did not choose, which
argues for a merge/rename/pin surface -- itself a good feature.

*Moot as asked.* The inversion deleted the shelf rather than reauthoring
it -- there is no folder for a cluster to name, and nothing routes. The
live question this leaves behind is threads, which are discovered
structure with no names at all; whether a person ever wants to see or
rename one is the same feature argument, moved.

**On-device decode speed.** Every claim about a free tier that is not
merely sponsored assumes a phone can run a small model at a tolerable rate,
and nobody has measured it. It gates the whole delivery plan and it is the
cheapest thing on this list to answer.
