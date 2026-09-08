"""How far does "more files is better" hold? Daniel's question, batch 17.

    python gran_v4.py [k]

Every shelf comparison so far has moved two things at once: 34 pairs against
170 pairs changes the number of files *and* the vocabulary that names them.
The finding was that the finer shelf wins at matched reach, and the mechanism
was facts-per-file. If that mechanism is the whole story then it should keep
paying past 170, and the only way to ask is to hold the vocabulary constant
and vary K alone.

So the files here are not a vocabulary. K-means over the 1559 row vectors,
K from 12 to 1559, seeded. That is not shippable -- nobody can browse an
archive whose folders have no names -- but it isolates granularity from
naming, which no pair of hand-written shelves can. The real shelves are
printed beside the curve as reference points.

K = 1559 is one row per file, which is `shelf_v4`'s flat arm: the ceiling this
curve is climbing towards.

Matched on reach throughout, because that is the control the shelf work
established: a coarse shelf looks good at equal file count only because a
file of 80 rows is not comparable to a file of 9.

It holds all the way. There is no plateau below one row per file:

       K rows/file   ~20 rows  ~60 rows  ~120 rows  ~240 rows   to 85%
      12    129.9      55%       55%        55%        72%        687
      34     45.9      51%       51%        69%        78%        610
      85     18.3      56%       74%        78%        85%        272
     171      9.1      66%       76%        84%        86%        235
     342      4.6      74%       85%        86%        86%         61
     680      2.3      82%       86%        86%        87%         45
    1559      1.0      87%       87%        87%        87%          3

Read the last column, not the middle ones. Finer files do not raise the
ceiling -- every K above 85 gets within 2pp of flat's 87% if you let it open
enough rows -- they lower the *price* of getting near it. 85% costs 687 rows
at K=12, 235 at the shipped shelf's granularity, and 61 at twice that. Since
those rows are what reaches Intent, the column is a token bill.

The knee is right above where we ship. Between K=171 and K=342 the price of
85% falls from 235 rows to 61: a 4x saving for a 2x finer shelf, the sharpest
return anywhere in the table. Above 342 it flattens -- 680 buys 45 and one
row per file buys 3, but by then the coarse pass is scoring 680 or 1559
centroids and has become the flat scan it was meant to avoid.

**What changed is that this is now actionable, and it was not before.** The
reason never to go finer than 170 was that the Cataloger has to choose from
the list, and batches 8-12 measured a 4B model getting worse as the list grew.
Batch 16b takes the model out of that loop: a written gloss files as well as
two LLM calls do. Once arithmetic does the filing, "too many pairs to choose
from" stops being a cost, and the ~340-pair shelf this table points at becomes
writable. The remaining cost of going finer is human, not model -- 340 pairs
is 340 glosses to write and a folder tree a person still has to recognise.

**Is 340 parquet files a problem? It is cheaper than 171, on both IO axes.**
`ParquetArchiveStore` is one file per pair, created lazily, with the file name
as the index. The cost of a read is the files it opens; the cost of a write is
rewriting one whole file. Measured at the budget that reaches 85%:

        K   files opened   rows read   p95 file
       85        12           272         36
      171        20           235         21
      342         8            61         10
      680        12            45          5

Going finer *reduces* file opens, because a sharper file needs fewer of them
to cover the answer -- 8 opens at K=342 against 20 at K=171. It halves the
write cost too, since a rewrite is proportional to file size and p95 goes from
21 rows to 10. And 340 files of ~5KB is about 1.7MB of directory, which is
nothing. The scale objection to a finer shelf is a read-model objection, not a
storage one, and the read model gets better.

**Category-then-topic, or one flat pick?** One flat pick. `shelf_v4` measured
the staging directly with centroids at both levels, so only the staging
varies: keeping the top 3 categories scores 65% against 72% for ranking all
files at once, and the staged arm is monotone up to the flat arm and never
past it. A gate can only discard what the second stage would have ranked, and
a category centroid is a blurrier vector than any file centroid inside it.

That answer holds at 342 because the flat pick is 342 dot products against a
384-dim vector -- microseconds, and far below the cost of opening one parquet
file. Staging exists to avoid scoring every file, and there is still nothing
to avoid. It would become a real question at 100k pairs, and `shelf_v4`'s
table is the price list for that day.

Worth separating: the *directory layout* stays hierarchical either way. Files
are named category~topic and a person still browses them as a tree. Nothing
about picking flatly requires storing flatly, and the pick order is not the
folder order.

Two things this cannot say. The files here have no names, so it does not
follow that a *hand-written* 340-pair vocabulary would cut the same way -- the
clusters are optimal for the embedder by construction. And 1559 is the flat
arm, which `shelf_v4` already showed is not sublinear in the archive; it is
the ceiling in this table, not a proposal.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, shape
from cosine_v4 import answered_row

KS = (12, 34, 85, 171, 342, 680, 1559)
TARGETS = (20, 60, 120, 240)


def kmeans(X, K, seed=4, iters=40):
    """Spherical k-means. Vectors are L2-normalised, so cosine is a dot and a
    re-normalised mean is the right centroid."""
    rng = np.random.default_rng(seed)
    C = X[rng.choice(len(X), K, replace=False)].copy()
    lab = np.zeros(len(X), int)
    for _ in range(iters):
        new = np.argmax(X @ C.T, axis=1)
        if (new == lab).all():
            break
        lab = new
        for i in range(K):
            m = lab == i
            # An emptied cluster is re-seeded on the point furthest from its
            # own centroid, so K stays K rather than silently collapsing.
            C[i] = X[m].mean(0) if m.any() else X[rng.integers(len(X))]
        C /= np.maximum(np.linalg.norm(C, axis=1, keepdims=True), 1e-9)
    return lab, C


def sweep(rowsv, flat, qv, qs, lab, C, k):
    """strict and reach for every file budget, so reach can be matched later."""
    K = len(C)
    out = []
    sizes = np.bincount(lab, minlength=K)
    for files in sorted(set([1, 2, 3, 5, 8, 12, 20, 32, 50, 80, 128, 200, 320,
                             512, 800, 1559]) - set(range(K + 1, 2000))):
        strict = reach = 0
        for q, v in zip(qs, qv):
            chosen = np.argsort(-(C @ v))[:files]
            pool = np.flatnonzero(np.isin(lab, chosen))
            reach += len(pool)
            top = pool[np.argsort(-(rowsv[pool] @ v))[:k]] if len(pool) else []
            strict += answered_row([flat[i] for i in top], q)
        out.append((files, reach / float(len(qs)), 100.0 * strict / len(qs)))
    return out


def main(k=5):
    a, rows = rb.archive(CACHE)
    print("archive: " + shape(rows))
    flat = [r for p in sorted(rows) for r in rows[p]]
    e = Embedder()
    print("embedding %d rows ..." % len(flat), flush=True)
    rowsv = e.encode([rb.embed_text(r) for r in flat], kind="passage")
    qs = sorted(ANSWERS)
    qv = e.encode(qs, kind="query")

    print("\n  strict at matched reach, k=%d rows to Intent" % k)
    print("  %6s %8s   %s %8s" % ("K", "rows/file",
          "  ".join("~%d rows" % t for t in TARGETS), "to 85%"))
    for K in KS:
        if K >= len(flat):
            lab, C = np.arange(len(flat)), rowsv.copy()
        else:
            lab, C = kmeans(rowsv, K)
        sw = sweep(rowsv, flat, qv, qs, lab, C, k)
        cells = []
        for t in TARGETS:
            b = min(sw, key=lambda r: abs(r[1] - t))
            cells.append("%3.0f%% (%.0f)" % (b[2], b[1]))
        # The cheapest budget that reaches 85%, which is 2pp off the flat
        # ceiling. This is the number the curve is really about: finer files
        # do not raise the ceiling, they lower the price of getting near it.
        ok = [r for r in sw if r[2] >= 85.0]
        cost = ("%.0f" % min(ok, key=lambda r: r[1])[1]) if ok else "never"
        print("  %6d %8.1f   %s %8s" % (K, len(flat) / float(K),
                                        "  ".join("%-11s" % c for c in cells),
                                        cost))


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
