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
