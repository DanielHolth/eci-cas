"""Pick the file with vectors, then the row. No LLM anywhere in the read path.

    python shelf_v4.py [k] [files]      # default k=5 rows, 3 files

Two ways to summarise a file so it can be ranked as a unit:

    centroid    the mean of its row vectors, one vector per file
    sample-n    n of its row vectors kept; the file scores as its single
                best-matching row

This is the architecture the earlier arms skipped past. `cosine` narrows with
an LLM reading file names and `flat` does not narrow at all; both were
measured, and neither is what a retrieval system normally does. The normal
thing is a coarse pass over file summaries and a fine pass over the rows in
the few files that survive -- and it keeps the shelf. Categories, topics,
Merged() addressing and human-legible file names all survive here. What gets
deleted is the Librarian *call*, not the vocabulary, which is a much smaller
claim than "drop the pairs" and worth testing before the larger one.

It is also the only arm here that answers the scale objection. `flat` beat
everything on 1559 rows, but a flat sweep is linear in the archive and the
regime that matters is a hundred thousand rows with near-duplicates
accumulating. This arm sweeps 170 file vectors and then a few hundred rows,
which is the shape that survives growth. If it matches flat at this size,
that is the interesting result, because only one of the two keeps matching it
later.

centroid versus sample-n is not a tuning choice, it is the fat-file question.
Averaging seventy-four unrelated rows lands near nothing in particular, so a
centroid should degrade exactly where v4 put its fat files -- and v4 put gold
in them on purpose. sample-n scores a file by its best row, so a fat file is
found by whichever of its rows actually matches. If centroid loses to
sample-n, the reason is legible and the fix is not a bigger model.

The comparison is deliberately unfair to this arm in one way: `files` is
fixed, so it opens the same number of files whether or not it is confident,
and cannot decline. That matches how `cosine` and `flat` are scored and keeps
the three readable in one table.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import retrieval_v4 as corpus
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, answered, gold_index, shape
from cosine_v4 import answered_row


def main(k=5, files=3):
    a, rows = rb.archive(CACHE)
    index = sorted(rows)
    gold = gold_index(a)
    print("archive: " + shape(rows))

    flat_rows, owner = [], []
    for p in index:
        for r in rows[p]:
            flat_rows.append(r)
            owner.append(index.index(p))
    owner = np.array(owner)

    e = Embedder()
    print("embedding %d rows ..." % len(flat_rows), flush=True)
    matrix = e.encode([rb.line(r) for r in flat_rows])

    # One centroid per file, and a fixed sample of each file's rows. The
    # sample is the first n in stored order rather than a random draw: the
    # archive order is the order the padding was generated in, which is
    # arbitrary with respect to the questions, and a seeded draw would add a
    # knob without adding independence.
    per_file = [np.flatnonzero(owner == i) for i in range(len(index))]
    cent = np.vstack([matrix[ix].mean(0) if len(ix) else np.zeros(matrix.shape[1])
                      for ix in per_file])
    cent /= np.maximum(np.linalg.norm(cent, axis=1, keepdims=True), 1e-9)

    qs = sorted(ANSWERS)
    turns = qs + list(corpus.NULLS)
    qv = e.encode(turns)
    print("embedded. %d turns\n" % len(turns), flush=True)

    samples = (1, 3, 10)
    arms = ["centroid"] + ["sample-%d" % s for s in samples]
    tally = collections.Counter()

    for turn, v in zip(turns, qv):
        is_null = turn not in ANSWERS
        scores = matrix @ v

        picks = {"centroid": np.argsort(-(cent @ v))[:files]}
        for s in samples:
            # A file's score is its best row among the n kept. -inf for an
            # empty file so it never wins a slot by default.
            best = np.array([float(scores[ix[:s]].max()) if len(ix) else -1e9
                             for ix in per_file])
            picks["sample-%d" % s] = np.argsort(-best)[:files]

        for name in arms:
            chosen = picks[name]
            pool = np.flatnonzero(np.isin(owner, chosen))
            idx = pool[np.argsort(-scores[pool])[:k]] if len(pool) else np.array([], int)
            got = [flat_rows[i] for i in idx]
            tally[name + ".rows"] += len(got)
            if is_null:
                continue
            tally[name + ".answer"] += answered(got, turn)
            tally[name + ".strict"] += answered_row(got, turn)
            # Did the coarse pass open a file that actually holds the answer?
            # This is the same quantity `select` reports for the Librarian, so
            # the two narrowings are directly comparable.
            tally[name + ".select"] += bool(gold[turn] & set(index[i] for i in chosen))

    n = len(qs)
    print("questions %d   rows k=%d   files opened=%d" % (n, k, files))
    print("\n  %-10s %8s %8s %8s %8s" % ("arm", "select", "joint", "strict", "rows"))
    for name in arms:
        print("  %-10s %7d%% %7d%% %7d%% %8.1f" % (
            name,
            100 * tally[name + ".select"] // n,
            100 * tally[name + ".answer"] // n,
            100 * tally[name + ".strict"] // n,
            tally[name + ".rows"] / float(len(turns))))
    print("\n  select = share of questions where an opened file holds the answer.")
    print("           Directly comparable to the Librarian's own select number.")
    print("  joint / strict / rows as in cosine_v4.")


if __name__ == "__main__":
    args = [int(x) for x in sys.argv[1:3] if x.isdigit()]
    main(*(args or [5]))
