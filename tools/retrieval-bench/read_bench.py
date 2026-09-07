"""Read-side bench: where does a question lose its answer?

The write-side bench (bench.py) scores one number per arm. This one refuses
to, because the read path has three places to lose a fact and docs/roadmap.md
only ever measured the first:

  select  -- did Librarian open the pair the fact was written to
  pick    -- did Recall keep the row out of what was opened
  answer  -- were the answer's tokens in what came back

58% in the roadmap is `select` alone, on an archive whose padding pairs were
empty files. Both of those are fixed here: retrieval_v3 is 35 direct
questions instead of 17, and every pair in the vocabulary is populated, so a
wrong pick returns something plausible rather than nothing.

Three things this deliberately mirrors from LibrarianAgent rather than
reimplementing:

  * "other" topics are hidden from the selector (Shown) -- 82% hidden against
    71% visible, so a bench that showed them would measure a system nobody
    runs;
  * "category/other" is appended in code for every category selected
    (WithOverflow);
  * selection is capped at MaxSelectedPairs and picking at
    MaxPickedPerWorker, from appsettings.json.

The archive is frozen to a cache file. Extraction and filing are the noisiest
part of the whole system, and an arm that re-rolled them would be measuring
the write side again -- the same reason bench.py extracts once per rep and
files the same rows through every arm. Delete the cache to rebuild; do not
delete it between two arms you intend to compare.
"""
import json, os, re, sys, collections

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import bench
import retrieval_v3 as corpus
from answers_v3 import ANSWERS

CACHE = ROOT + "tools/retrieval-bench/.archive_v3.json"
MAX_SELECTED = 3     # appsettings.json Librarian:MaxSelectedPairs
MAX_PICKED = 5       # appsettings.json Recall:MaxPickedPerWorker
ROWS_PER_WORKER = 50 # appsettings.json Recall:RowsPerWorker

LIB = bench.load("librarian.txt")["main"]
REC = bench.load("recall.txt")["main"]


# --- the archive ------------------------------------------------------------

def all_pairs(vocab=None):
    v = vocab or bench.VOCAB
    return [c + "/" + t for c, ts in v.items() for t in ts]


def pad_rows(pair):
    """Three plausible rows for a pair no statement landed in.

    Padding has to have content or `select` is the only stage that can fail:
    an empty file cannot mislead the picker and cannot supply a wrong answer.
    Generated once and cached -- these are distractors, and a distractor that
    changed between arms would be an uncontrolled variable.
    """
    cat, topic = pair.split("/")
    prompt = ("Three short facts a person might have on file under '"
              + cat + " / " + topic + "'. One per line, no numbering, in the form:\n"
              "subtopic=<1-2 words> subject=<1-2 words> key=<1-3 words> value=<1-4 keywords>\n"
              "Do not mention " + cat + " or " + topic + " as the subject.")
    rows = []
    for part in bench.ROWSPLIT.split(bench.strip(bench.call(prompt, 200))):
        f = bench.fields(part)
        if f:
            rows.append(dict(f, pair=pair))
    return rows[:3]


def build(vocab=None):
    """Real write path over the corpus, then padding everywhere else."""
    gold, pad = [], {}
    prompt = bench.load("archivist.txt")["main"]
    for i, (stmt, _) in enumerate(corpus.STATEMENTS, 1):
        print("  write %d/%d: %s" % (i, len(corpus.STATEMENTS), stmt[:44]), flush=True)
        for pair, row in bench.write(stmt, prompt, vocab):
            gold.append(dict(row, pair=pair, stmt=stmt))
    landed = set(g["pair"] for g in gold)
    todo = [p for p in all_pairs(vocab) if p not in landed]
    for i, pair in enumerate(todo, 1):
        print("  pad %d/%d: %s" % (i, len(todo), pair), flush=True)
        pad[pair] = pad_rows(pair)
    return {"gold": gold, "pad": pad}


def archive(cache=CACHE, vocab=None):
    """The frozen archive for one vocabulary. One cache file per vocabulary:
    a merged shelf has to be filed as well as read, so two arms that differ
    in vocabulary are two archives, not two views of one."""
    if not os.path.exists(cache):
        print("building archive (once): %s" % os.path.basename(cache), flush=True)
        with open(cache, "w", encoding="utf-8") as f:
            json.dump(build(vocab), f, indent=1)
    with open(cache, encoding="utf-8") as f:
        a = json.load(f)
    rows = collections.defaultdict(list)
    for g in a["gold"]:
        rows[g["pair"]].append(g)
    for pair, rs in a["pad"].items():
        rows[pair].extend(rs)
    return a, rows


# --- the two model stages ---------------------------------------------------

def numbers(reply, ceiling):
    if "none" in reply.lower():
        return []
    return [int(n) for n in re.findall(r"\d+", reply) if int(n) < ceiling]


def select(question, index, gloss=None):
    """LibrarianAgent: hide 'other', ask, then re-add category/other in code.

    `gloss` is an arm, not a feature: {category: {topic: "a few words"}},
    appended to each option line. Nothing downstream sees it -- the pairs
    returned are the same strings either way, so an arm cannot accidentally
    change what gets opened or how a hit is scored.
    """
    shown = [p for p in index if not p.endswith("/other")]

    def label(p):
        if not gloss:
            return p
        c, t = p.split("/", 1)
        words = gloss.get(c, {}).get(t)
        return "%s (%s)" % (p, words) if words else p

    options = "\n".join("%d. %s" % (i, label(p)) for i, p in enumerate(shown))
    reply = bench.strip(bench.call(
        LIB.replace("{options}", options).replace("{text}", question)
           .replace("{max}", str(MAX_SELECTED)), 40))
    picked = [shown[i] for i in numbers(reply, len(shown))][:MAX_SELECTED]
    overflow = [p.split("/")[0] + "/other" for p in picked]
    return picked + [p for p in dict.fromkeys(overflow) if p in index and p not in picked]


def line(r):
    return "%s %s %s = %s" % (r.get("subtopic", "-"), r["subject"], r["key"], r["value"])


def pick(question, rows):
    """RecallAgent: chunk by RowsPerWorker, keep at most MaxPickedPerWorker."""
    kept = []
    for i in range(0, len(rows), ROWS_PER_WORKER):
        chunk = rows[i:i + ROWS_PER_WORKER]
        listing = "\n".join("%d. %s" % (j, line(r)) for j, r in enumerate(chunk))
        reply = bench.strip(bench.call(
            REC.replace("{rows}", listing).replace("{text}", question)
               .replace("{max}", str(MAX_PICKED)), 40))
        kept += [chunk[j] for j in numbers(reply, len(chunk))][:MAX_PICKED]
    return kept


# --- scoring ----------------------------------------------------------------

def answered(rows, question):
    blob = " ".join(line(r).lower() for r in rows)
    return any(all(tok in blob for tok in alt) for alt in ANSWERS[question])


def gold_index(a):
    """question -> the pairs its statement's rows were actually filed to."""
    by_stmt = collections.defaultdict(set)
    for g in a["gold"]:
        by_stmt[g["stmt"]].add(g["pair"])
    out = {}
    for stmt, qs in corpus.STATEMENTS:
        for q in qs:
            out[q] = by_stmt.get(stmt, set())
    return out


def run(reps=1, verbose=True):
    a, rows = archive()
    index = sorted(rows)
    gold = gold_index(a)

    # A question whose fact never survived extraction cannot be lost by the
    # read path, and scoring it as a read miss would charge the reader for
    # the writer's error. Counted and reported, not excluded: the headline
    # stays end-to-end, and `writable` says how much of the gap was already
    # gone before Librarian saw anything.
    writable = set(q for q in ANSWERS
                   if answered([r for p in gold[q] for r in rows[p] if r.get("stmt")], q))

    tally = collections.Counter()
    for _ in range(reps):
        for q in sorted(ANSWERS):
            opened = select(q, index)
            hit = bool(gold[q] & set(opened))
            got = pick(q, [r for p in opened for r in rows[p]])
            ok = answered(got, q)
            # Category-level hit, scored separately because the first run
            # said the two come apart hard: 14 of 19 selection misses had
            # opened the right category and the wrong topic. "The right
            # drawer is not guessable from the question" (docs/roadmap.md,
            # from the hierarchical result) is not what this measures --
            # the drawer is guessable, the folder inside it is not.
            gc = set(p.split("/")[0] for p in gold[q])
            oc = set(p.split("/")[0] for p in opened)
            tally["n"] += 1
            tally["select"] += hit
            tally["select_cat"] += bool(gc & oc)
            tally["answer"] += ok
            if hit:
                tally["pick_n"] += 1
                tally["pick"] += any(r.get("stmt") and r["pair"] in gold[q] for r in got)
            if verbose and not ok:
                where = "unwritable" if q not in writable else ("select" if not hit else "pick")
                print("  miss[%-10s] %s  opened=%s" % (where, q, ",".join(opened) or "-"))

    n = tally["n"]
    pn = max(tally["pick_n"], 1)
    print("\nreps %d   questions %d   writable %d/%d" % (reps, n, len(writable), len(ANSWERS)))
    print("  category %d/%d = %d%%" % (
        tally["select_cat"], n, 100 * tally["select_cat"] // n))
    print("  select %d/%d = %d%%   pick %d/%d = %d%%   answer %d/%d = %d%%" % (
        tally["select"], n, 100 * tally["select"] // n,
        tally["pick"], tally["pick_n"], 100 * tally["pick"] // pn,
        tally["answer"], n, 100 * tally["answer"] // n))


def oblique():
    """Bonus tier. Never folded into the headline -- see retrieval_v3."""
    a, rows = archive()
    index = sorted(rows)
    by_stmt = collections.defaultdict(set)
    for g in a["gold"]:
        by_stmt[g["stmt"]].add(g["pair"])
    good = ans = 0
    for q, stmt, alts in corpus.OBLIQUE:
        want = by_stmt[stmt]
        opened = select(q, index)
        g = bool(want & set(opened))
        got = pick(q, [r for p in opened for r in rows[p]])
        blob = " ".join(line(r).lower() for r in got)
        a_ok = any(all(t in blob for t in alt) for alt in alts)
        good += g
        ans += a_ok
        print("  %s%s  %s\n        filed=%s\n        opened=%s" % (
            "HIT " if g else "    ", "ANS" if a_ok else "   ", q,
            ",".join(sorted(want)) or "-", ",".join(opened) or "-"))
    print("\noblique: filed pair opened %d/%d   answered %d/%d" % (
        good, len(corpus.OBLIQUE), ans, len(corpus.OBLIQUE)))


if __name__ == "__main__":
    if "--oblique" in sys.argv:
        oblique()
    else:
        run(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 1)
