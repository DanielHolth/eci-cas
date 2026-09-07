"""Does vector filing degrade faster than the Cataloger as the archive fills?

    python scale_v4.py [k] [files]

`file_v4` finds that filing by gloss ties two LLM calls per row while putting
nine rows in ten somewhere else -- but it concentrates the gold into 31 pairs
where the Cataloger uses 54. Fewer, fatter files is the exact mechanism that
made every consolidated shelf lose in batches 8-12, so the tie could be an
artifact of 1559 rows being small enough that fatness does not yet bite.

There is no bigger corpus tonight, so this varies density instead: keep every
gold row, keep the same filing, and thin the padding to a fraction. Density is
not size -- an archive ten times larger has more *distinct* facts, not the
same facts more often -- so this cannot prove behaviour at 100k. What it can
do is show the direction of the two curves. If by-gloss loses ground to the
Cataloger as padding grows from a quarter to all of it, the concentration
worry is real and the tie is a small-archive result. If the two curves fall in
parallel, fatness is hurting both readers equally and the tie is about filing
rather than about size.

Result (3 files, k=5, strict):

    padding kept      25%      50%     100%
    llm               85%      78%      72%
    by-gloss          82%      75%      73%

The curves fall in parallel and cross at the end. Fatness is costing both
readers about 13pp over a 4x dilution and costing them the same, so the tie in
file_v4 is not an artifact of a thin archive -- concentration into 31 pairs
does not show up as divergence at any density available here. The worry is
downgraded, not closed: this dilutes 78 facts rather than adding 78,000, and
the failure mode at real scale is distinct near-neighbours, which no amount of
duplicated padding will produce.

Subsampling is seeded and identical across arms: the two filings see the same
distractors, so nothing here is a draw between different archives.
"""
import sys, collections, random
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import retrieval_v4 as corpus
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, gold_index, shape
from cosine_v4 import answered_row
from file_v4 import fact_text

FRACTIONS = (0.25, 0.5, 1.0)


def main(k=5, files=3):
    a, rows = rb.archive(CACHE)
    index = sorted(rows)
    print("archive: " + shape(rows))
    e = Embedder()

    pad = {p: [r for r in rows[p] if not r.get("stmt")] for p in index}
    gold_rows = [r for p in index for r in rows[p] if r.get("stmt")]
    padv = e.encode([rb.embed_text(r) for p in index for r in pad[p]], kind="passage")
    off, gloss = 0, []
    for p in index:
        n = len(pad[p])
        gloss.append(padv[off:off + n].mean(0) if n else np.zeros(padv.shape[1]))
        off += n
    gloss = np.vstack(gloss)
    gloss /= np.maximum(np.linalg.norm(gloss, axis=1, keepdims=True), 1e-9)

    gv = e.encode([fact_text(r) for r in gold_rows], kind="query")
    filings = {"llm": [index.index(r["pair"]) for r in gold_rows],
               "by-gloss": list(np.argmax(gv @ gloss.T, axis=1))}

    qs = sorted(ANSWERS)
    qv = e.encode(qs, kind="query")
    # Every row's vector once, keyed by identity, so thinning is a mask over a
    # matrix rather than a re-encode. Rows are dicts and unhashable, so index
    # by position in a stable listing.
    every = [r for p in index for r in pad[p]] + gold_rows
    allv = np.vstack([padv, e.encode([rb.embed_text(r) for r in gold_rows],
                                     kind="passage")])
    pos = {id(r): i for i, r in enumerate(every)}

    print("\n  %-9s %8s %8s %8s %8s" % ("filing", "padding", "rows", "select", "strict"))
    for frac in FRACTIONS:
        keep = set()
        drop = random.Random(4)
        for p in index:
            n = len(pad[p])
            keep |= set(id(r) for r in drop.sample(pad[p], int(round(n * frac))))
        for name, where in filings.items():
            moved = collections.defaultdict(list)
            for p in index:
                moved[p] += [r for r in pad[p] if id(r) in keep]
            for r, i in zip(gold_rows, where):
                moved[index[i]].append(r)
            flat = [r for p in index for r in moved[p]]
            owner = np.array([index.index(p) for p in index for _ in moved[p]])
            M = allv[[pos[id(r)] for r in flat]]
            cent = np.vstack([M[owner == i].mean(0) if (owner == i).any()
                              else np.zeros(M.shape[1]) for i in range(len(index))])
            cent /= np.maximum(np.linalg.norm(cent, axis=1, keepdims=True), 1e-9)

            by_stmt = collections.defaultdict(set)
            for r, i in zip(gold_rows, where):
                by_stmt[r["stmt"]].add(index[i])
            gold = {q: by_stmt.get(stmt, set())
                    for stmt, questions in corpus.STATEMENTS for q in questions}

            sel = strict = 0
            for q, v in zip(qs, qv):
                chosen = np.argsort(-(cent @ v))[:files]
                sel += bool(gold[q] & set(index[i] for i in chosen))
                pool = np.flatnonzero(np.isin(owner, chosen))
                top = pool[np.argsort(-(M[pool] @ v))[:k]] if len(pool) else []
                strict += answered_row([flat[i] for i in top], q)
            n = len(qs)
            print("  %-9s %7d%% %8d %7d%% %7d%%" % (
                name, 100 * frac, len(flat), 100 * sel // n, 100 * strict // n))

    print("\n  Density, not size: the same facts diluted, not more facts. Read")
    print("  the direction of the two curves, not the levels.")


if __name__ == "__main__":
    args = [int(x) for x in sys.argv[1:3] if x.isdigit()]
    main(*(args or [5]))
