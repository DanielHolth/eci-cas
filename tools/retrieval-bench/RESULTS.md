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
