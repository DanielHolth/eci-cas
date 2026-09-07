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

# recall.txt is tuned hard against false positives -- its header records
# greetings picking rows 3/4/1 times out of five until the "reply none" line
# went in. The oracle arms say that tuning now costs 16pp on real questions
# with the right pair already open, so this is the other side of the dial:
# same shape, same parser contract, a bar of "might help" instead of
# "answering without it would be wrong". It is only a candidate if it keeps
# the NULLS clean, which is why they are scored here and were not before.
LENIENT = '''Pick any fact that might help answer the turn. Prefer keeping a
fact over dropping it: a fact that turns out not to help costs little, a fact
that is dropped cannot be recovered. If the turn asks nothing about the person
-- a greeting, an acknowledgement, small talk -- reply none.

Reply with the numbers of the facts to keep, best first, bare digits,
comma-separated (e.g. "0, 2"), at most {max}. If none, reply "none".

Candidate facts (index: subtopic / subject key = value), most important first:
{rows}

Turn: {text}'''

BASE = ROOT + "tools/retrieval-bench/"


class Arm:
    """One shelf and one way of showing it."""

    def __init__(self, name, cache, vocab=None, gloss=None, select=None, oracle=False, pick=True, rec=None):
        self.name = name
        self.oracle = oracle
        self.pick = pick
        self.rec = rec
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
        # The oracle opens the pair the writer actually used. It is not a
        # strategy -- nothing at runtime knows the gold pair -- it is the
        # ceiling: whatever `answer` it fails to reach is lost by Recall or
        # by the row itself, and no selector however good can win it back.
        opened = (sorted(self.gold[q]) if self.oracle
                  else self.select(q, self.index, self.rows))
        rows = [r for p in opened for r in self.rows[p]]
        # An arm with pick=False hands Recall's input straight to the answer.
        # That is a shippable configuration and not just a ceiling: on the
        # lean shelf whole-category opens ten rows, which is a smaller prompt
        # than the one Recall is given to sift it with. If the numbers match,
        # the stage is paying a call to subtract nothing.
        got = rb.pick(q, rows, self.rec) if self.pick else rows
        t = self.t
        t["n"] += 1
        t["select"] += bool(self.gold[q] & set(opened))
        t["cat"] += bool(set(p.split("/")[0] for p in self.gold[q])
                         & set(p.split("/")[0] for p in opened))
        t["answer"] += rb.answered(got, q)
        t["opened"] += len(opened)
        t["rows"] += sum(len(self.rows[p]) for p in opened)
        return opened, got

    def ask_null(self, q):
        """A turn that must come back empty. Scored because dropping Recall
        means every greeting hands the answerer whatever the selector opened,
        and an arm that wins on answers by leaking rows into small talk has
        not won."""
        opened = self.select(q, self.index, self.rows)
        rows = [r for p in opened for r in self.rows[p]]
        got = rb.pick(q, rows, self.rec) if self.pick else rows
        self.t["null_n"] += 1
        self.t["null_ok"] += not got
        return got

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


def values_category():
    """The two arms that won, composed: pick pairs by their contents, then
    open the rest of those categories anyway. sampled_values and
    whole_category were the top two of the first six and they disagree about
    what the selector should be shown, so this is the test of whether they
    are two routes to the same win or two wins that add."""
    inner = sampled_values()

    def go(q, index, rows):
        picked = inner(q, index, rows)
        cats = list(dict.fromkeys(p.split("/")[0] for p in picked))
        return picked + [p for p in index
                         if p.split("/")[0] in cats and p not in picked]
    return go


def two_stage(gloss=None, cats=2):
    """Categories first, then topics inside the ones chosen.

    The hierarchical read is a recorded dead end (-35pp, the only read result
    that ever cleared its floor) and it is being re-run because the reason
    given for it turned out to be false. It was written off as "the right
    drawer is not guessable from the question"; the drawer is guessable 79%
    of the time. If the two-step still loses with that known, the cause is
    the commitment itself -- one category chosen means the second-best drawer
    is gone -- which is why this takes two categories rather than one.
    """
    def go(q, index, rows):
        cat_list = sorted(set(p.split("/")[0] for p in index))
        options = "\n".join("%d. %s" % (i, c) for i, c in enumerate(cat_list))
        reply = bench.strip(bench.call(
            rb.LIB.replace("{options}", options).replace("{text}", q)
                  .replace("{max}", str(cats)), 40))
        chosen = [cat_list[i] for i in rb.numbers(reply, len(cat_list))][:cats]
        if not chosen:
            return rb.select(q, index, gloss)
        inner = [p for p in index if p.split("/")[0] in chosen]
        return rb.select(q, inner, gloss)
    return go


def subject_llm(gloss=None, cap=3):
    """Daniel's design as he framed it: a second weak model, on subjects only.

    The lexical arm cannot resolve "my sister" to Marit, and that is the whole
    class of oblique question the bonus tier exists for. This asks a model
    which of the archive's known subjects the turn is about, then unions the
    pairs holding them. A curated index, not an enumerated one -- only
    subjects that appear in more than a padding row would survive at scale,
    and this list is already only what the archive holds.

    Union and cap as in subject_union: it may add a file the selector missed,
    never displace one it named.
    """
    def go(q, index, rows):
        picked = rb.select(q, index, gloss)
        subs = sorted(set(r["subject"].lower() for p in index for r in rows[p]
                          if r.get("stmt")))
        options = "\n".join("%d. %s" % (i, s) for i, s in enumerate(subs))
        reply = bench.strip(bench.call(
            "Which of these does the turn ask about? Reply with the numbers, "
            "bare digits, comma-separated, at most %d. If none, reply \"none\".\n\n"
            "Known subjects:\n%s\n\nTurn: %s" % (cap, options, q), 40))
        want = set(subs[i] for i in rb.numbers(reply, len(subs)))
        extra = [p for p in index
                 if any(r["subject"].lower() in want for r in rows[p])]
        return picked + [p for p in extra if p not in picked][:cap]
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
    Arm("lean+2stage", ".archive_v3_lean.json", vocab=LEAN,
        select=two_stage(LEAN_GLOSS)),
    Arm("lean+subjllm", ".archive_v3_lean.json", vocab=LEAN,
        select=subject_llm(LEAN_GLOSS)),
    Arm("lean+oracle", ".archive_v3_lean.json", vocab=LEAN, oracle=True),
    Arm("full+oracle", ".archive_v3.json", oracle=True),
    Arm("lean+wc+nopick", ".archive_v3_lean.json", vocab=LEAN,
        select=whole_category(LEAN_GLOSS), pick=False),
    Arm("full+wc+nopick", ".archive_v3.json",
        select=whole_category(GLOSS), pick=False),
    Arm("lean+or+nopick", ".archive_v3_lean.json", vocab=LEAN,
        oracle=True, pick=False),
    Arm("lean+val+wc", ".archive_v3_lean.json", vocab=LEAN,
        select=values_category()),
    Arm("lean+wc+lenient", ".archive_v3_lean.json", vocab=LEAN,
        select=whole_category(LEAN_GLOSS), rec=LENIENT),
    Arm("lean+or+lenient", ".archive_v3_lean.json", vocab=LEAN,
        oracle=True, rec=LENIENT),
    # The two prompt-only changes on the shipped shelf. Neither touches the
    # vocabulary, so neither can damage filing, and the write side is where
    # consolidation lost what the read side gained.
    Arm("full+gloss+len", ".archive_v3.json", gloss=GLOSS, rec=LENIENT),
    Arm("full+len", ".archive_v3.json", rec=LENIENT),
]


def run(reps, arms):
    for arm in arms:
        arm.load()
        print("%-12s %3d pairs   writable %d/%d" % (
            arm.name, len(arm.index), len(arm.writable), len(ANSWERS)), flush=True)
    print(flush=True)
    for rep in range(reps):
        for q in sorted(ANSWERS):
            for arm in arms:
                arm.ask(q)
        for q in corpus.NULLS:
            for arm in arms:
                arm.ask_null(q)
        print("rep%d  " % (rep + 1) + "   ".join(
            "%-11s %s" % (a.name, a.delta()) for a in arms), flush=True)

    print("\n%-12s %8s %8s %8s %8s %8s" % ("arm", "category", "select", "answer", "files", "rows"))
    for arm in arms:
        t, n = arm.t, arm.t["n"]
        nn = max(t["null_n"], 1)
        print("%-14s %7d%% %7d%% %7d%% %5d%% %8.1f %8.1f" % (
            arm.name, 100 * t["cat"] // n, 100 * t["select"] // n, 100 * t["answer"] // n,
            100 * t["null_ok"] // nn, t["opened"] / n, t["rows"] / n))


if __name__ == "__main__":
    want = [a for a in sys.argv[1:] if not a.isdigit()]
    reps = next((int(a) for a in sys.argv[1:] if a.isdigit()), 3)
    run(reps, [a for a in ARMS if not want or a.name in want])
