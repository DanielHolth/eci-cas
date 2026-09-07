"""The v4 read arms. Pre-cosine baseline tonight; the vector arms slot in.

    python arms_v4.py [reps]

Four arms are planned (README, v4). Two of them need no embedder and are
what this runs today:

    recall      librarian + the shipped recall.txt
    nopick      librarian + hand everything on, no filtering call
    lenient     librarian + arena.LENIENT, the keep-it-anyway bar
    -- the other two live in cosine_v4.py, which needs no model --
    cosine      librarian as the coarse cut, vectors as the fine one
    flat        no librarian at read time at all

Why these three first. They are the pre-cosine reference line, and they have
to be measured on *this* corpus rather than carried over from v3: v3 is 24
statements at 1.7 rows a file and v4 is 78 at up to 65, so a number from one
does not describe the other. "72% on v3" and "72% on v4" are not comparable
quantities.

Two things this scores that the v3 runner does not.

`nulls` is a first-class column, not a footnote. v3 has 8 nulls against 35
questions and reports them separately, which grades every arm on a curve
that rewards guessing: an arm keeping everything takes full credit for its
recall and pays nothing for volunteering facts nobody asked about. v4 has 28
against 87, and both numbers are printed side by side, because the whole
lenient-versus-strict question is a trade between them and a trade cannot be
read off one side of itself.

`rows` is the candidate-set size handed to Intent. It is not a score, it is
the cost: rows a turn is model calls a turn downstream, and an arm that wins
on answers while tripling the rows has not obviously won.

Arms are interleaved per question rather than run one after another -- the
same discipline as every other batch here. A model server that drifts, warms
up, or gets busy drifts across all arms equally if they alternate, and
entirely into the last arm if they do not.
"""
import sys, collections

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import bench
import read_bench as rb
import retrieval_v4 as corpus
from answers_v4 import ANSWERS
from arena import LENIENT

CACHE = ROOT + "tools/retrieval-bench/.archive_v4_minimal.json"

ARMS = [
    ("recall",  rb.REC),
    ("nopick",  None),
    ("lenient", LENIENT),
]


def answered(rows, question):
    blob = " ".join(rb.line(r).lower() for r in rows)
    return any(all(tok in blob for tok in alt) for alt in ANSWERS[question])


def gold_index(a):
    by_stmt = collections.defaultdict(set)
    for g in a["gold"]:
        by_stmt[g["stmt"]].add(g["pair"])
    out = {}
    for stmt, qs in corpus.STATEMENTS:
        for q in qs:
            out[q] = by_stmt.get(stmt, set())
    return out


def shape(rows):
    sizes = sorted((len(rs) for rs in rows.values()), reverse=True)
    return "%d pairs, %d rows, fattest %s, median %d" % (
        len(sizes), sum(sizes), sizes[:3], sizes[len(sizes) // 2])


def run(reps=1):
    a, rows = rb.archive(CACHE)
    index = sorted(rows)
    gold = gold_index(a)
    print("archive: " + shape(rows))

    # What the write side already lost. A question whose fact never survived
    # extraction cannot be lost by the read path, and charging a reader for
    # it would report the writer's error as a retrieval result.
    writable = set(q for q in ANSWERS
                   if answered([r for p in gold[q] for r in rows[p] if r.get("stmt")], q))
    print("writable: %d/%d\n" % (len(writable), len(ANSWERS)), flush=True)

    tally = collections.Counter()
    for rep in range(reps):
        for q in sorted(ANSWERS):
            # Selection is shared by every arm in this batch. Librarian reads
            # file names and never row text, so no arm here can move it, and
            # re-rolling it per arm would inject selection variance into a
            # comparison that is entirely about picking.
            opened = select_once(q, index, tally, gold)
            pool = [r for p in opened for r in rows[p]]
            for name, prompt in ARMS:
                got = pool if prompt is None else rb.pick(q, pool, prompt)
                tally[name + ".answer"] += answered(got, q)
                tally[name + ".rows"] += len(got)
        for t in corpus.NULLS:
            opened = rb.select(t, index)
            pool = [r for p in opened for r in rows[p]]
            for name, prompt in ARMS:
                got = pool if prompt is None else rb.pick(t, pool, prompt)
                tally[name + ".null_clean"] += (len(got) == 0)
        print("  rep %d done" % (rep + 1), flush=True)

    n = reps * len(ANSWERS)
    nn = reps * len(corpus.NULLS)
    print("\nreps %d   questions %d   nulls %d   writable %d/%d"
          % (reps, len(ANSWERS), len(corpus.NULLS), len(writable), len(ANSWERS)))
    print("  select %d/%d = %d%%   (shared by every arm)"
          % (tally["select"], n, 100 * tally["select"] // n))
    print("\n  %-9s %8s %8s %8s" % ("arm", "answer", "nulls", "rows"))
    for name, _ in ARMS:
        print("  %-9s %7d%% %7d%% %8.1f" % (
            name,
            100 * tally[name + ".answer"] // n,
            100 * tally[name + ".null_clean"] // max(nn, 1),
            tally[name + ".rows"] / float(n)))
    print("\n  answer = of %d answerable questions" % len(ANSWERS))
    print("  nulls  = of %d turns stating no fact, share the arm kept nothing for"
          % len(corpus.NULLS))
    print("  rows   = mean candidate set handed on, the cost side")


def select_once(q, index, tally, gold):
    opened = rb.select(q, index)
    tally["select"] += bool(gold[q] & set(opened))
    return opened


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 1)
