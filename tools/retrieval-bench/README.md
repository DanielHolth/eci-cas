# retrieval-bench

What the write side actually produces, scored. Everything here talks to a
local OpenAI-compatible server at `localhost:8080` and loads its prompts from
`src/EciCas.Host/instructions/` — the shipped files, never copies. A bench
tuned against a double lets a hand revision go unmeasured, which is the same
reason `ShippedInstructions` exists in the C# tests.

```bash
python cat_tie.py 5        # one prompt rule, both arms, 5 interleaved reps
```

## The three things being scored

| axis | what it asks | key |
|---|---|---|
| value | can the question be answered from the rows at all | `answers.py` |
| drawer | is the category defensible for that statement | `filing_key.py` |
| nulls | rows produced from a message that states nothing | `corpus.NULLS` |

They are separate on purpose. `docs/roadmap.md`'s 58% is pair-hit alone — no
value is ever inspected — so end-to-end recall is that number times whatever
fraction of rows still contain the answer, and the second number had never
been measured before this directory existed. An extraction rule that improves
one axis and wrecks another is not an improvement, so the extraction A/B
scores all three at once.

`filing_key.py` holds a *set* of defensible pairs per statement, not one right
answer, so it measures gross misfiling rather than my taste. The one place it
is strict is the person-known-through-the-job tie, because `cataloger.txt`
decides that tie in writing and a rule nothing enforces is a comment.

## Method

Interleave the arms within each rep, never run A five times and then B five
times: the local model's behaviour drifts across a long session and a
between-runs comparison measures the drift. Where a change is downstream of
extraction — anything about filing — extract **once** per rep and file the
same rows through every arm, or extraction variance swamps the effect being
measured. Judge by sign consistency across reps, not by the mean: the noise
floor on 12 statements is about 10 points, so a 4-point win in one rep is
nothing and a 2-point win in 5 reps of 5 is real.

Always score the nulls. A rule that makes extraction more complete usually
makes it more fabricating too, and that trade is invisible on the other axes.

## Files

- `bench.py` — primitives: instruction loading, the call, row parsing, the
  two-call filing path. Import it; it is not a script.
- `answers.py`, `filing_key.py` — the two keys.
- `cat_tie.py` — the person-vs-job tie rule. **Shipped**: 88% → 96%, better
  or equal in 5 of 5. Arms are flipped so it still re-runs against the new
  shipped line.
- `extract_ab.py` — extraction rules on all three axes at once. **Shipped**
  the property rule: value 81% → 91%, better or equal in 5 of 5, drawer
  unchanged, and fabrication on the nulls 0.4 rows per rep → 0.0. Not shipped:
  "the key names what was stated, not what it means" — three reps up, one
  down, one level.
- `variant_b.py` — that property rule, now the shipped text; `apply()` runs
  the replacement backwards so the comparison still re-runs.
- `subject_probe.py` — does a named person actually become the subject.
  Counts subjects on two sentences, which is the one thing the sufficiency
  score cannot see: `subject=brother key=location value=Tromso` has every word
  the answer needs and is still an address Librarian will not look at.
- `full_path.py` — no score, just the whole address as written, several runs
  per statement, so drift and settlement are visible side by side. Reading it
  is what showed that category is effectively deterministic while
  subtopic/subject/key change on nearly every run — and that this is fine,
  because only the first two route.
- `review_other.py` — the second pass over `other`. **Dead end**: it removes
  `other` as designed and buys no accuracy, because `x/other` was never the
  loss it looks like — the read path opens `other` alongside its parent
  category in code. Kept so nobody builds it twice.

## The read side

`read_bench.py` is the read instrument, and it exists because every read
number in the roadmap except the -35pp hierarchical loss is smaller than the
noise floor of the thing that produced it.

Two fixes over the old measurement, both aimed at resolution rather than at
any idea:

- **`tests/corpora/retrieval_v3.py`** -- 24 statements, 35 direct questions
  against v2's 17, written before anything was run and not revised against a
  score. When it is spent, write v4 rather than editing it.
- **Populated distractors.** The 58% in the roadmap was measured with the
  padding pairs as empty files, so a wrong pick returned nothing. Here all
  170 pairs hold rows, generated once and cached, so a wrong pick returns
  something plausible and can mislead the picker downstream.

It reports three numbers and refuses to collapse them, because the read path
has three places to lose a fact and only the first was ever measured:
`select` (did Librarian open the right pair), `pick` (did Recall keep the row
out of what it opened), `answer` (were the answer's tokens in what came
back). It also reports `writable` -- how many questions the write side had
already lost before Librarian saw anything -- so a read arm is never credited
or charged for extraction variance.

The archive is frozen to `.archive_v3.json` (gitignored). Rebuild by deleting
it; do **not** delete it between two arms you intend to compare, for the same
reason bench.py extracts once per rep.

`--oblique` scores the bonus tier: questions that do not name the fact they
need ("who should we visit while we are in Bodo?" wanting a sibling). These
are not expected to pass and are never folded into the headline. They are the
cases a subject index would have to earn its keep on.

## The sentence column

Two arms, run in this order, because the second is meaningless if the first
moves:

- `write_sentence_ab.py` -- does asking for the sentence damage the
  extraction it rides on. The old arm is `archivist.txt` read out of git at
  the commit before the field, not a copy kept here. Scored on the address
  fields only: the sentence restates the fact in full words, so a scorer that
  saw it would report the model's own paraphrase back as a win.
- `sentence_ab.py` -- does the sentence stop Recall discarding the row. One
  extraction, one archive (`.archive_v3_sent.json`), one selection per
  question, five arms that differ only in how a row is rendered to Recall.

**Pre-registered**: the sentence should lift the filtered arms and barely
move `nopick`, since `nopick` keeps everything and cannot use more surface.
The gap closing from below is the mechanism; a uniform lift on both is not,
and means the story is wrong. `select` cannot move -- Librarian reads file
names, never row text -- so it is printed once as a harness check.

Written down before the run, which is what batches 3-7 did not have.

## The known limit

`tests/corpora/retrieval_v2.py` is 12 statements and the write-side arms
already sit near 90%. There is almost no headroom left to measure in, and its
held-out half is burned. v3 replaces it for read work; the write-side arms
above still run against v2 and should be re-based on v3 before any of them is
re-litigated.

## v4 — the corpus that has to settle four questions

Pre-registered before generation. v3 is spent: it cannot settle file
fatness, cannot settle whether the sentence column costs facts, and cannot
settle the question that matters most, which is whether the closed
vocabulary earns the two model calls it costs.

**The admission that motivates it.** Batches 3-12 compared shelves --
170-pair against lean, terse, consolidated. Every arm assumed a vocabulary
and argued about its shape. No arm has ever compared having a vocabulary to
not having one. The categorizer/librarian path has not been measured against
its own absence, and all of that work predates vectors being on the table.

Four arms this corpus exists to run, all on one frozen archive, differing
only in the reader:

    librarian + recall     today
    librarian + nopick     the batch 12 winner
    librarian + cosine     pairs as the coarse cut, vectors as the fine one
    flat cosine            no Librarian at read time at all

The write path is identical in all four. Archivist and Cataloger still file
to pairs, and every row additionally carries its sentence and its vector, so
the flat view is a projection of the same rows rather than a second archive.
Redundant columns cost nothing at rest and keep the comparison free of write
variance.

### Shape, decided before generating

**Lumpy, not uniform.** Real archives are a few fat pairs and a long tail of
two-row ones. A corpus where every pair holds twenty rows would flatter both
readers: the picker gets real discrimination pressure everywhere, and cosine
gets uniform density with no thin files where a wrong-but-close row wins by
default. Target distribution across the 170 pairs, checked after generation
and reported with the archive:

    fat     ~8 pairs    50-80 rows    the regime Recall cannot afford
    middle  ~40 pairs   10-25 rows    where most turns land
    thin    the rest     1-4 rows     where a bad vector is unopposed

**Cross-category near-misses are mandatory.** Rows in unrelated pairs that a
question could plausibly match on surface. Without them the flat arm faces
no discrimination pressure and wins trivially -- and the entire point of the
flat arm is that it may win for real.

**The null ratio moves.** v3 is 8 nulls against 35 questions, and nulls are
scored separately and never folded into the headline. That grades every arm
on a curve that rewards guessing: an arm keeping everything takes full credit
for its recall and pays nothing for volunteering facts nobody asked about.
v4 raises nulls to roughly a third of the question set, because pricing
lenient against strict is the open question the current instrument cannot
see.

**Tier-tagged caches from the start.** `.archive_v4_<tier>.json`. The archive
is written by the tier's own model, so an end-to-end comparison across tiers
mixes a write difference with a read difference and cannot attribute either.
Tagging is cheap now and a retrofit later; it is what makes the cross arm
possible -- Default's readers over Minimal's archive, same rows, only the
reader changing.

### Cost, so the size is chosen rather than discovered

At ~3000 rows a full Default archive build is roughly 1M input and 250k
output tokens across Archivist, Cataloger and padding: under a dollar at
`gpt-5.6-luna` pricing. Minimal is free. Cost does not constrain this corpus,
which is stated here so nobody later trims it for a reason that was not real.

### What freezes when

The pre-cosine Minimal baseline pins the corpus. A statement added after it
invalidates every number measured against it. So the shape above is settled
here, before generation, and v5 is the way to change it.
