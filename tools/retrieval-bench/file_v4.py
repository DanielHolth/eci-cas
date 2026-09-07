"""File the row with vectors instead of two LLM calls. Daniel's idea 2, write half.

    python file_v4.py [k] [files]

`bench.file_fact` spends two calls per row: pick the category from a prompt
listing them, then pick the topic from that category's topics. That is the one
place the closed vocabulary clearly pays for itself -- the read-side Librarian
call does not (arms_v4, cosine_v4). Idea 2 asks whether even this one can be
arithmetic: match the fact against examples of what each file holds.

Three filings of the same extracted rows. Extraction is not re-run and the
padding is left exactly where it is, so the distractor design and every row's
content are identical across arms and the only variable is which file a gold
row went into.

    llm         where the Cataloger actually put it. The shipped path.
    by-name     nearest pair name, embedded. No knowledge of contents.
    by-gloss    nearest centroid of that file's padding -- the padding is
                LLM-generated examples of what belongs under the pair, which
                is what Daniel means by a gloss, and it is independent of the
                gold rows so this is not scoring itself.

Scored by whether the archive that results is readable: `select` is whether a
picker opens a file holding the answer, `strict` whether one of k rows carries
the whole key.

Result:

    filing       agree   select   strict   spread
    llm           100%      74%      72%       54
    by-name        32%      66%      65%       55
    by-gloss       11%      77%      73%       31

Ten samples is the number, and the whole file buys nothing over ten:

    gloss size      1     3    10   whole file
    select         58%   64%  77%      77%
    strict         59%   60%  73%      73%

The median pair holds 3 rows, so for most files ten samples *is* the whole
file and the two right-hand columns are the same archive. The gain from 3 to
10 therefore comes entirely from the fat pairs -- which is where a gloss has
any work to do, since a three-row file is nearly its own summary. That also
sets the ceiling on what a hand-written gloss could add: against a fat file it
is competing with ten real examples, not with a name.

It holds at every width, and the gap widens as more files are opened
(select / strict, k=5):

    files opened      1          3          5
    llm            49 / 49    74 / 72    82 / 79
    by-name        54 / 51    66 / 65    80 / 78
    by-gloss       55 / 52    77 / 73    89 / 81

So the concentration into 31 pairs is not costing anything here -- a filer
that collapsed usefully-distinct facts together would lose at files=1 first,
and by-gloss is ahead there too.

**by-gloss ties two LLM calls per row while agreeing with them 11% of the
time.** A paired bootstrap over the 87 questions says +1.1pp strict, 95% CI
[-9.2, +12.6], P(better) 53% -- a coin flip, and the right word is "ties", not
"beats". 81 gold rows cannot resolve 3pp and it would be dishonest to read the
table as if they could.

The tie is the finding. The Cataloger's specific choices are very nearly
irrelevant to whether the fact is found again: nine times in ten the vector
puts the row somewhere else and the archive reads the same. What retrieval
needs is that filing and retrieval agree with each other, not that either
agrees with a human's sense of where a thing goes.

The same bootstrap does separate the gloss sizes, which is what makes it worth
running: gloss-1 is -12.6pp with P(better) 1% and gloss-3 -11.5pp at 3%. Ten
samples versus one or three is a real difference; ten samples versus two LLM
calls is not.

The gloss is not free, but it is amortised the right way round. It is the mean
of a file's padding -- LLM-written examples of what belongs under that pair --
so it costs a handful of calls per *file*, once, against two calls per *row*
forever. For 171 files and an archive that grows without bound, that is the
whole argument.

Two costs, and the second is the one to watch. `by-name` loses 7pp, so the
gloss is doing the work and a bare file name is not enough -- consistent with
name-only being the weakest read arm in shelf_v4. And by-gloss uses 31 pairs
where the Cataloger uses 54: it concentrates. Fewer, fatter files is the exact
mechanism that made consolidated shelves lose in batches 8-12, so this number
is a live risk at scale even though it does not bite at 1559 rows. It is also
a legibility cost -- 11% agreement means an archive filed somewhere a person
would not look, which is a real loss for a store meant to be human-readable
even if retrieval does not care.

The circularity worth naming, because it is not the one it looks like. Filing
by vector and then retrieving by vector share a metric, which sounds like an
arm agreeing with itself. It is not: filing scores the *fact* against a file,
retrieval scores the *question* against a row, and those are different vectors
-- a fact and the question it answers are near each other only if the embedder
is doing its job. What the shared metric does buy is consistency, and that is
the actual argument for the idea rather than a flaw in the test.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, answered, gold_index, shape
from cosine_v4 import answered_row


def fact_text(r):
    """What the Cataloger is shown, near enough: the fact, not its address.

    Deliberately excludes category/topic -- those are the answer being
    predicted, and line() contains them."""
    return " ".join(x for x in (r.get("subtopic"), r.get("subject"), r.get("key"),
                                r.get("value"), r.get("sentence")) if x)


def main(k=5, files=3):
    a, rows = rb.archive(CACHE)
    index = sorted(rows)
    print("archive: " + shape(rows))
    e = Embedder()

    pad = {p: [r for r in rows[p] if not r.get("stmt")] for p in index}
    gold_rows = [r for p in index for r in rows[p] if r.get("stmt")]
    print("%d gold rows to re-file across %d pairs" % (len(gold_rows), len(index)),
          flush=True)

    padv = e.encode([rb.embed_text(r) for p in index for r in pad[p]], kind="passage")
    off, gloss = 0, []
    for p in index:
        n = len(pad[p])
        # A pair with no padding has no gloss; it gets a zero vector and can
        # never be chosen by by-gloss. That is the honest behaviour -- an empty
        # file is a file nothing is known about -- and it is rare here.
        gloss.append(padv[off:off + n].mean(0) if n else np.zeros(padv.shape[1]))
        off += n
    gloss = np.vstack(gloss)
    gloss /= np.maximum(np.linalg.norm(gloss, axis=1, keepdims=True), 1e-9)

    # How many samples a gloss needs. Daniel's idea says ten; the arm above
    # uses the whole file, which for a fat pair is sixty and is not a gloss any
    # more. Seeded random draw, not the first n -- build() appends gold before
    # padding, and taking the head is how the shelf_v4 sample arms accidentally
    # read the answer key.
    import random as _r
    draw = _r.Random(4)
    small = {}
    for m in (1, 3, 10):
        vecs, off2 = [], 0
        for p in index:
            n = len(pad[p])
            block = padv[off2:off2 + n]
            off2 += n
            if not n:
                vecs.append(np.zeros(padv.shape[1]))
                continue
            take = draw.sample(range(n), min(m, n))
            vecs.append(block[take].mean(0))
        v = np.vstack(vecs)
        small[m] = v / np.maximum(np.linalg.norm(v, axis=1, keepdims=True), 1e-9)
    names = e.encode([p.replace("/", " / ") for p in index], kind="passage")

    gv = e.encode([fact_text(r) for r in gold_rows], kind="query")
    filings = {"llm": [index.index(r["pair"]) for r in gold_rows],
               "by-name": list(np.argmax(gv @ names.T, axis=1)),
               "by-gloss": list(np.argmax(gv @ gloss.T, axis=1))}
    for m in (1, 3, 10):
        filings["gloss-%d" % m] = list(np.argmax(gv @ small[m].T, axis=1))

    qs = sorted(ANSWERS)
    qv = e.encode(qs, kind="query")
    PERQ = {}
    print("\n  %-9s %8s %8s %8s %8s" % ("filing", "agree", "select", "strict", "spread"))
    for name, where in filings.items():
        # Rebuild the archive with gold moved and padding untouched.
        moved = collections.defaultdict(list)
        for p in index:
            moved[p] += pad[p]
        for r, i in zip(gold_rows, where):
            moved[index[i]].append(r)
        flat = [r for p in index for r in moved[p]]
        owner = np.array([index.index(p) for p in index for _ in moved[p]])
        M = e.encode([rb.embed_text(r) for r in flat], kind="passage")
        cent = np.vstack([M[owner == i].mean(0) if (owner == i).any()
                          else np.zeros(M.shape[1]) for i in range(len(index))])
        cent /= np.maximum(np.linalg.norm(cent, axis=1, keepdims=True), 1e-9)

        by_stmt = collections.defaultdict(set)
        for r, i in zip(gold_rows, where):
            by_stmt[r["stmt"]].add(index[i])
        gold = {}
        import retrieval_v4 as corpus
        for stmt, questions in corpus.STATEMENTS:
            for q in questions:
                gold[q] = by_stmt.get(stmt, set())

        sel = strict = 0
        per_q = []
        for q, v in zip(qs, qv):
            chosen = np.argsort(-(cent @ v))[:files]
            sel += bool(gold[q] & set(index[i] for i in chosen))
            pool = np.flatnonzero(np.isin(owner, chosen))
            top = pool[np.argsort(-(M[pool] @ v))[:k]] if len(pool) else []
            s_ = answered_row([flat[i] for i in top], q)
            strict += s_
            per_q.append(s_)
        PERQ[name] = np.array(per_q, float)
        n = len(qs)
        print("  %-9s %7d%% %7d%% %7d%% %8d" % (
            name,
            100 * sum(i == j for i, j in zip(where, filings["llm"])) // len(where),
            100 * sel // n, 100 * strict // n, len(set(where))))

    # 87 questions and an 81-row gold set make a 3pp gap two questions wide.
    # Paired bootstrap over questions: every arm answers the same questions,
    # so pairing removes question difficulty from the comparison and what is
    # left is the filing. Seeded, like everything else here.
    rng = np.random.default_rng(4)
    base = PERQ["llm"]
    print("\n  paired bootstrap against llm, 5000 resamples of %d questions:" % len(qs))
    for name in PERQ:
        if name == "llm":
            continue
        d = PERQ[name] - base
        boot = d[rng.integers(0, len(d), (5000, len(d)))].mean(1)
        print("    %-9s strict %+5.1fpp   95%% CI [%+5.1f, %+5.1f]   P(better) %3d%%" % (
            name, 100 * d.mean(), 100 * np.percentile(boot, 2.5),
            100 * np.percentile(boot, 97.5), 100 * (boot > 0).mean()))

    print("\n  agree  = share of rows filed where the Cataloger filed them")
    print("  select = a centroid pick of %d files opens one holding the answer" % files)
    print("  strict = one of k=%d rows then carries the whole key" % k)
    print("  spread = distinct pairs used. A filer that collapses everything")
    print("           into a few files scores well on nothing downstream.")


if __name__ == "__main__":
    args = [int(x) for x in sys.argv[1:3] if x.isdigit()]
    main(*(args or [5]))
