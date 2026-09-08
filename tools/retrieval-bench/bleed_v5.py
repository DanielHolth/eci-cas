"""Which drawers are catch-alls? Measured, not argued. No LLM.

    python bleed_v5.py [path-to-vocabulary]

Five outside models reviewed docs/vocabulary/v512.txt and four of them
independently flagged `moment` as a second `other` -- a drawer defined by
narrative significance rather than by subject, which would absorb overflow
from work, health, travel and family alike. That is exactly the property
the draft already rejected `preference` for, so the claim deserves a number
rather than a vote.

The test. Every v4 row already carries the category the Cataloger actually
filed it under. File the same rows into the candidate shelf by cosine, and
for each candidate drawer look at the SPREAD of source categories its rows
came from. A drawer serving one subject draws from one or two sources. A
catch-all draws from many, because that is what being a catch-all means.

    sources     distinct shipped categories contributing to this drawer
    H           Shannon entropy of that mix, in bits. 0 = one source.
    conc        share of the drawer from its single largest source.

A drawer with volume AND high H is the defect. High H on three rows is
noise, so nothing under MIN_ROWS is reported.

This measures label geometry, not corpus taste: it asks where the embedder
puts rows, and the embedder is what will do the filing in production. The
usual caveat still applies -- the corpus is one synthetic household written
against the 170-pair shelf, so a candidate drawer for a subject the corpus
lacks cannot be convicted here, only acquitted.
"""
import sys, math, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from embed import Embedder
from arms_v4 import CACHE
from flat_v5 import parse

MIN_ROWS = 8


def entropy(counter):
    total = sum(counter.values())
    return -sum((n / total) * math.log2(n / total) for n in counter.values())


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

    _, rows = rb.archive(CACHE)
    flat, source = [], []
    for p in sorted(rows):
        for r in rows[p]:
            flat.append(r)
            source.append(p.split("/")[0])

    e = Embedder()
    R = e.encode([rb.embed_text(r) for r in flat], kind="passage")
    P = e.encode(summarise(pairs), kind="passage")
    labels = np.argmax(R @ P.T, axis=1)

    mix = collections.defaultdict(collections.Counter)
    for i, lab in enumerate(labels):
        mix[pairs[lab]][source[i]] += 1

    # Per-drawer, then rolled up per candidate CATEGORY -- the category is
    # what gets frozen, so that is the level the verdict belongs at.
    print("drawers with %d+ rows, most mixed first" % MIN_ROWS)
    print("  %-30s %5s %7s %6s %6s  %s" % ("drawer", "rows", "sources", "H", "conc", "top sources"))
    scored = []
    for pair, c in mix.items():
        n = sum(c.values())
        if n < MIN_ROWS:
            continue
        scored.append((entropy(c), n, pair, c))
    for H, n, pair, c in sorted(scored, reverse=True)[:15]:
        top = ", ".join("%s %d" % (k, v) for k, v in c.most_common(3))
        print("  %-30s %5d %7d %6.2f %5d%%  %s"
              % (pair, n, len(c), H, 100 * c.most_common(1)[0][1] // n, top))

    print("")
    print("by candidate category (rows filed there, from how many sources)")
    print("  %-14s %6s %7s %6s %6s" % ("category", "rows", "sources", "H", "conc"))
    bycat = collections.defaultdict(collections.Counter)
    for pair, c in mix.items():
        bycat[pair.split("/")[0]].update(c)
    out = []
    for cat, c in bycat.items():
        n = sum(c.values())
        out.append((entropy(c), n, cat, c))
    for H, n, cat, c in sorted(out, reverse=True):
        if n < MIN_ROWS:
            continue
        print("  %-14s %6d %7d %6.2f %5d%%" % (cat, n, len(c), H, 100 * c.most_common(1)[0][1] // n))

    print("")
    print("  H is bits over the 10 shipped categories, so 3.32 is the ceiling.")
    print("  A subject drawer sits near 0-1. A catch-all sits near the ceiling")
    print("  while holding real volume -- that pair of properties is the defect.")


if __name__ == "__main__":
    main(*sys.argv[1:2])
