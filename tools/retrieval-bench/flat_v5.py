"""How evenly does a vocabulary spread the writes? No LLM, no archive build.

    python flat_v5.py [path-to-vocabulary]     # default docs/vocabulary/v512.txt

Daniel's objective for the 512-pair shelf, stated directly: make the pair
landscape as flat as possible -- every drawer taking roughly the same share
of the writes. Labels are the means, not the goal. So the vocabulary needs a
number attached to it and not a taste argument, and this is the number.

Every row of the v4 corpus is filed into its nearest pair by cosine, exactly
as file_v4's `by-gloss` arm does, and the occupancy histogram is reported:

    fill        share of pairs that took at least one row
    top-1       share of all rows landing in the single fattest pair.
    top-10%     share landing in the fattest TENTH of drawers. Flat reads
                10% at any shelf size, so this is the column that compares
                two shelves; a raw "fattest ten pairs" does not.
    gini        0 is perfectly flat, 1 is one drawer holding everything.
    p50/p95     rows in the median and the 95th-percentile drawer. p95 is
                what a parquet whole-file rewrite actually costs.

READ THE EMPTIES WITH CARE -- they are mostly an artifact and not a verdict.
The v4 corpus is one synthetic household of 1559 rows written AGAINST the
170-pair vocabulary, whose ten categories are a subset of these thirty-two.
So `mood`, `media`, `moment` and `plan` are near-empty here because nothing
in the corpus is a memory or a mood, not because the drawers are wrong. A
corpus cannot tell you a drawer is unnecessary when it was never given
anything to put in it.

What the corpus CAN tell you, and what this is for:

    - collisions. Two drawers whose vectors sit on top of each other split
      what should be one, or one drawer eating a neighbour's rows. That is
      visible with any corpus, because it is a fact about the labels.
    - fat drawers. A pair holding 5% of everything is the one to split, and
      the corpus is honest about that for the subjects it does cover.

512 against 170 is also not a fair fight on fill by construction: three
times the drawers over the same rows cannot help but leave more of them
empty. Compare the shapes on gini and top-10, which are scale-free, and
treat fill as a coverage report on the corpus rather than on the shelf.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from embed import Embedder
from arms_v4 import CACHE


def parse(path):
    """category: topic topic ... , continuation lines indented. As cataloger.txt."""
    cats, cur = {}, None
    body = open(path, encoding="utf-8").read().split("## vocabulary", 1)[-1]
    for raw in body.split("\n"):
        line = raw.rstrip()
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith(" ") and ":" in line:
            cur, rest = line.split(":", 1)
            cats[cur] = rest.split()
        elif cur:
            cats[cur] += line.split()
    return cats


def gini(counts):
    """Standard Gini over drawer occupancies. Empty drawers count -- a shelf
    that leaves half its addresses unused is not flat, it is half a shelf."""
    x = np.sort(np.asarray(counts, float))
    n = len(x)
    if x.sum() == 0:
        return 0.0
    return float((2 * np.arange(1, n + 1) - n - 1).dot(x) / (n * x.sum()))


def report(name, pairs, labels, total):
    counts = np.bincount(labels, minlength=len(pairs))
    order = np.argsort(-counts)
    # Share of rows in the fattest tenth of drawers. THE column to compare
    # shelves on: "top-10 pairs" is not scale-free -- ten of 512 is a fifth
    # of the shelf slice that ten of 171 is -- so a bigger shelf flatters
    # itself on it. A perfectly flat shelf reads 10% here at any size.
    d = max(1, len(pairs) // 10)
    # The same thing over the drawers the corpus actually reached. The plain
    # column above charges a shelf for every address the corpus had nothing
    # to put in, which on a 512 shelf fed by a 170-shelf corpus is most of
    # the gap. This one asks the narrower and answerable question: of the
    # subjects this corpus does cover, is the shelf spreading them evenly?
    live = counts[counts > 0]
    lo = np.sort(live)[::-1][:max(1, len(live) // 10)].sum()
    print("  %-12s %5d %6d%% %6d%% %6d%% %6d%% %6.3f %6d %6d"
          % (name, len(pairs),
             100 * int((counts > 0).sum()) // len(pairs),
             100 * int(counts[order[0]]) // total,
             100 * int(counts[order[:d]].sum()) // total,
             100 * int(lo) // total,
             gini(counts), int(np.median(counts)),
             int(np.percentile(counts, 95))))
    return counts, order


def summarise(pairs):
    """Bare path or gloss, and the difference is the whole point.

    Round 4 measured a shelf twice with the same topics and got 10 of 20
    against 18 of 20, because a bi-encoder compares a sentence to a
    sentence and `leisure/making` is not one. Every number this file
    printed before the gloss block existed was therefore a floor. Falls
    back to the bare path if the gloss file is absent, so the old
    behaviour is still reachable and still comparable.
    """
    try:
        from build_gloss import load_gloss
        g = load_gloss()
    except Exception:
        return [p.replace("/", " / ") for p in pairs]
    return ["%s: %s" % (p, g[p]) if p in g else p.replace("/", " / ") for p in pairs]


def main(path=None):
    path = path or (ROOT + "docs/vocabulary/v512.txt")
    cats = parse(path)
    pairs = ["%s/%s" % (c, t) for c in sorted(cats) for t in cats[c]]
    print("%s: %d categories, %d pairs" % (path.rsplit("/", 1)[-1], len(cats), len(pairs)))

    _, rows = rb.archive(CACHE)
    flat = [r for p in sorted(rows) for r in rows[p]]
    print("corpus: %d rows over %d shipped pairs\n" % (len(flat), len(rows)), flush=True)

    e = Embedder()
    # The row embeds as line+sentence, the same text file_v4 files on, so
    # this measures the shelf and not a different reading of the corpus.
    R = e.encode([rb.embed_text(r) for r in flat], kind="passage")
    # No gloss exists yet for these 512, so the pair is summarised by its own
    # path -- the `name-only` arm of shelf_v4, its weakest summary. Every
    # number here is therefore a floor: a written gloss can only spread the
    # rows more evenly than the bare label does.
    P = e.encode(summarise(pairs), kind="passage")

    print("  %-12s %5s %6s %6s %6s %6s %6s %6s %6s"
          % ("shelf", "pairs", "fill", "top-1", "top10%", "live10%", "gini", "p50", "p95"))
    labels = np.argmax(R @ P.T, axis=1)
    counts, order = report("candidate", pairs, labels, len(flat))

    # The shipped shelf, filed the same way, as the control. Without it the
    # numbers above have no scale: gini 0.6 could be good or a disaster.
    shipped = sorted(rows)
    S = e.encode([p.replace("/", " / ") for p in shipped], kind="passage")
    report("shipped/170", shipped, np.argmax(R @ S.T, axis=1), len(flat))
    # And the shelf as the archive was actually written, which is the
    # Cataloger's own filing rather than any vector's.
    truth = np.array([shipped.index(p) for p in sorted(rows) for _ in rows[p]])
    report("as-written", shipped, truth, len(flat))

    print("")
    print("  fattest drawers in the candidate shelf")
    for i in order[:12]:
        print("    %-34s %4d rows  %2d%%" % (pairs[i], counts[i], 100 * counts[i] // len(flat)))

    empty_by_cat = collections.Counter()
    for i, p in enumerate(pairs):
        if counts[i] == 0:
            empty_by_cat[p.split("/")[0]] += 1
    print("")
    print("  categories the corpus never reached (empty topics of 16)")
    for c, n in empty_by_cat.most_common(12):
        print("    %-14s %2d" % (c, n))
    print("")
    print("  Empties are corpus coverage, not shelf quality -- see the docstring.")
    print("  Judge the shelf on top-10%, which reads 10% for a flat shelf at")
    print("  any size. gini is reported but counts empty drawers, so on this")
    print("  corpus it penalises the bigger shelf for subjects it simply lacks.")


if __name__ == "__main__":
    main(*sys.argv[1:2])
