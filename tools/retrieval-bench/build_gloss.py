"""Validate docs/vocabulary/v512-gloss.txt against the vocabulary, and
measure what the glosses buy.

    python build_gloss.py            # validate + measure
    python build_gloss.py --check    # validate only, no embedder

Validation is the point of the script existing. A gloss file drifts the
moment a topic is renamed, and a missing line is invisible -- the pair
simply falls back to its bare path and quietly matches nothing, which is
exactly the 1-of-11 failure that motivated the file. So: every pair has a
line, every line names a real pair, nothing appears twice.

The measurement re-runs the round-4 probe over the WHOLE shelf rather
than the partial gloss set that produced the original figure, so the
number here supersedes it.
"""
import io, sys

ROOT = __file__.rsplit("tools", 1)[0]
VOCAB = ROOT + "docs/vocabulary/v512.txt"
GLOSS = ROOT + "docs/vocabulary/v512-gloss.txt"

sys.path[:0] = [ROOT + "tools/retrieval-bench"]
from flat_v5 import parse

# Hobby sentences from round 4, plus the record and workplace probes that
# settled those two categories. Each is (sentence, expected category).
PROBES = [
    ("I spent all Saturday in the greenhouse potting seedlings", "leisure"),
    ("bought a new lathe for the workshop", "leisure"),
    ("my chess rating went up to 1600", "leisure"),
    ("I collect vintage postcards", "leisure"),
    ("the running club meets on Thursdays", "leisure"),
    ("finished the oak bookshelf I was building", "leisure"),
    ("birdwatching is my main hobby", "leisure"),
    ("I do the crossword every morning", "leisure"),
    ("I entered the county show with my dahlias", "leisure"),
    ("I took up knitting last winter", "leisure"),
    ("the culture at my workplace is very informal", "workplace"),
    ("unwritten rules about when you can leave the office", "workplace"),
    ("my office is open-plan and noisy", "workplace"),
    ("my manager micromanages everyone", "workplace"),
    ("where do I keep my passport", "record"),
    ("my birth certificate is in the safe", "record"),
    ("I need to renew my residence permit", "record"),
    ("the tenancy agreement I signed last year", "record"),
    ("I filed the paperwork for the council last week", "record"),
    ("the title deeds to the house", "record"),
]


def load_gloss(path=GLOSS):
    out = {}
    for n, raw in enumerate(io.open(path, encoding="utf-8"), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        assert ": " in line, "line %d is not `pair: gloss`" % n
        pair, text = line.split(": ", 1)
        assert pair not in out, "line %d repeats %s" % (n, pair)
        out[pair] = text.strip()
    return out


def check():
    cats = parse(VOCAB)
    pairs = ["%s/%s" % (c, t) for c in cats for t in cats[c]]
    gloss = load_gloss()
    missing = [p for p in pairs if p not in gloss]
    extra = [p for p in gloss if p not in set(pairs)]
    assert not missing, "%d pairs have no gloss: %s" % (len(missing), missing[:8])
    assert not extra, "%d glosses name no pair: %s" % (len(extra), extra[:8])
    print("%d pairs, %d glosses, all matched" % (len(pairs), len(gloss)))
    return pairs, gloss


def measure(pairs, gloss):
    import numpy as np
    sys.path[:0] = [ROOT + "tests/corpora"]
    from embed import Embedder
    e = Embedder()
    Q = e.encode([q for q, _ in PROBES], kind="query")
    for name, text in (("bare paths", [p.replace("/", " / ") for p in pairs]),
                       ("glossed", ["%s: %s" % (p, gloss[p]) for p in pairs])):
        P = e.encode(text, kind="passage")
        hit = 0
        rows = []
        for (q, want), row in zip(PROBES, Q @ P.T):
            top = pairs[int(np.argmax(row))]
            ok = top.split("/")[0] == want
            hit += ok
            rows.append("  %s %-46s %-28s %.3f" % (" " if ok else "x", q[:46], top, row.max()))
        print("\n== %s: %d/%d" % (name, hit, len(PROBES)))
        print("\n".join(rows))


def main():
    pairs, gloss = check()
    if "--check" not in sys.argv:
        measure(pairs, gloss)


if __name__ == "__main__":
    main()
