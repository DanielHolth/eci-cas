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
