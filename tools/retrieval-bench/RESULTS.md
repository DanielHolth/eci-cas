# Read-side arena — results log

Numbers only compare *within* a batch. Every arm in a batch is interleaved
question by question against the same frozen archive; across batches the same
arm has come back 9pp apart (lean+oracle, 66% then 57%), so a row from one
table must never be read against a row from another.

Baseline throughout: `full`, no gloss, shipped Recall.

## Batch 5 — four reps, the first table that can decide anything

  arm             category select answer quiet files rows
  full                 86%    41%    30%   71%   3.4   7.0
  full+gloss           87%    53%    43%   84%   3.1   6.6
  full+gloss+len       84%    52%    47%   87%   3.1   6.5
  full+len             83%    38%    28%   87%   3.3   6.7
  lean+wholecat        85%    85%    56%   68%   4.9  10.3
  lean+wc+lenient      85%    85%    57%   81%   4.9  10.4

**The gloss is settled.** Shipped shelf, filing untouched, one prompt change:
select 41 -> 53, answer 30 -> 43 (per rep 0/+5/+8/+5), quiet 71 -> 84. Ready.

**The lenient bar is not what batch 4 made it look like.** Alone it is
worthless -- full+len 28% against full's 30%, and its select drops, which it
has no mechanism to do and which is a useful reminder of the noise on a
single arm. With the gloss it is +4pp but +7/+3/-5/0, no sign consistency.
Its one honest win stays the oracle arm at two rows: it recovers judgment
errors where the open is narrow and nothing where it is wide. Batch 4's
+15pp was measuring a case the shipped system does not run.

What it does do reliably is the opposite of the predicted trade-off: quiet
87% on both arms carrying it, the highest in the table, against strict's 71%.
A bar that keeps more rows leaks less small talk.

**lean+wholecat leads on answer** -- 56 against full+gloss+len's 47, per rep
+1/+1/+7/+4. But whole-category is not a property of the lean shelf;
full+wholecat matched it in batch 2 at 21 files and 48 rows a turn.
Consolidation is not the win, it is what makes the win affordable, and it
charges a writable ceiling of 29/35 against 30/35 for it.

lean+wholecat's 68% quiet is the gap the gate arms exist to close.

## Batch 6 — the gate, four reps

  arm             category select answer quiet files rows
  lean+wc+nopick       85%    85%    66%   40%   5.0  10.6
  lean+wc+lenient      85%    85%    58%   78%   4.9  10.6
  lean+wc+gate         85%    85%    29%  100%   5.0  10.6
  full+wc+gate         80%    80%    39%  100%  20.6  46.6
  full+gloss+gate      80%    52%    27%   96%   3.0   6.3

The mechanism works and the calibration is wrong. Small talk went perfectly
quiet, which is what the gate was for, and `answer` fell 66% -> 29% with
`select` untouched at 85%: the right files opened and the gate binned their
rows. It says no to more than half of genuine questions.

That is a prompt fault and a repeat of one this repo already solved.
librarian.txt states its asymmetry outright -- a wrongly-opened topic is
filtered in phase two, an unopened one is gone. The gate stated none, so the
model weighed yes and no as equal guesses when the costs are nothing alike:
opening and not needing it costs rows in a prompt, not opening loses the
answer. Rerun with the asymmetry written in.

Also visible here: nopick at 40% quiet is worse than the 37% of batch 3, and
full+gloss+gate's select of 52% matches full+gloss elsewhere, which is the
sanity check that a Recall-side arm is not moving selection.

## Batch 7 — the gate with the asymmetry stated, four reps

  arm             category select answer quiet files rows
  lean+wc+nopick       85%    85%    65%   46%   4.9  10.5
  lean+wc+lenient      82%    82%    55%   87%   5.0  10.6
  lean+wc+gate         86%    86%    45%   46%   5.1  11.0

Stating the asymmetry moved the gate from one failure to the opposite one.
`answer` recovered 29% -> 45%, and `quiet` collapsed 100% -> 46%, which is
nopick's number to the point. Told that looking costs little and not looking
loses the answer, the model now says yes to nearly everything, so the gate
is a no-op on the job it was built for.

It is dominated on both axes at once. Against nopick it gives up 20pp of
answer and buys no quiet. Against lenient it gives up 10pp of answer and
41pp of quiet. There is no reading of this table where the gate is the arm
to ship.

Two prompts, opposite miscalibrations, no middle: the 4B is not holding a
graded yes/no on this question, it is picking a side and going there. The
knob is real -- batch 6 proved the two Recall jobs separate cleanly, 100%
quiet with select untouched -- but this model cannot be talked onto the
middle of it.

Parked, and parked on its own terms rather than deferred. The standing rule
is to retest inconclusive results against a smaller pool after the write
work; the gate is the one exception, because its prompt is shown the turn
and nothing else. Pool size cannot reach it. It comes back only as code --
a confidence threshold, or the gate asked once with both costs priced --
not as another prompt.

lenient stands as the quiet arm: 87% here, 78%-81% across batches 5 and 6,
the only thing measured that raises quiet without gutting answer.

## Batch 8 — the gloss on the WRITE side, 170 pairs, four reps

Not an arena batch. `write_gloss_ab.py`, scored against filing_key.KEY.
Extraction runs once per rep and the category call once per row, both shared
by the arms, so the topic call is the only difference.

  arm     pair  category  other   rows
  plain    83%       96%     5%     56
  gloss    87%       96%     0%     56

  per-rep pair delta (gloss - plain):  +2  +1   0  -1

`category` identical at 96% is the harness check, not a result: the two arms
were handed the same drawer by construction, and a split there would have
meant the control was broken.

The verdict is no effect. 4pp on the mean with the sign changing across reps
is exactly what the repo's own floor is set to reject, and one rep going the
other way is the thing sign consistency exists to catch. The gloss does not
help the writer at 170 pairs, and it does not hurt it either.

That is close to what the shelf predicts. At 170 pairs the folder names are
already concrete -- passport, renewal, allergy -- and a gloss adds little to
a name that is already the word the fact uses. The claim was never about
concrete names; it was about ABSTRACT merged ones, thing and upkeep and
history, which say nothing on their own. This shelf cannot test that claim,
because it has no abstract names in it.

There is also a ceiling. plain files 83% of rows into a defensible drawer
against a key that is deliberately generous, so there are roughly nine wrong
rows in 56 for any arm to win back.

`other` fell 5% -> 0% and never went the other way, which is the direction
the shared-dictionary idea predicts most directly -- other is what the writer
reaches for when no folder looks like it is about the fact. But it is three
events in 56 rows. Directionally right, far too small to bank.

What this batch is for is the baseline. The gloss's effect on filing is zero
at 170 concrete pairs, measured before consolidation moved anything. When
the merged shelf is filed both ways, a gain there cannot be the gloss being
generally useful, because here it was not. It would be the merged names
needing a definition, which is the actual claim.

Correction recorded with it: `writable` was cited earlier as evidence that
consolidation damages the writer, lean 29/35 against full 30/35. It is not.
read_bench.gold_index defines gold as the pairs a statement's rows were
ACTUALLY filed to, so wherever the writer puts a fact becomes gold and
`writable` measures extraction alone. That 1-fact gap is rebuild noise
between two archive builds. The claim that consolidation hurts filing is
untested, not supported -- which is the whole reason this scorer exists.

## Batch 9 — VOID

The terse shelf's first end-to-end run. bench.file_fact's vocab override
reached the topic call only, so the writer was handed the shipped ten drawer
names and filed 18 of 31 gold rows to unfiled/unfiled. It produced a full
table and the table was read as a shelf result. Numbers discarded, archive
deleted, build() now refuses to cache an archive containing unfiled rows.

The tell was in the table: terse+oracle showed 11.1 rows against
lean+oracle's 2.0 elsewhere. An oracle opening five times more rows is not
a shelf, it is a bug.

## Batch 10 — the terse shelf, end to end, three reps

  arm                category select answer quiet files rows
  full+gloss              81%    49%    38%   83%   3.1   6.5
  lean+wc+lenient         85%    85%    60%   91%   5.0  10.5
  terse                   68%    57%    26%   83%   3.5   6.8
  terse+gloss             75%    57%    36%   87%   2.9   5.5
  terse+wc+lenient        71%    71%    50%   91%   5.4  10.5
  terse+oracle           100%   100%    71%   79%   1.1   3.0

  terse -> terse+gloss, per rep:  +6  +2  +2
  terse+wc+lenient vs lean+wc+lenient, per rep:  -3  -3  -5

writable 30/35, level with the shipped shelf: the terse writer is not damaged.

THE GLOSS CLAIM IS CONFIRMED. 26% -> 36%, three reps, no negative rep, about
10pp. Batch 8 measured the same gloss at zero on 170 concrete pairs. Taken
together those two results say the gloss is not generally useful -- it is
specifically what makes an abstract folder name workable. `passport` does
not need defining and `document` does. That is the result the whole
before-and-after ordering existed to produce.

TERSE LOSES END TO END. 50% against lean's 60%, negative in all three reps,
at the floor rather than inside it.

The loss is at the category stage: 71% against 85%. Fewer categories with a
WORSE hit rate, which kills the assumption that shrinking the pool would
raise it -- renaming cost more than shrinking gained. Three names have to be
mapped from a question that never uses them: resources, records, and
appointment. appointment is the suspect, because it is the time-cut drawer
and every dated question must now choose between it and the subject drawer.
The writer has six tie rules for that choice. The reader has none.

That asymmetry is new and it is the first candidate to test: the ties are in
the Cataloger's drawer prompt and nowhere in the Librarian's. On the shipped
shelf that did not matter, because no drawer was cut on a second axis.

Not answerable from this table: whether the terse archive is better WRITTEN.
terse+oracle at 71% is the highest oracle measured anywhere, but lean+oracle
is not in this batch and a cross-batch read is the one thing the method
forbids. Running the three oracles together is the next batch.

## Batch 11 — the three archives with the selector removed, four reps

  arm            answer  files  rows
  full+oracle       78%    1.1    1.7
  terse+oracle      69%    1.1    3.0
  lean+oracle       67%    1.1    2.0

  terse vs lean, per rep:  +4  0  0  -1

The oracle opens the pair the writer used, so this is how well each shelf is
WRITTEN, with selection taken out. Four reps, one batch, finally comparable.

Consolidation does not improve the archive. The shipped 170-pair shelf is
about 10pp ahead of both consolidated shelves, and terse against lean is
nothing -- terse's extra gloss and its six tie rules buy back nothing at
this stage.

The mechanism is the rows column and it is clean: 1.7 rows a file for full,
2.0 for lean, 3.0 for terse. Fewer folders means more facts per folder, and
Recall has to find the answer in a bigger pile. The ranking tracks the row
count exactly.

So consolidation's cost is not filing accuracy -- writable was level all
along, and batch 8 already withdrew that claim. It is that every file gets
fatter and a fatter file loses more to Recall.

Which says what consolidation was ever for. It never improved anything on
its own. It made whole-category fan-out affordable, 5 files instead of 21,
and the gain attributed to the lean shelf across batches 3-7 was the
selector's, borrowed.

That reopens something set aside as unaffordable. Batch 3 measured
full+wc+nopick at 75% answer, the highest end-to-end number in this log, on
the shipped shelf at 21 files and 48 rows. It was dismissed on cost. This
batch says the shipped archive is genuinely the best written one, so that
75% may not be a fluke of its batch. Cross-batch, so it proves nothing --
the next batch runs whole-category on all three shelves together.

## Batch 12 — whole-category on all three shelves, one batch, three reps

  arm               category  select  answer  files  rows
  full+wc+nopick         85%     85%     78%   20.6  46.4
  full+wc+lenient        82%     82%     60%   21.4  48.4
  lean+wc+lenient        82%     82%     54%    5.2  11.1
  terse+wc+lenient       73%     73%     48%    5.5  11.2

  answer, per rep:
    full+wc+nopick   27  27  28
    full+wc+lenient  21  24  18
    lean+wc+lenient  18  19  20
    terse+wc+lenient 19  15  17

The comparison batch 11 asked for, and it settles the shelf question. The
shipped 170-pair shelf beats both consolidated shelves end-to-end, not only
at oracle, and full+wc+nopick wins every rep by a margin no noise floor
covers -- 78% against 60% for the same shelf with the lenient bar, and 24pp
over terse.

Batch 3's 75% was not a fluke of its batch. It reproduces at 78% here with
everything else held constant.

Consolidation is now negative on every measurement that exists. It never
improved filing (batch 8), it never improved the archive (batch 11), and it
loses end-to-end even with the selector handicap removed by fan-out (here).
The lean and terse shelves are withdrawn as candidates.

Two things the terse work produced that survive it. The gloss result is
real and portable: no effect on 170 concrete pairs (batch 8), ~10pp on 34
abstract ones (batch 10) -- a gloss is what makes an abstract folder name
workable, not a general improvement. And the rows column, which explains
every shelf result in this log once you stop reading it as a cost line.

The cost is the honest caveat and it is large: 21 files and 48 rows a turn,
against 5 and 11. On a 24-statement corpus that is affordable. It does not
scale -- Recall chunks by RowsPerWorker, so rows a turn is LLM calls a
turn, and the whole-category arms are buying accuracy with prompt volume
that grows linearly with the archive.

So the next question is not which shelf. It is whether the narrowing inside
a category can be done by something other than a model call. That is the
sentence column and the embedding beside it, and it moves ahead of any
further vocabulary work.

## Batch 13 — does asking for the sentence damage the extraction, three reps

  arm   value  rows/stmt  with sentence  fabricated on nulls
  old     84%       1.21             0%                  0/8
  new     85%       1.06            98%                  0/8

  per rep, value:  old 29 30 30   new 28 31 31
  per rep, rows:   old 28 32 27   new 26 25 25

The gate before any read arm: a loss here would sit underneath every later
number and read as a read result.

Sufficiency is flat -- 84% against 85%, signs -1 +1 +1, nothing. Coverage
is 98%, so optionality did not quietly empty the field and the read arm has
something real to measure. No fabrication on NULLS either way, so the extra
shape to fill did not invite filling it.

The one thing that moved is rows per statement: 1.21 to 1.06, down in all
three reps, 87 rows against 76. That is the failure the arm was watching
for -- a model spending budget on prose and returning fewer facts -- and it
is a consistent sign, not noise, even though the old arm's own spread (28,
32, 27) is wider than the gap.

What that means is bounded by what this bench asks. Sufficiency held with
12% fewer rows, so the rows that vanished carried no answer to any of the
35 questions. Whether that is pruning marginal restatements or losing facts
nobody happens to ask about today, this corpus cannot tell: it scores only
the questions it has. Recorded as a standing caveat on the sentence column
rather than settled, and worth re-testing when the corpus grows for the
fatness work.

Proceeding to the read arm regardless, because it compares two renderings
of one archive and is internally controlled -- the row cost is a write-side
fact that sits beside it, not a confound inside it. But it raises the bar
the sentence has to clear: it must buy more on read than 12% fewer rows
costs.

## Batch 14 — the sentence on the read side, one archive rendered two ways

  select (shared by every arm) 80%, rows carrying a sentence 407/409

  arm            answer   nulls    kept
  nopick            72%     54%    49.4
  strict+addr       42%     83%     1.4
  strict+sent       54%     95%     1.3
  lenient+addr      59%     79%     1.7
  lenient+sent      60%     79%     1.4

  per rep, answer:
    nopick        24  26  26
    strict+addr   14  17  14
    strict+sent   18  19  20
    lenient+addr  21  20  21
    lenient+sent  21  22  21

The prediction was written down before the run: the sentence lifts the
filtered arms, barely moves nopick, and the gap closes from below. It is
half right, and the half that fails is the half that matters.

Strict moves and moves hard: 42% to 54%, positive in all three reps, and
its null handling goes 83% to 95% -- with more surface to match against, a
reader that keeps almost nothing stops guessing when it should decline.
That is the mechanism working exactly as described.

Lenient does not move: 59% to 60%, signs +0 +2 +0, nothing. And lenient is
the arm that would ship. So on the configuration we would actually run, the
sentence buys approximately nothing on read, while costing 12% of extracted
rows (batch 13).

The kept column says why the two differ. Lenient already keeps 1.7 rows
against strict's 1.4, and gold rows sit in files holding a couple of rows
each. There is nearly nothing to discriminate between: the sentence adds
surface for a decision that is barely being made. Strict is the only arm
here that simulates scarcity, and scarcity is the condition the sentence
was designed for.

So this is a third result the corpus is too small to settle, alongside file
fatness and the row cost. The claim "the sentence helps Recall keep the
right row" is supported only where rows are scarce, and this archive is not
that archive. nopick's 72% over lenient's 60% is the same 12pp shape as
batch 12 and unchanged by any of this.

What the batch does establish: the column is written (98% coverage, 407 of
409 rows), it costs nothing in sufficiency or fabrication, it demonstrably
helps a reader under pressure, and its read-side value at this scale is
unproven rather than disproven. The case for keeping it now rests on what
it was always the prerequisite for -- a vector over the sentence rather
than over the address form -- not on a read-side win it has not shown.

## Batch 15: the embedder, the scorer, and both halves of the gloss idea

Four results overnight, two of them corrections to this log rather than
additions to it.

**The ship embedder is multilingual-e5-small, and the comparison that said
otherwise was asking the wrong question.** On the v4 corpus bge-small-en-v1.5
wins 92/81/92 flat/centroid/all-rows against e5's 88/75/88, and it is faster.
The corpus is English throughout, which is bge's home ground, and
`archivist.txt` writes the sentence in the language of the message -- so a
Norwegian household gets a Norwegian archive and none of those numbers
describe it. `lang_v4.py` puts twenty v4 statements, hand-written in
Norwegian, into the real 1559-row English archive and asks the English
questions:

    embedder            top1   top5   top20   mean rank (of 1579)
    bge-small-en          0%    15%     15%       256.1
    multilingual-e5      30%    90%     95%         4.5

bge has no shared multilingual space, so the Norwegian row is not near its
question at any k. Ship e5, pay 4-12pp on English. Hand-written rather than
machine-translated: a translation model in the loop would put its vocabulary
between the statement and the embedder, and a bad translation would read as
a bad embedder.

**The scorer was ignoring half the archive.** `answered` matched answer keys
against `line(r)` -- category/topic/subject/key/value -- and never the
sentence field, which the Archivist writes, the archive stores and the
embedder has read since a7096f4. `raw_v4.py`:

    stored              ceiling   strict@5    chars
    line                    91%        77%       48
    line+sent               97%        87%      103
    line+sent+stmt         100%        88%      106

`writable` goes 77/87 to 85/87 with no change to what is written. Every
write-side loss reported in batches 3-14 is partly measurement, and the
specific claim that the Archivist drops proper nouns from two-fact statements
is **withdrawn** -- the noun is in the sentence and `line()` dropped it.
Numbers from before 2026-09-08 do not compare to numbers after it.

That is also Daniel's idea 1 answered: storing the original message on top
does reach a 100% ceiling, so the raw text makes every fact recoverable and
lets one row carry two. But the sentence already gets 6 of the 9 points for
free, and the message is the expensive half of the idea.

**Idea 2, read half: staging the pick costs and never pays.** Match the
category first, then the file within it, with derived centroids at both
levels so the summary method is constant and the gap is the staging alone:

    categories kept    1     2     3     5     8    all
    strict            54%   62%   65%   72%   72%   72%

Monotone into the flat arm and never past it. A gate can only discard files
the second stage would have ranked, and a category centroid is blurrier than
any file centroid inside it. A written gloss could sharpen the gate; it cannot
beat removing it. Held as a price list rather than a refutation -- staging
exists to avoid scoring every file, 171 files are free to score, and 7pp at
c=3 for a 14x smaller second stage is the trade once they are not.

**Idea 2, write half: this is where it pays.** `file_v4.py` re-files the same
extracted rows three ways, padding untouched, so the only variable is where a
gold row went.

    filing       agree   select   strict   spread
    llm           100%      74%      72%       54
    by-name        32%      66%      65%       55
    by-gloss       11%      77%      73%       31

    files opened      1          3          5
    llm            49 / 49    74 / 72    82 / 79
    by-gloss       55 / 52    77 / 73    89 / 81

    gloss size      1     3    10   whole file
    strict         59%   60%  73%      73%

Ten samples is the number Daniel proposed and it is the right one: the median
pair holds 3 rows, so ten samples *is* the whole file for most of them, and
the 3-to-10 gain comes entirely from the fat pairs where a gloss has any work
to do. The bootstrap separates these cleanly -- gloss-1 at -12.6pp, P(better)
1%; gloss-3 at -11.5pp, 3% -- so the instrument can see a real difference when
there is one, and reports none between by-gloss and the Cataloger.

by-gloss uses the mean of a file's padding as its gloss -- LLM-written
examples of what belongs under that pair, independent of the gold rows, so it
is not scoring itself. **It ties two LLM calls per row while putting nine
rows in ten somewhere else.** A paired bootstrap over the 87 questions puts
by-gloss at +1.1pp strict, 95% CI [-9.2, +12.6], P(better) 53%: a coin flip.
"Beats" would be overreading 81 gold rows. The tie is the point. What retrieval needs is that filing
and retrieval agree with each other, not that either agrees with a human's
sense of where a thing goes. And the economics run the right way: a gloss
costs calls per file, once, against two calls per row forever.

Three things held against it. `by-name` loses 7pp, so the gloss and not the
name is doing the work -- consistent with name-only being the weakest read arm
in shelf_v4. by-gloss concentrates into 31 pairs against the Cataloger's 54,
and fewer-fatter-files is exactly what sank the consolidated shelves in
batches 8-12, though the width sweep shows it is not biting at this size.
And 11% agreement is a legibility cost for a store meant to be human-readable,
which no retrieval number will ever show.

**Withdrawn from the previous commit:** the address-only Recall listing was
called a product bug. It is a measured default -- `sentence_ab.py` tested the
same swap in batch 14 and found it lifts a scarce reader 12pp and moves the
shipping lenient arm not at all. `rb.pick` now takes `show` so that fork can
go away and v4, which has the fat files v3 lacked, can ask again.

### Batch 15b: the LLM read arms on v4, and the sentence again

Three reps, interleaved, corrected scorer, `writable` 85/87.

    arm         answer    nulls     rows
    recall         34%      73%      1.2
    recall+s       34%      79%      1.0
    nopick         52%      47%     33.0
    lenient        39%      83%      1.4
    select 45% -- shared by every arm

**Filtering costs 13-18pp again.** nopick's 52% over lenient's 39% is the same
shape as batch 12 and survives both the embedder change and the scorer fix.
Recall discarding rows remains the largest single loss on the LLM read path,
ahead of selection and ahead of any shelf choice.

**The sentence replicates batch 14 exactly**, on the archive that was supposed
to settle it. `recall+s` shows Recall the same rows with the sentence visible:
answer does not move at all (34% to 34%), nulls improve 73% to 79%, and the
candidate set gets slightly smaller. So the v4 fat files did not create the
scarcity the sentence was supposed to need -- and the finding is the same one
v3 gave: more surface makes a reader better at *declining*, not at choosing.
Two corpora agreeing is worth more than either, and the question is closed.

**The whole LLM read path is beaten by arithmetic.** Best LLM arm here is 52%
answer at 33 rows a turn. Filing by gloss and picking files by centroid, with
no model call anywhere in either path, is 73% strict at 5 rows a turn.

## Batch 16 — one written gloss per pair, on both shelves

Daniel's question: test the 34-pair terse shelf with a single embedded gloss
per pair. Both shelves already ship written glosses (`bench.CAT["gloss"]`,
`terse_vocab.TERSE_GLOSS`) and neither had ever been embedded, so this needed
no model calls. `gloss_v4.py` re-files all 1559 rows — gold and padding — into
each shelf by nearest gloss, then reads back.

**A written gloss ties a derived one.** Shipped shelf, 3 files, k=5: picking by
the written line reads 70% strict, picking by a centroid of what landed there
reads 70%. This closes the cold start `file_v4` opened — a ten-row gloss works,
a one-row gloss is 12pp worse, and a young archive has no ten rows. A written
line is available on day one and costs nothing per row.

**Terse's apparent win is a reach artifact.** At equal file count terse reads
75% against 70%, but three files of 34 is a tenth of the archive against a
fiftieth, so terse ranks 214 rows where the shipped shelf ranks 63.

    ~rows reached     shipped/170        terse/34
        60            70%  (3 files)     51%  (1 file, 80 rows)
       100            78%  (5 files)     51%  (1 file, 80 rows)
       220            80%  (8 files)     75%  (3 files, 214 rows)

Paired bootstrap, terse minus shipped, 5000 resamples of 87 questions:

    shipped@3 vs terse@1   63 vs 80 rows    -18.4pp   CI [-32.2, -4.6]   P 0%
    shipped@8 vs terse@3  176 vs 214 rows    -4.6pp   CI [-13.8, +4.6]   P 13%

The cheap end is a real loss; the expensive end is a tie bought with 38 extra
rows per question. Terse's smallest openable unit is 80 rows, so it cannot
express a cheap read at all — batch 12's facts-per-file mechanism again, and
now for a vector reader rather than a weak model, which is the one thing the
earlier batches could not say.

Coverage looks like a flaw and is not: `TERSE_GLOSS` covers 26 of 34 pairs and
the shipped gloss 160 of 170, but every missing pair on both shelves is
`x/other`. `other` is the valve, defined by matching nothing, so a vector filer
has no direction to point at and correctly never files into it.

**Batch 16b — the written gloss as a filer.** Re-run inside `file_v4` beside
the two Cataloger calls it would replace:

    filing       agree   select   strict   spread
    llm           100%      74%      72%       54
    by-gloss       11%      77%      73%       31
    written        38%      75%      73%       60

Ties llm on the bootstrap (+1.1pp, P 53%) exactly as the derived gloss does,
and wins on both caveats carried against it: agreement triples and spread goes
to 60 pairs, wider than the Cataloger's 54, so the concentration worry inverts
rather than shrinking. This is the arm to ship.

## Batch 17 — pricing granularity, and batch 18 — building the grid

`gran_v4.py` prices the read: how many pairs must be opened to reach 85% of
the answerable questions, as a curve over shelf size. `grid_v4.py` builds a
candidate shelf two-level, by spherical k-means over the corpus rather than
by taxonomy, so a shelf can be proposed from the vector space instead of
argued into existence. Both are numpy only, no sklearn.

The finding that carried forward was not a shelf but a format: for drawers
broad enough to need one, an example sentence beats a keyword list as the
pair's summary. That is what the gloss block below tests at full scale.

## flat_v5 and bleed_v5 — two questions about a candidate shelf

`flat_v5.py` files all 1559 v4 rows into a vocabulary by cosine and reports
the occupancy histogram. The metric to read is `top10%` — the share of rows
in the fattest tenth of drawers, which reads 10% for a perfectly flat shelf
at any size, so shelves of 170 and 512 pairs are comparable. `live10%` is the
same over non-empty drawers. `gini` counts empty drawers and therefore
penalises the bigger shelf for subjects this one synthetic household simply
lacks; it is reported, not steered by.

`bleed_v5.py` asks the other question: is a drawer a catch-all? For each
candidate drawer it takes the Shannon entropy of the SHIPPED categories its
rows came from. A subject drawer draws from one or two sources and sits near
0–1 bits; a catch-all draws from many and approaches the 3.32-bit ceiling.
Volume AND high entropy together are the defect — high entropy on three rows
is noise, so nothing under eight rows is reported.

Both caveats apply to every number either script prints. The corpus is one
synthetic household written against the 170-pair shelf, so a candidate drawer
for a subject the corpus lacks can be acquitted but never convicted. And both
scripts now summarise pairs through the gloss block; before that they
summarised by bare path, which is why every figure recorded against a
candidate shelf before batch 19 is a floor rather than a result.

## Batch 19 — the gloss block, and the largest single effect in the log

Twenty probe sentences, run against the 512-pair shelf twice with the same
topics and only the summary changed:

    bare paths   10/20
    glossed      18/20

`leisure` alone went 1 of 11 to 10 of 11. Two different topic re-cuts had
already been tried on it — one aspect-shaped, one subject-shaped — and both
scored exactly 1 of 11, which is what says the defect was never taxonomy. A
hobby sentence names the hobby, and the other 31 categories own those nouns:
"greenhouse" is home, "film" is media, "bookshelf" is project, "club" is
social. The drawer was unreachable, not broad.

Two second-order findings, both of which cost real measurements to learn:

**Aspect-shaped topics only work when the aspect words are words people
say.** `culture`, `norms` and `office` land, which is why the work/workplace
re-cut succeeded. `session`, `practice` and `kit` do not, and the aspect
version of `leisure` was worse in kind than the subject one — "birdwatching
is my main hobby" fell from `leisure/hobby-skill` to `device/watch`.

**A gloss carrying a common temporal or vague phrase is a magnet regardless
of its subject.** "I had the jab last autumn" pulled "I took up knitting last
winter" into `health/vaccination`. "I take something every morning for it"
pulled "I do the crossword every morning" into `care/medication`. "How safe
the place feels" pulled 45 rows into `home/security`. Concrete nouns, no
calendar words.

And the trade, stated rather than hidden: glossing improved routing sharply
and made the write distribution slightly LESS flat, because a good gloss is a
stronger magnet and strong magnets concentrate.

    live10%  41% -> 39%     p95  14 -> 15     fill  59% -> 51%

Flatness was always the proxy. Landing the row in the right drawer is the
goal, so the trade is taken.

**The category name barely matters once a drawer is glossed.** `admin` versus
`record`, identical topics and identical glosses, scored 6/10 and 6/10 with
mean top scores of 0.8352 and 0.8349 — three ten-thousandths apart. Category
labels are a readability choice, and that is now known rather than assumed.

## Batch 20 — 512 against the shipped 170, matched on rows reached

`shelf512_v5.py`. Batch 16's construction with the candidate shelf as a third
arm: all 1559 v4 rows re-filed into each shelf by nearest gloss, then read
back, no model calls. Every previous v512 number was write-side -- occupancy,
entropy, twenty routing probes -- and none of them said whether a question
gets answered.

Matched on ROWS REACHED, because the asymmetry batch 16 had to learn now runs
the other way: three files of 480 is a six-hundredth of the archive against a
fiftieth, so the file-count table would flatter the shipped shelf exactly as
it once flattered terse. `x/other` is dropped from the 512 shelf under the
same rule the shipped arms already run under -- 480 pairs.

    ~rows    shipped/170        v512
      20     58%   (18 rows)    70%   (22 rows)     centroid pick
      60     70%   (63 rows)    81%   (59 rows)
     100     78%  (114 rows)    85%   (97 rows)
     220     82%  (255 rows)    86%  (243 rows)

    ~rows    shipped/170        v512
      60     70%   (47 rows)    63%   (50 rows)     gloss pick
     100     75%  (119 rows)    68%  (110 rows)
     220     81%  (170 rows)    71%  (166 rows)

**The result splits by which picker opens the file, and that is the whole
finding.** Picked by the mean of what landed in a drawer, 512 wins at every
budget and the cheap end excludes zero: +11.5pp, CI [+2.3, +20.7], P 99%.
Picked by the written line, it loses: -6.9, -6.9, -10.3pp, and only the
expensive point excludes zero.

So the drawers are better and the glosses that open them are not. That is
the cold-start problem arriving on schedule -- `file_v4` already measured
that a gloss derived from one row is 12pp worse than one derived from ten,
and a 480-drawer shelf reaches ten rows a drawer far later than a 170-pair
one does. Centroid is what the archive can offer after it fills; the written
line is what day one has.

**The prefix ablation, which was meant to explain the gap and only half
does.** `gloss_v4.gloss_text` puts `cat / topic: ` in front of every gloss.
On a comma list that prefix is most of the grammar; on a sentence it is a
label bolted to the front of one. Removing it:

    ~rows    v512 prefixed   v512 bare     pick
      60     63%             67%           gloss
     100     68%             71%
     220     71%             81%
      60     81%             78%           centroid
     220     86%             83%

It recovers the whole gloss-pick loss at the expensive end (81% against the
shipped shelf's 81%, +0.0pp) and costs a little on centroid, so it is a wash
overall and the prefix stays. What it does establish is that the remaining
cold gap is a property of the LINES, not of the shelf.

The register hypothesis is the untested one and it is next. A row embeds as
a short third-person declarative -- "The employee's contract ended on
December 31st" -- and all 512 glosses are written first person, "I bought a
lathe for the workshop". gloss_v4's own docstring made this argument about
descriptive glosses and it applies with more force here: the glosses are in
a register no row and no question is ever in. Rewriting them third person is
one pass over one file and would be measured by re-running this script.

**Verdict: 512 ships.** Populated, it is better at every budget and the cheap
end is the only interval in the batch that excludes zero. Cold, it is a wash
once the prefix is off. And it reaches those numbers with a third of the rows
per file -- 3.2 against 9.2 -- which is the facts-per-file mechanism from
batch 12 pointing the same way it has every time it has been asked.

## Batch 21 — the register hypothesis, and it does not survive

`register_v5.py`. Batch 20 named one explanation for v512's weak cold pick:
all 512 glosses are first person while every row embeds as a third-person
declarative. Rewriting 480 lines by hand is a large write, so the cheap
version ran first -- a mechanical pronoun swap, plus an arm that drops the
`cat / topic:` prefix for `cat topic. ` so the gloss has the exact shape of
`read_bench.embed_text`.

    arm                open   gloss   centroid   reach
    1st, prefixed         3     56%       79%       20
    1st, prefixed         5     60%       81%       33
    1st, prefixed         8     63%       85%       50
    1st, prefixed        20     68%       86%      110
    3rd, prefixed         3     48%       74%       20
    3rd, prefixed         5     51%       80%       35
    3rd, prefixed         8     58%       82%       54
    3rd, prefixed        20     68%       87%      108
    3rd, row-shaped       3     52%       74%       16
    3rd, row-shaped       5     57%       81%       26
    3rd, row-shaped       8     60%       82%       39
    3rd, row-shaped      20     72%       92 rows  <- best gloss point

Third person is WORSE or level at every point on both pickers. The one
number that beats the first-person arm is row-shaped at 20 files, 72%
against 68%, and it gets there on 92 rows against 110 -- inside noise on 87
questions, and the same arm is 3pp down on centroid.

The caveat, and it cuts one way: the conversion emits "the person" 480
times where a real row says "the employee" or "the student", so it adds a
constant token a hand rewrite would not have. So this does not prove
register cannot matter. It does say the large effect predicted in batch 20
is not there to collect, and a 480-line hand rewrite is no longer justified
by a measurement -- only by taste, which is the thing this log exists to
not spend on.

The prediction was mine and it was wrong. v512 ships with the first-person
glosses as written.
