"""The 512-pair shelf against the shipped 170, matched on rows reached.

    python shelf512_v5.py [k]

Every number recorded about v512 so far is write-side: flat_v5 occupancy,
bleed_v5 entropy, and the twenty routing probes of batch 19. None of them
says whether a question gets answered. The one shelf comparison that ever
ran end to end was batch 12, and 512 was not in it.

So this is batch 16's construction with a third shelf. gloss_v4.evaluate
re-files all 1559 v4 rows -- gold and padding -- into a shelf by nearest
gloss, then reads back, and the only variable is the shelf and its glosses.
No model calls.

Matched on ROWS REACHED, not on file count, for the reason batch 16 had to
learn: three files of 34 is a tenth of the archive and three of 171 is a
fiftieth. Here the asymmetry runs the other way -- three files of 480 is a
six-hundredth -- so reading the file-count table would now flatter the
shipped shelf exactly as it once flattered terse.

`x/other` is dropped from the 512 shelf, which is not a handicap but the
same rule the shipped arm already runs under: every pair missing a gloss on
either shipped shelf is an `other`, the valve is defined by matching
nothing, and a vector filer correctly never files into it. 480 pairs.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, shape
import gloss_v4
from gloss_v4 import shipped_gloss, evaluate
from build_gloss import load_gloss


def v512_gloss():
    return {p: g for p, g in load_gloss().items() if not p.endswith("/other")}


def main(k=5):
    a, rows = rb.archive(CACHE)
    print("archive: " + shape(rows))
    flat = [r for p in sorted(rows) for r in rows[p]]
    e = Embedder()
    print("embedding %d rows ..." % len(flat), flush=True)
    rowsv = e.encode([rb.embed_text(r) for r in flat], kind="passage")
    qs = sorted(ANSWERS)
    qv = e.encode(qs, kind="query")
    n = len(qs)

    # Third arm: the same 512 glosses with the `cat / topic:` prefix that
    # gloss_v4.gloss_text puts in front of every line removed. Both shipped
    # glosses are comma lists, where the prefix is most of the grammar; the
    # 512 lines are sentences, where it is a label bolted to the front of
    # one. Isolating it costs nothing and it turns out to matter.
    gloss_v4.gloss_text = lambda pair, words: words
    shelves = [("shipped/170", shipped_gloss()), ("v512", v512_gloss()),
               ("v512 nopfx", v512_gloss())]
    gloss_v4.gloss_text = lambda pair, words: pair.replace("/", " / ") + ": " + words
    for name, shelf in shelves:
        print("%-12s %d pairs glossed" % (name, len(shelf)))

    print("\n  %-12s %4s %-9s %7s %7s %7s %6s"
          % ("shelf", "open", "pick by", "select", "strict", "reach", "used"))
    swept, PERQ = [], {}
    for files in (1, 2, 3, 5, 8, 12, 20, 32):
        for name, shelf in shelves:
            gloss_v4.gloss_text = ((lambda pair, words: words) if "nopfx" in name
                                   else (lambda pair, words:
                                         pair.replace("/", " / ") + ": " + words))
            t, pq = evaluate(e, rowsv, flat, None, shelf, qv, qs, k, files)
            for how in ("gloss", "centroid"):
                PERQ[(name, files, how)] = pq[how]
                reach = t[how + ".reach"] / float(n)
                strict = 100 * t[how + ".strict"] // n
                swept.append((name, files, how, reach, strict))
                print("  %-12s %4d %-9s %6d%% %6d%% %7.0f %6d" % (
                    name, files, how, 100 * t[how + ".select"] // n, strict,
                    reach, t["pairs_used"]))

    for how in ("gloss", "centroid"):
        print("\n  matched on rows reached, %s pick:" % how)
        print("    %8s   " % "~rows" + " ".join("%-24s" % nm for nm, _ in shelves))
        for target in (20, 60, 100, 220):
            cells = []
            for nm, _ in shelves:
                cand = [r for r in swept if r[2] == how and r[0] == nm]
                b = min(cand, key=lambda r: abs(r[3] - target))
                cells.append("%3d%% (%2d files, %3.0f rows)" % (b[4], b[1], b[3]))
            print("    %8d   " % target + " ".join("%-24s" % c for c in cells))

    # Paired over the same questions at the points where reach is closest,
    # picked from the sweep rather than hardcoded -- the file counts that
    # match on reach are not known until the sweep has run.
    rng = np.random.default_rng(4)
    print("\n  paired bootstrap, v512 minus shipped, 5000 resamples of %d questions:" % n)
    for how in ("gloss", "centroid"):
        for target in (60, 100, 220):
            pick = {}
            for nm, _ in shelves:
                cand = [r for r in swept if r[2] == how and r[0] == nm]
                pick[nm] = min(cand, key=lambda r: abs(r[3] - target))
            lo, hi = pick["shipped/170"], pick["v512 nopfx"]
            d = PERQ[("v512 nopfx", hi[1], how)] - PERQ[("shipped/170", lo[1], how)]
            boot = d[rng.integers(0, len(d), (5000, len(d)))].mean(1)
            print("    %-8s ~%3d rows  shipped@%-2d vs v512nopfx@%-2d (%3.0f vs %3.0f rows)"
                  "  %+5.1fpp  95%% CI [%+5.1f, %+5.1f]  P(v512 better) %3d%%" % (
                      how, target, lo[1], hi[1], lo[3], hi[3], 100 * d.mean(),
                      100 * np.percentile(boot, 2.5), 100 * np.percentile(boot, 97.5),
                      100 * (boot > 0).mean()))

    print("\n  strict = one of k=%d rows carries the whole key. Comparable." % k)
    print("  reach  = mean rows inside the opened files, before ranking.")
    print("  select is NOT comparable across shelves: 3 of 480 is 0.6%,")
    print("         3 of 171 is 1.8%.")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
