"""Pick the file with vectors, then the row. No LLM anywhere in the read path.

    python shelf_v4.py [k] [files]      # default k=5 rows, 3 files

Ways to summarise a file so it can be ranked as a unit, cheapest first:

    name-only   the file's own name, embedded. One vector, no content.
    centroid    the mean of its row vectors. One vector.
    sample-n    n rows drawn at random; the file scores as its best of them.
    all-rows    every row votes; the file scores as its single best row.

Daniel's idea, and the reason this file exists: give the picker samples of
what is in each file rather than only the name. It works -- but not in the
form first measured here, and not for the reason first claimed.

    method        vectors/file   strict     (3 files opened, k=5, of 87)
    librarian          0           40%      LLM reading the name
    name-only          1           50%
    sample-3           3           63%
    centroid           1           70%
    sample-10         10           75%
    all-rows       1 per row       80%
    flat           1 per row       78%      no files at all

Three things to keep straight.

**A summary vector beats an LLM reading the name, and the cheapest one is
best.** centroid at one vector a file scores 70% against the Librarian's 40%,
and beats a three-row sample costing three times as much. Even the file name
embedded rather than read by a model is worth 10pp. The Librarian call is not
losing because names are uninformative; it is losing because a 4B model
ranking 170 names is worse than arithmetic on the same information.

**centroid was written off here on the first pass and that was wrong.** The
sample arms took the first n rows in stored order, justified as "arbitrary
with respect to the questions". It is not: build() appends gold before
padding, so 95% of gold sits at index 0-2 and "the first three rows" is a
pointer to the answer. That arm read 88%; blind sampling reads 63%. The
sample must be blind to what is being retrieved or it is not a sample.

**all-rows does not answer the scale objection**, though an earlier version of
this docstring claimed it did. Scoring a file by its best row requires a
vector per row and a similarity against every one of them -- the same sweep
flat does. What it saves is what reaches Intent, not what gets scanned. Only
name-only, centroid and sample-n are sublinear in the archive, and they cost
10-30pp against flat. That trade is the real finding, and which side of it to
take is a tier decision rather than a correctness one.

The comparison is deliberately unfair to every arm here in one way: `files` is
fixed, so each opens the same number of files whether or not it is confident,
and none can decline. That matches how `cosine` and `flat` are scored and
keeps them readable in one table.
"""
import sys, collections, random
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

    # One centroid per file, and a sample of each file's rows.
    #
    # The sample is a seeded random draw, NOT the first n. The first version
    # of this arm took the first n "because stored order is arbitrary with
    # respect to the questions". It is not: build() appends gold rows before
    # padding, so 95% of gold sits at index 0-2 and "the first three rows" is
    # a pointer to the answer rather than a sample of the file. That arm read
    # 88% and the honest number is 63%. Sampling has to be blind to the thing
    # being retrieved or it is not sampling.
    per_file = [np.flatnonzero(owner == i) for i in range(len(index))]
    draw = random.Random(4)
    sampled = {s: [np.array(draw.sample(list(ix), min(s, len(ix))), dtype=int)
                   if len(ix) else np.array([], int) for ix in per_file]
               for s in (1, 3, 10)}
    cent = np.vstack([matrix[ix].mean(0) if len(ix) else np.zeros(matrix.shape[1])
                      for ix in per_file])
    cent /= np.maximum(np.linalg.norm(cent, axis=1, keepdims=True), 1e-9)

    names = e.encode([p.replace("/", " / ") for p in index])
    qs = sorted(ANSWERS)
    turns = qs + list(corpus.NULLS)
    qv = e.encode(turns)
    print("embedded. %d turns\n" % len(turns), flush=True)

    samples = (1, 3, 10)
    arms = ["name-only", "centroid"] + ["sample-%d" % s for s in samples] + ["all-rows"]
    tally = collections.Counter()

    for turn, v in zip(turns, qv):
        is_null = turn not in ANSWERS
        scores = matrix @ v

        picks = {"centroid": np.argsort(-(cent @ v))[:files]}
        for s in samples:
            # A file's score is its best row among the n sampled. -inf for an
            # empty file so it never wins a slot by default.
            best = np.array([float(scores[ix].max()) if len(ix) else -1e9
                             for ix in sampled[s]])
            picks["sample-%d" % s] = np.argsort(-best)[:files]
        # No sampling at all: every row votes for its file. This is the
        # ceiling the samples are approximating, and it costs one vector per
        # row -- the same vectors flat already needs, so it saves nothing on
        # the sweep. It saves on what reaches Intent, not on what is scanned.
        allbest = np.array([float(scores[ix].max()) if len(ix) else -1e9
                            for ix in per_file])
        picks["all-rows"] = np.argsort(-allbest)[:files]
        # And the Librarian's own information, as a floor: the file name and
        # nothing else, scored the same way.
        picks["name-only"] = np.argsort(-(names @ v))[:files]

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
