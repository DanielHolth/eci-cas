"""Are the 512 glosses in the wrong register? Batch 20 said to find out.

    python register_v5.py

Batch 20 left one explanation for why v512's written glosses open its
drawers worse than the shipped shelf's do: every one of them is first
person ("I bought a lathe for the workshop") while every row in the archive
embeds as a third-person declarative ("The employee was hired in 2021").
gloss_v4's docstring makes the same argument about descriptive glosses --
a gloss in a register no row and no question is ever in sits in its own
corner of the space however well it is written.

Rewriting 480 lines by hand is a large write, so this is the cheap version
first: a mechanical pronoun swap, plus an arm that also drops the
`cat / topic:` prefix for `cat topic. ` so the gloss has the exact SHAPE of
read_bench.embed_text -- address words, full stop, declarative sentence.

The crude conversion is the honest caveat and it cuts one way: it emits
"the person" 480 times where a real row says "the employee" or "the
student", so it adds a constant token a hand rewrite would not have. Read
a null here as "the predicted large effect is not there", not as "register
cannot matter".
"""
import sys, re
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import gloss_v4
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE
from gloss_v4 import evaluate
from shelf512_v5 import v512_gloss

SUB = [(r"\bI am\b", "the person is"), (r"\bI have\b", "the person has"),
       (r"\bI was\b", "the person was"), (r"\bI do\b", "the person does"),
       (r"\bI\b", "the person"), (r"\bmy\b", "the person's"),
       (r"\bmine\b", "theirs"), (r"\bme\b", "them"),
       (r"\bmyself\b", "themselves"), (r"\bwe\b", "they"),
       (r"\bour\b", "their"), (r"\bus\b", "them")]


def third(s):
    for a, b in SUB:
        s = re.sub(a, b, s)
    return s[0].upper() + s[1:] + "."


def main(k=5):
    g = v512_gloss()
    g3 = {p: third(v) for p, v in g.items()}
    a, rows = rb.archive(CACHE)
    flat = [r for p in sorted(rows) for r in rows[p]]
    e = Embedder()
    print("embedding %d rows ..." % len(flat), flush=True)
    rowsv = e.encode([rb.embed_text(r) for r in flat], kind="passage")
    qs = sorted(ANSWERS)
    qv = e.encode(qs, kind="query")
    n = len(qs)

    arms = [("1st, prefixed", g, lambda p, w: p.replace("/", " / ") + ": " + w),
            ("3rd, prefixed", g3, lambda p, w: p.replace("/", " / ") + ": " + w),
            ("3rd, row-shaped", g3, lambda p, w: p.replace("/", " ") + ". " + w)]
    print("  %-16s %4s %7s %7s %7s" % ("arm", "open", "gloss", "centr", "reach"))
    for name, shelf, fn in arms:
        for files in (3, 5, 8, 20):
            gloss_v4.gloss_text = fn
            t, _ = evaluate(e, rowsv, flat, None, shelf, qv, qs, k, files)
            print("  %-16s %4d %6d%% %6d%% %7.0f" % (
                name, files, 100 * t["gloss.strict"] // n,
                100 * t["centroid.strict"] // n, t["gloss.reach"] / float(n)))


if __name__ == "__main__":
    main()
