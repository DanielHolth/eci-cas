"""Many read arms, interleaved, against one corpus.

gloss_ab.py compares two ways of showing one shelf. This compares shelves as
well: an arm carries its own vocabulary, and a vocabulary has to be filed
before it can be read, so each one gets its own frozen archive. That is the
confound consolidation brings and there is no way around it -- merging topics
changes where the writer puts a fact as much as where the reader looks.

What keeps it honest anyway:

  * Arms are interleaved question by question, so server drift, thermal
    throttling and a model that wakes up slow are shared by every arm rather
    than landing on whichever ran last.
  * Every arm answers the same questions from the same corpus, and each is
    scored against ITS OWN archive's gold pairs -- a lean arm is credited
    only for opening the pair its own writer used.
  * `writable` is reported per arm. A vocabulary that files a fact into a
    folder where the value gets mangled should be charged for that, and a
    vocabulary whose extraction happened to go badly should not be credited
    with a read win.
  * Category is scored alongside select. The ten categories are the same in
    every vocabulary here on purpose: it is the one number that stays
    comparable when everything else moves.

Judge on sign consistency across reps against a 10pp floor. With 35
questions a rep, 10pp is three or four questions -- so a single rep proves
nothing, and the per-rep line exists to be read rather than the mean.
"""
import sys, collections

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import bench
import retrieval_v3 as corpus
from answers_v3 import ANSWERS
from topic_gloss import GLOSS
from lean_vocab import LEAN, LEAN_GLOSS

BASE = ROOT + "tools/retrieval-bench/"


class Arm:
    """One shelf and one way of showing it."""

    def __init__(self, name, cache, vocab=None, gloss=None, select=None):
        self.name = name
        self.cache = BASE + cache
        self.vocab = vocab
        self.gloss = gloss
        self.select = select or (lambda q, index, rows: rb.select(q, index, self.gloss))

    def load(self):
        a, self.rows = rb.archive(self.cache, self.vocab)
        self.index = sorted(self.rows)
        self.gold = rb.gold_index(a)
        self.writable = set(
            q for q in ANSWERS
            if rb.answered([r for p in self.gold[q] for r in self.rows[p] if r.get("stmt")], q))
        self.t = collections.Counter()
        self.prev = collections.Counter()
        return self

    def ask(self, q):
        opened = self.select(q, self.index, self.rows)
        got = rb.pick(q, [r for p in opened for r in self.rows[p]])
        t = self.t
        t["n"] += 1
        t["select"] += bool(self.gold[q] & set(opened))
        t["cat"] += bool(set(p.split("/")[0] for p in self.gold[q])
                         & set(p.split("/")[0] for p in opened))
        t["answer"] += rb.answered(got, q)
        t["opened"] += len(opened)
        t["rows"] += sum(len(self.rows[p]) for p in opened)
        return opened, got

    def delta(self):
        d = " ".join("%s %2d" % (k, self.t[k] - self.prev[k]) for k in ("cat", "select", "answer"))
        self.prev = self.t.copy()
        return d


ARMS = [
    Arm("full", ".archive_v3.json"),
    Arm("full+gloss", ".archive_v3.json", gloss=GLOSS),
    Arm("lean", ".archive_v3_lean.json", vocab=LEAN),
    Arm("lean+gloss", ".archive_v3_lean.json", vocab=LEAN, gloss=LEAN_GLOSS),
]


def run(reps, arms):
    for arm in arms:
        arm.load()
        print("%-12s %3d pairs   writable %d/%d" % (
            arm.name, len(arm.index), len(arm.writable), len(ANSWERS)))
    print()
    for rep in range(reps):
        for q in sorted(ANSWERS):
            for arm in arms:
                arm.ask(q)
        print("rep%d  " % (rep + 1) + "   ".join(
            "%-11s %s" % (a.name, a.delta()) for a in arms), flush=True)

    print("\n%-12s %8s %8s %8s %8s %8s" % ("arm", "category", "select", "answer", "files", "rows"))
    for arm in arms:
        t, n = arm.t, arm.t["n"]
        print("%-12s %7d%% %7d%% %7d%% %8.1f %8.1f" % (
            arm.name, 100 * t["cat"] // n, 100 * t["select"] // n, 100 * t["answer"] // n,
            t["opened"] / n, t["rows"] / n))


if __name__ == "__main__":
    want = [a for a in sys.argv[1:] if not a.isdigit()]
    reps = next((int(a) for a in sys.argv[1:] if a.isdigit()), 3)
    run(reps, [a for a in ARMS if not want or a.name in want])
