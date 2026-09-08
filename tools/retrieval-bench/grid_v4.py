"""32 categories x 16 topics = 512 pairs. Daniel's proposal, batch 18.

    python grid_v4.py [k]

`gran_v4` put the knee just above the shipped shelf: K=342 buys 85% for 61
rows where K=171 needs 235, and K=680 only takes that to 45. Daniel proposes
fixing the number with a binary shape -- 32 categories of 16 topics, 512
pairs -- so the size has a reason rather than being tuned.

Two questions, and only the second is really about 512.

    free-512     512-means over the rows. Optimal allocation, unnamed.
    grid-32x16   32-means, then exactly 16 subclusters inside each. Same
                 total, but resolution spread evenly whether a category
                 holds 400 rows or 12.

The grid is the proposal's real content. A binary shape is not just a round
number: it forces every category to spend the same resolution, and archives
are not uniform -- this one has files of 74, 70 and 65 rows against a median
of 3. Whether that uniformity costs anything is the question, and the free
arm is the ceiling it is measured against.

Empty pairs are the other half. In `ParquetArchiveStore` an unused pair costs
nothing on disk -- files are created lazily -- but it is not free to a vector
filer: an empty pair still ships a gloss, still competes for a slot in the
pick, and a slot spent on an empty file is a slot that reads nothing. The
grid guarantees empties in a way free clustering never does.
"""
import sys
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import gran_v4
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, shape
from cosine_v4 import answered_row


def grid(X, cats, topics, seed=4):
    """cats-means, then exactly `topics` subclusters inside each category.

    A category with fewer rows than `topics` still gets `topics` slots; the
    surplus are empty, which is precisely the cost being measured."""
    lab, _ = gran_v4.kmeans(X, cats, seed)
    out = np.zeros(len(X), int)
    cents = []
    for c in range(cats):
        ix = np.flatnonzero(lab == c)
        base = c * topics
        if len(ix) > topics:
            sub, C = gran_v4.kmeans(X[ix], topics, seed)
            out[ix] = base + sub
            cents.append(C)
        else:
            # Fewer rows than slots: one row each, the rest stay empty.
            out[ix] = base + np.arange(len(ix))
            C = np.zeros((topics, X.shape[1]))
            C[:len(ix)] = X[ix]
            cents.append(C)
    return out, np.vstack(cents)


def price(rowsv, flat, qv, qs, lab, C, k, want=85.0):
    """Cheapest budget reaching `want`, as (files opened, rows read)."""
    for files in (1, 2, 3, 5, 8, 12, 20, 32, 50, 80, 128, 200, 320):
        if files > len(C):
            break
        st = rc = 0
        for q, v in zip(qs, qv):
            ch = np.argsort(-(C @ v))[:files]
            pool = np.flatnonzero(np.isin(lab, ch))
            rc += len(pool)
            top = pool[np.argsort(-(rowsv[pool] @ v))[:k]] if len(pool) else []
            st += answered_row([flat[i] for i in top], q)
        if 100.0 * st / len(qs) >= want:
            return files, rc / float(len(qs)), 100.0 * st / len(qs)
    return None, None, None


def main(k=5):
    a, rows = rb.archive(CACHE)
    print("archive: " + shape(rows))
    flat = [r for p in sorted(rows) for r in rows[p]]
    e = Embedder()
    print("embedding %d rows ..." % len(flat), flush=True)
    V = e.encode([rb.embed_text(r) for r in flat], kind="passage")
    qs = sorted(ANSWERS)
    qv = e.encode(qs, kind="query")

    arms = []
    for K in (342, 512, 680):
        lab, C = gran_v4.kmeans(V, K)
        arms.append(("free-%d" % K, lab, C))
    for cats, tops in ((32, 16), (16, 32), (24, 16)):
        lab, C = grid(V, cats, tops)
        arms.append(("grid-%dx%d" % (cats, tops), lab, C))

    print("\n  %-12s %6s %8s %10s %10s %8s" % (
        "shelf", "pairs", "empty", "files@85%", "rows@85%", "p95"))
    for name, lab, C in arms:
        sizes = np.bincount(lab, minlength=len(C))
        f, r, got = price(V, flat, qv, qs, lab, C, k)
        print("  %-12s %6d %7d%% %10s %10s %8.0f" % (
            name, len(C), 100 * int((sizes == 0).sum()) // len(C),
            f if f else "never", "%.0f" % r if r else "-",
            np.percentile(sizes[sizes > 0], 95)))

    print("\n  empty = pairs holding no rows. They cost nothing on disk but")
    print("          still ship a gloss and still compete for a pick slot.")
    print("  files/rows@85%% = cheapest budget reaching 85%% strict, k=%d." % k)


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
