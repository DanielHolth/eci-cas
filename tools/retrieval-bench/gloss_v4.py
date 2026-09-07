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

A paired bootstrap over the 87 questions, which is what separated the gloss
sizes in `file_v4` and is owed here too:

    shipped@3 vs terse@1   63 vs 80 rows    -18.4pp   CI [-32.2, -4.6]   P 0%
    shipped@8 vs terse@3  176 vs 214 rows    -4.6pp   CI [-13.8, +4.6]   P 13%

So the cheap end is a real loss and not a table artifact -- the interval
excludes zero and terse never wins a resample. The expensive end is a tie, and
a tie bought with 38 extra rows per question, which is the same shape as
`file_v4`'s by-gloss result: read it as "no better", not as "close".
Terse's problem is granularity, not glosses: its smallest openable unit is 80
rows, so it cannot express a cheap read at all, and three of its rows in four
are unreachable at any budget the shipped shelf would use. That is the
facts-per-file mechanism from batch 12 arriving again by a different road --
and it now holds for a vector reader, which was the one thing the earlier
batches could not say, since they only ever had a weak model doing the picking.

Coverage looks like a flaw and is not. `TERSE_GLOSS` covers 26 of 34 pairs and
the shipped gloss 160 of 170, but every missing pair on both shelves is
`x/other` -- one per category, no exceptions. `other` is the valve: it is
defined by matching nothing, so there is no direction in the space for it and
a gloss would invent a meaning it does not have. A vector filer never files
into `other`, which is right, and consistent with `other` being write-side
only and opened in code anyway.

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
    perq = {"gloss": [], "centroid": []}
    for q, v in zip(qs, qv):
        for how, M in (("gloss", G), ("centroid", cent)):
            chosen = np.argsort(-(M @ v))[:files]
            pool = np.flatnonzero(np.isin(owner, chosen))
            out[how + ".reach"] += len(pool)
            out[how + ".select"] += bool(per_q[q] & set(int(c) for c in chosen))
            top = pool[np.argsort(-(rowsv[pool] @ v))[:k]] if len(pool) else []
            s_ = answered_row([rows_flat[i] for i in top], q)
            out[how + ".strict"] += s_
            perq[how].append(s_)
    out["pairs_used"] = len(set(owner.tolist()))
    return out, {how: np.array(perq[how], float) for how in perq}


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
    swept, PERQ = [], {}
    for files in (1, 2, 3, 5, 8):
        for name, shelf in shelves:
            t, pq = evaluate(e, rowsv, flat, None, shelf, qv, qs, k, files)
            for how in ("gloss", "centroid"):
                PERQ[(name, files, how)] = pq[how]
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
    print("    %8s   %-22s %-22s" % ("~rows", "shipped/170", "terse/34"))
    for target in (20, 60, 100, 220):
        cells = []
        for nm, _ in shelves:
            cand = [r for r in swept if r[2] == "centroid" and r[0] == nm]
            b = min(cand, key=lambda r: abs(r[3] - target))
            cells.append("%3d%% (%d files, %3.0f rows)" % (b[4], b[1], b[3]))
        print("    %8d   %-22s %-22s" % (target, cells[0], cells[1]))

    # 87 questions makes a 5pp gap four questions wide, so the file-count
    # table cannot be read as a win for either shelf on its own. Paired over
    # questions -- both shelves answer the same ones -- at the two points
    # where their reach is closest. Positive means terse is ahead.
    rng = np.random.default_rng(4)
    print("")
    print("  paired bootstrap, terse minus shipped, 5000 resamples of %d questions:" % n)
    for lo, hi, why in ((3, 1, "63 vs 80 rows"), (8, 3, "176 vs 214 rows")):
        d = PERQ[("terse/34", hi, "centroid")] - PERQ[("shipped/170", lo, "centroid")]
        boot = d[rng.integers(0, len(d), (5000, len(d)))].mean(1)
        print("    shipped@%d vs terse@%d (%s)  %+5.1fpp  95%% CI [%+5.1f, %+5.1f]"
              "  P(terse better) %3d%%" % (
                  lo, hi, why, 100 * d.mean(), 100 * np.percentile(boot, 2.5),
                  100 * np.percentile(boot, 97.5), 100 * (boot > 0).mean()))

    print("\n  select = an opened file holds the answer. Not comparable across")
    print("           shelves: 3 of 34 is 9% of the shelf, 3 of 171 is 1.8%.")
    print("  strict = one of k=%d rows carries the whole key. Comparable." % k)
    print("  reach  = mean rows inside the opened files, before ranking.")
    print("  used   = distinct pairs any row landed in.")
    print("  pick by gloss = rank files by the written line; centroid = by the")
    print("           mean of what actually landed there.")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
