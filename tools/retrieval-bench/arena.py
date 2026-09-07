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
import re, sys, collections

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


# --- selection strategies ---------------------------------------------------
#
# Each is a `select` for an Arm: (question, index, rows) -> pairs to open.
# They exist because the baseline localised the loss precisely -- the reader
# is right about the category 79% of the time and about the pair 36% -- and
# every one of these is a different way of spending that 43pp gap.

def whole_category(gloss=None):
    """Open every topic in the categories the selector chose.

    No extra model call: the categories are already implied by the pairs it
    named. This is the cheapest possible use of the baseline's central fact,
    and it is the honest test of "wider fan-out buys nothing" -- that dead
    end widened by adding more *pairs the model ranked next*, which is more
    of the same signal. This widens along the axis the model got right.

    On the lean shelf it costs 3 categories x 4 topics; on the full one it is
    3 x 17, which is most of an archive and expected to drown Recall. Both
    are run so the cost curve is visible rather than assumed.
    """
    def go(q, index, rows):
        picked = rb.select(q, index, gloss)
        cats = list(dict.fromkeys(p.split("/")[0] for p in picked))
        return picked + [p for p in index
                         if p.split("/")[0] in cats and p not in picked]
    return go


STOP = set("what when where who how why do does did i my me we our us is are "
           "the a an of in on at to for and or any my mine about me's have has "
           "should could would can will there their be been am not no yes it "
           "its this that these those from with by as if than then so".split())


def subject_union(gloss=None, cap=3):
    """Model's pairs, plus pairs holding a row whose subject the question names.

    Daniel's idea, in its cheapest form: no second model, just a string match.
    A subject index is the one projection of the archive's contents that a
    question reliably names -- "Where does Marit live?" contains "Marit", and
    the write side's property rule made subject the segment that holds still
    (Lars 0/10 -> 10/11). The selector cannot use any of that, because it is
    shown file names and a name is not in a file name.

    Union, not intersection, because librarian.txt's asymmetry says so: a
    wrongly-opened topic is filtered in phase two, an unopened one is gone.

    Capped, and appended after the model's picks, so it can add a file the
    selector missed but never displace one it named.
    """
    def go(q, index, rows):
        picked = rb.select(q, index, gloss)
        want = set(w for w in re.findall(r"[a-z0-9]+", q.lower()) if w not in STOP and len(w) > 2)
        extra = []
        for p in index:
            for r in rows[p]:
                if want & set(re.findall(r"[a-z0-9]+", r["subject"].lower())):
                    extra.append(p)
                    break
        return picked + [p for p in extra if p not in picked][:cap]
    return go


def sampled_values(n=2):
    """Show the selector a row or two from each pair instead of a gloss.

    The roadmap's other untried idea: ask the question of the contents rather
    than of the names. A gloss is a guess at what a folder holds; this is what
    it actually holds. Only affordable on the lean shelf -- 40 pairs x 2 rows
    is a prompt, 170 x 2 is not, which is itself an argument for consolidating.
    """
    def go(q, index, rows):
        shown = [p for p in index if not p.endswith("/other")]
        lines = []
        for i, p in enumerate(shown):
            sample = "; ".join(rb.line(r) for r in rows[p][:n])
            lines.append("%d. %s -- %s" % (i, p, sample))
        reply = bench.strip(bench.call(
            rb.LIB.replace("{options}", "\n".join(lines)).replace("{text}", q)
                  .replace("{max}", str(rb.MAX_SELECTED)), 40))
        picked = [shown[i] for i in rb.numbers(reply, len(shown))][:rb.MAX_SELECTED]
        overflow = [p.split("/")[0] + "/other" for p in picked]
        return picked + [p for p in dict.fromkeys(overflow) if p in index and p not in picked]
    return go


ARMS = [
    Arm("full", ".archive_v3.json"),
    Arm("full+gloss", ".archive_v3.json", gloss=GLOSS),
    Arm("lean", ".archive_v3_lean.json", vocab=LEAN),
    Arm("lean+gloss", ".archive_v3_lean.json", vocab=LEAN, gloss=LEAN_GLOSS),
    Arm("lean+wholecat", ".archive_v3_lean.json", vocab=LEAN,
        select=whole_category(LEAN_GLOSS)),
    Arm("lean+subject", ".archive_v3_lean.json", vocab=LEAN,
        select=subject_union(LEAN_GLOSS)),
    Arm("lean+values", ".archive_v3_lean.json", vocab=LEAN,
        select=sampled_values()),
    Arm("full+wholecat", ".archive_v3.json", select=whole_category(GLOSS)),
    Arm("full+subject", ".archive_v3.json", select=subject_union(GLOSS)),
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
