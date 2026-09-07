"""One written gloss per pair, on both shelves. Daniel's question, batch 16.

    python gloss_v4.py [k]

`file_v4` filed rows by a gloss derived from ten rows already in the file, and
found that a one-row gloss is 12pp worse. That left the interesting version
untested: a gloss that is *written*, one line per pair, rather than sampled.
Both shelves already ship one and neither has ever been embedded --
`bench.CAT["gloss"]` gives the 170-pair shelf a line per pair ("identity/fear:
afraid, scared, phobia, terrified") and `terse_vocab.TERSE_GLOSS` gives the
34-pair shelf the same. They were written to be read by a model inside a
prompt. This asks what they are worth as vectors.

And it asks it on both shelves, because Daniel's question pairs the two: 34
pairs with one gloss each. Consolidation lost on every previous measurement
(batches 8-12) and the mechanism was facts-per-file -- fewer folders means
fatter folders. A gloss changes what that costs: the reason terse lost was
that a weak model could not tell its broad drawers apart, and a vector does
not have that problem.

No model calls. Every row in the v4 archive -- gold and padding alike -- is
re-filed into each shelf by nearest gloss, so the content is identical across
arms and the only variable is the shelf and its glosses. Re-filing the padding
too is the point: a shelf has to hold the distractors as well as the answers,
and leaving 1478 padding rows on their original 170-pair addresses would hand
the terse arm an archive nobody could have built.

Two results, and they point opposite ways until reach is held fixed.

**A written gloss is as good as a derived one, and needs no archive.** On the
170-pair shelf, picking files by the written line scores 70% strict at three
files against 70% for a centroid of what actually landed there. `file_v4` had
left this open: a gloss derived from ten rows works, one derived from a single
row does not, and a young archive has no ten rows to derive from. A written
line closes that gap -- it is available on day one, it costs nothing per row,
and it is already in the repo for both shelves. Cold start is solved.

**Terse still loses, and the file-count table hides it.** At three files terse
reads 75% against the shipped shelf's 70%, which looks like a reversal of
batches 8-12. It is not. Three files of 34 is a tenth of the archive and three
of 171 is a fiftieth, so terse is being handed 214 rows to rank where the
shipped shelf gets 63. Matched on rows reached instead:

    ~rows reached     shipped/170        terse/34
        20            58%  (1 file)      --
        60            70%  (3 files)     51%  (1 file, 80 rows)
       100            78%  (5 files)     51%  (1 file, 80 rows)
       220            80%  (8 files)     75%  (3 files, 214 rows)

The shipped shelf wins at every budget, by 19pp in the middle of the range.
Terse's problem is granularity, not glosses: its smallest openable unit is 80
rows, so it cannot express a cheap read at all, and three of its rows in four
are unreachable at any budget the shipped shelf would use. That is the
facts-per-file mechanism from batch 12 arriving again by a different road --
and it now holds for a vector reader, which was the one thing the earlier
batches could not say, since they only ever had a weak model doing the picking.

Coverage, which flatters neither arm and should be fixed before either number
is quoted again: `TERSE_GLOSS` covers 26 of 34 pairs and the shipped gloss 160
of 170, so both shelves have unglossed pairs that no row can ever be filed
into. terse ran on 26 files and the shipped shelf used 148.

Reading `select` across shelves needs care and the table prints what it needs.
Three files of 34 is 9% of the shelf; three of 171 is 1.8%. Opening the same
*number* of files is therefore not opening the same share of the archive, so
`reach` -- the mean rows inside the opened files -- is printed beside it, and
`strict` is the number to compare: k=5 rows reach Intent either way.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import bench
import read_bench as rb
import retrieval_v4 as corpus
import terse_vocab
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, shape
from cosine_v4 import answered_row
from file_v4 import fact_text


def shipped_gloss():
    """`pair: words, words` lines from the shipped category prompt."""
    out = {}
    for ln in bench.CAT["gloss"].splitlines():
        if ":" in ln and "/" in ln.split(":")[0]:
            pair, words = ln.split(":", 1)
            out[pair.strip()] = words.strip()
    return out


def terse_gloss():
    return {c + "/" + t: w for c, ts in terse_vocab.TERSE_GLOSS.items()
            for t, w in ts.items()}


def gloss_text(pair, words):
    """The gloss as a passage. The pair name is included because it is real
    information the shelf carries and a reader would see it -- and because
    dropping it would make this a test of the word list alone, which is not
    what either shelf ships."""
    return pair.replace("/", " / ") + ": " + words


def evaluate(e, rowsv, rows_flat, gold_stmt, shelf, qv, qs, k, files):
    """File every row into `shelf` by nearest gloss, then read it back."""
    pairs = sorted(shelf)
    G = e.encode([gloss_text(p, shelf[p]) for p in pairs], kind="passage")
    owner = np.argmax(rowsv @ G.T, axis=1)

    cent = np.vstack([rowsv[owner == i].mean(0) if (owner == i).any()
                      else np.zeros(rowsv.shape[1]) for i in range(len(pairs))])
    cent /= np.maximum(np.linalg.norm(cent, axis=1, keepdims=True), 1e-9)

    gold = collections.defaultdict(set)
    for r, i in zip(rows_flat, owner):
        if r.get("stmt"):
            gold[r["stmt"]].add(int(i))
    per_q = {q: gold.get(stmt, set())
             for stmt, questions in corpus.STATEMENTS for q in questions}

    out = collections.Counter()
    for q, v in zip(qs, qv):
        for how, M in (("gloss", G), ("centroid", cent)):
            chosen = np.argsort(-(M @ v))[:files]
            pool = np.flatnonzero(np.isin(owner, chosen))
            out[how + ".reach"] += len(pool)
            out[how + ".select"] += bool(per_q[q] & set(int(c) for c in chosen))
            top = pool[np.argsort(-(rowsv[pool] @ v))[:k]] if len(pool) else []
            out[how + ".strict"] += answered_row([rows_flat[i] for i in top], q)
    out["pairs_used"] = len(set(owner.tolist()))
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
    n = len(qs)

    shelves = [("shipped/170", shipped_gloss()), ("terse/34", terse_gloss())]
    for name, shelf in shelves:
        missing = [p for p in rb.all_pairs() if p not in shelf] if "170" in name else []
        print("%-12s %d pairs glossed%s" % (
            name, len(shelf),
            (", %d shipped pairs have none" % len(missing)) if missing else ""))

    print("\n  %-12s %4s %-9s %7s %7s %7s %6s"
          % ("shelf", "open", "pick by", "select", "strict", "reach", "used"))
    swept = []
    for files in (1, 2, 3, 5, 8):
        for name, shelf in shelves:
            t = evaluate(e, rowsv, flat, None, shelf, qv, qs, k, files)
            for how in ("gloss", "centroid"):
                reach = t[how + ".reach"] / float(n)
                strict = 100 * t[how + ".strict"] // n
                swept.append((name, files, how, reach, strict))
                print("  %-12s %4d %-9s %6d%% %6d%% %7.0f %6d" % (
                    name, files, how,
                    100 * t[how + ".select"] // n, strict, reach,
                    t["pairs_used"]))

    # Matched on reach rather than on file count, which is the only honest way
    # to set a 34-pair shelf beside a 170-pair one. Three files of 34 is a
    # tenth of the archive; three of 171 is a fiftieth. A shelf that wins by
    # being handed four times as many rows to rank has not shown that its
    # folders are better, it has shown that a bigger slice is easier.
    print("\n  matched on rows reached, centroid pick, nearest sweep point:")
    print("    %8s   %-16s %-16s" % ("~rows", "shipped/170", "terse/34"))
    for target in (20, 60, 100, 220):
        cells = []
        for nm, _ in shelves:
            cand = [r for r in swept if r[2] == "centroid" and r[0] == nm]
            b = min(cand, key=lambda r: abs(r[3] - target))
            cells.append("%3d%%  (%d files, %.0f)" % (b[4], b[1], b[3]))
        print("    %8d   %-16s %-16s" % (target, cells[0], cells[1]))

    print("\n  select = an opened file holds the answer. Not comparable across")
    print("           shelves: 3 of 34 is 9% of the shelf, 3 of 171 is 1.8%.")
    print("  strict = one of k=%d rows carries the whole key. Comparable." % k)
    print("  reach  = mean rows inside the opened files, before ranking.")
    print("  used   = distinct pairs any row landed in.")
    print("  pick by gloss = rank files by the written line; centroid = by the")
    print("           mean of what actually landed there.")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
