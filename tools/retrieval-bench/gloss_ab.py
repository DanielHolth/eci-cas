"""Does a few words of synonym beside each topic name help the selector?

The arm. Baseline over 3 reps: category 79%, select 36%, pick 76%,
answer 25%. The category is nearly right and the folder inside it is a
coin-flip between two or three near-synonyms, so this gives the selector
the words a question would actually use for each folder -- see
topic_gloss.py for why they are in the question's vocabulary and not the
folder's.

Method, as tools/retrieval-bench/README.md requires: the two arms are
interleaved inside each rep against the same frozen archive, so a slow drift
in the server or a rebuild cannot land on one arm; and the archive is not
rebuilt at all, because the gloss is shown only to the selector. Filing is
byte-identical between arms. That is the whole reason this arm goes first --
a moved number here has one possible parent.

Judged on sign consistency across reps against a 10pp floor, not on the mean.
"""
import sys, collections

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import retrieval_v3 as corpus
from answers_v3 import ANSWERS
from topic_gloss import GLOSS

ARMS = [("plain", None), ("gloss", GLOSS)]


def run(reps=3, verbose=True):
    a, rows = rb.archive()
    index = sorted(rows)
    gold = rb.gold_index(a)
    per = {name: collections.Counter() for name, _ in ARMS}
    prev = {name: collections.Counter() for name, _ in ARMS}

    for rep in range(reps):
        for q in sorted(ANSWERS):
            for name, g in ARMS:
                opened = rb.select(q, index, g)
                hit = bool(gold[q] & set(opened))
                got = rb.pick(q, [r for p in opened for r in rows[p]])
                t = per[name]
                t["n"] += 1
                t["select"] += hit
                t["cat"] += bool(set(p.split("/")[0] for p in gold[q])
                                 & set(p.split("/")[0] for p in opened))
                t["answer"] += rb.answered(got, q)
        # Per-rep deltas, not running totals: sign consistency is the test,
        # and a cumulative line cannot show a rep going the other way.
        line = "rep%d  " % (rep + 1)
        for name, _ in ARMS:
            t, p = per[name], prev[name]
            line += "%-6s sel %2d cat %2d ans %2d   " % (
                name, t["select"] - p["select"], t["cat"] - p["cat"], t["answer"] - p["answer"])
            prev[name] = t.copy()
        print(line + "(of %d)" % len(ANSWERS), flush=True)

    print()
    for name, _ in ARMS:
        t = per[name]
        n = t["n"]
        print("%-6s  category %d%%   select %d%%   answer %d%%" % (
            name, 100 * t["cat"] // n, 100 * t["select"] // n, 100 * t["answer"] // n))


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 3)
