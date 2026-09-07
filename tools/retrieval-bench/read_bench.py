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
import json, os, re, sys, random, collections

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


def plan_sizes(pairs, gold=None, seed=4):
    """How many rows each unpopulated pair should get. Lumpy, not uniform.

    Uniform padding is what v3 had -- three rows everywhere -- and it
    flatters both readers. The picker gets real discrimination pressure in
    every file it opens, and a cosine sweep gets uniform density with no
    thin files where a wrong-but-close row wins because nothing better is
    present. Real archives are a few fat pairs and a long tail of two-row
    ones, and the fat end is the regime Recall cannot afford: rows a turn is
    model calls a turn.

    The bands are the pre-registered ones (README, v4). Seeded so the
    archive is reproducible: two arms that disagree about which pairs are
    fat are not two views of one archive.

    `gold` is the set of pairs a statement actually landed in, and passing it
    is what makes the fat band mean anything. Drawn blind, the bands land on
    gold pairs at their population rate -- 8 fat files over 170 pairs put two
    of them on gold, four rows out of eighty-two -- so the case the lumpiness
    exists to create, an answer buried among sixty distractors, would be
    measured on almost no questions and read as noise either way. Half of each
    fat and middle slot is therefore drawn from gold-bearing pairs, which is a
    statement about what the instrument must be able to see, not about what
    real archives look like. The thin tail keeps whatever is left, so most
    answers still sit in small files and the easy case stays the common one.
    """
    r = random.Random(seed)
    gold = sorted(set(gold or ()) & set(pairs))
    rest = sorted(set(pairs) - set(gold))
    r.shuffle(gold)
    r.shuffle(rest)

    def take(n):
        # Half from gold where there is gold left, remainder from the rest,
        # and back to whichever list still has entries when one runs out.
        out = []
        for _ in range(n):
            src = gold if (gold and (len(out) % 2 == 0 or not rest)) else rest
            if not src:
                src = gold or rest
            if not src:
                break
            out.append(src.pop())
        return out

    sizes = {}
    for count, lo, hi in ((8, 50, 80), (40, 10, 25)):
        for p in take(count):
            sizes[p] = r.randint(lo, hi)
    for p in gold + rest:
        sizes[p] = r.randint(1, 4)
    return sizes


def pad_rows(pair, n=3):
    """`n` plausible rows for a pair no statement landed in.

    Padding has to have content or `select` is the only stage that can fail:
    an empty file cannot mislead the picker and cannot supply a wrong answer.
    Generated once and cached -- these are distractors, and a distractor that
    changed between arms would be an uncontrolled variable.

    Padding carries a sentence for the same reason it carries content at all.
    An archive where only the gold rows have one would let a sentence-aware
    reader find them by the presence of the field rather than by what it
    says, and the arm would measure the marking, not the mechanism. Archives
    cached before this only ever get read address-only, so they are unharmed.
    """
    cat, topic = pair.split("/")
    rows, seen, stall = [], set(), 0
    # Asked in batches rather than in one call: a small model asked for
    # sixty facts returns a dozen and then repeats itself. The dedupe below
    # is what makes a fat pair actually fat -- without it a 60-row target
    # fills with the same four rows and the file is wide but not varied,
    # which is the wrong distractor. `stall` gives up rather than looping
    # forever on a pair the model has run out of ideas about; the pair ends
    # up smaller than planned and the printed distribution says so.
    while len(rows) < n and stall < 4:
        want = min(6, n - len(rows))
        prompt = (str(want) + " short facts a person might have on file under '"
                  + cat + " / " + topic + "'. One per line, no numbering, in the form:\n"
                  "subtopic=<1-2 words> subject=<1-2 words> key=<1-3 words> value=<1-4 keywords>"
                  " sentence=<the same fact as one plain sentence>\n"
                  "Do not mention " + cat + " or " + topic + " as the subject.")
        before = len(rows)
        for part in bench.ROWSPLIT.split(bench.strip(bench.call(prompt, 60 * want))):
            f = bench.fields(part)
            if not f:
                continue
            sig = (f.get("subject", ""), f.get("key", ""), f.get("value", ""))
            if sig in seen:
                continue
            seen.add(sig)
            rows.append(dict(f, pair=pair))
        stall = 0 if len(rows) > before else stall + 1
    return rows[:n]


def build(vocab=None, cat_prompt=None, sizes=None, src=None):
    """Real write path over the corpus, then padding everywhere else.

    `sizes` is pair -> row count; None keeps v3's flat three everywhere, so
    every arm written against the old archives is untouched. `src` overrides
    which corpus module supplies the statements.
    """
    src = src or corpus
    gold, pad = [], {}
    prompt = bench.load("archivist.txt")["main"]
    for i, (stmt, _) in enumerate(src.STATEMENTS, 1):
        print("  write %d/%d: %s" % (i, len(src.STATEMENTS), stmt[:44]), flush=True)
        for pair, row in bench.write(stmt, prompt, vocab, cat_prompt):
            gold.append(dict(row, pair=pair, stmt=stmt))
    # A shelf whose category prompt names drawers it does not have files
    # everything to unfiled/unfiled and still produces a table. That is how
    # the terse shelf's first batch was read as a shelf result when it was a
    # harness bug, so it is an error now rather than a footnote.
    #
    # Proportional, not absolute. The failure this catches is systematic --
    # a prompt naming drawers the vocabulary does not have sends every row
    # to unfiled and still produces a table. A couple of rows going there is
    # a different thing entirely: it is the Cataloger missing, which is a
    # write-side result the bench exists to measure, and aborting on it
    # would refuse to run whenever the model is imperfect. Named rows are
    # printed either way so the number is never silent.
    stray = [g for g in gold if g["pair"].startswith("unfiled")]
    if stray:
        print("  %d of %d rows filed to unfiled/unfiled:" % (len(stray), len(gold)))
        for g in stray[:8]:
            print("    %s | %s" % (g.get("stmt", "?")[:40], g.get("key", "?")))
    if len(gold) and len(stray) > 0.2 * len(gold):
        raise SystemExit(
            "%d of %d rows filed to unfiled/unfiled: that is systematic, not "
            "model error -- the category prompt and the vocabulary disagree. "
            "Pass cat_prompt for a shelf that renames a category."
            % (len(stray), len(gold)))

    landed = collections.Counter(g["pair"] for g in gold)
    # `sizes` may be a callable, and for v4 it is. Which pairs hold gold is
    # not knowable until the write pass has run -- the Cataloger decides it,
    # and it is allowed to surprise us -- so a size plan fixed beforehand
    # cannot put fat files where the answers are. Planning here costs nothing
    # and is still seeded, so the archive stays reproducible given the same
    # write pass.
    if callable(sizes):
        sizes = sizes(all_pairs(vocab), set(landed))
    # With a flat target, a pair holding gold is already about the right size
    # and v3 skips it -- 1.7 rows against a target of 3 is not a difference
    # worth 170 extra calls. With `sizes` that reasoning inverts and becomes a
    # bug: every gold row would sit alone in a file of one to three while all
    # the fat files were pure distractor, so the answer could never be hiding
    # inside a fat file and the lumpy distribution would measure nothing. The
    # gold rows are what a fat file has to bury. So pairs holding gold are
    # topped up to their planned size, counting the gold rows they already
    # have. Scoped to `sizes` so every v3 archive keeps its exact semantics.
    todo = [p for p in all_pairs(vocab) if sizes or p not in landed]
    for i, pair in enumerate(todo, 1):
        n = (sizes.get(pair, 3) - landed[pair]) if sizes else 3
        if n <= 0:
            continue
        print("  pad %d/%d (%d rows, %d gold): %s"
              % (i, len(todo), n, landed[pair], pair), flush=True)
        pad[pair] = pad_rows(pair, n)
    return {"gold": gold, "pad": pad}


def archive(cache=CACHE, vocab=None, cat_prompt=None, sizes=None, src=None):
    """The frozen archive for one vocabulary. One cache file per vocabulary:
    a merged shelf has to be filed as well as read, so two arms that differ
    in vocabulary are two archives, not two views of one."""
    if not os.path.exists(cache):
        print("building archive (once): %s" % os.path.basename(cache), flush=True)
        # Built before the file is opened, not into it. Opening first means
        # a build that raises -- the unfiled guard, a dead model server --
        # leaves a zero-byte cache behind, which then satisfies the "exists,
        # delete to rebuild" check above and turns the next run into a
        # silent no-op over an empty archive. That reports a total collapse
        # indistinguishable from a result, which is the failure this whole
        # harness keeps having.
        built = build(vocab, cat_prompt, sizes, src)
        with open(cache, "w", encoding="utf-8") as f:
            json.dump(built, f, indent=1)
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


def embed_text(r):
    """What to hand the embedder for a row: the address line and the sentence.

    Measured, not assumed. Over the 80 writable v4 questions, flat top-5 reads
    88% on the address line alone, 88% on the sentence alone, and 92% on the
    two concatenated; a centroid shelf reads 76 / 70 / 81. Neither field
    subsumes the other -- the line carries the vocabulary and the exact value,
    the sentence carries the phrasing the embedder was trained on -- so the
    concatenation beats either, and it is free because both are already
    stored. 99% of v4 rows have a sentence; the ones that do not fall back to
    the line rather than embedding an empty string.
    """
    return line(r) + ". " + (r.get("sentence") or "")


def pick(question, rows, prompt=None, show=None):
    """RecallAgent: chunk by RowsPerWorker, keep at most MaxPickedPerWorker.

    `prompt` is an arm: a replacement for recall.txt with the same three
    placeholders. Default is the shipped one.

    `show` is how one row is rendered into the listing, defaulting to the
    address line -- which is what ships, and is a bug rather than a choice.
    Every row carries a sentence saying the fact in plain words, the embedder
    has read it since a7096f4, and Recall never saw it: it has been deciding
    what to keep from category/topic/subject/key/value alone. `raw_v4` shows
    that line drops 6pp of facts the sentence holds, so Recall was being asked
    to filter partly blind. Pass embed_text to show it what the vectors see.
    """
    show = show or line
    kept = []
    for i in range(0, len(rows), ROWS_PER_WORKER):
        chunk = rows[i:i + ROWS_PER_WORKER]
        listing = "\n".join("%d. %s" % (j, show(r)) for j, r in enumerate(chunk))
        reply = bench.strip(bench.call(
            (prompt or REC).replace("{rows}", listing).replace("{text}", question)
               .replace("{max}", str(MAX_PICKED)), 40))
        kept += [chunk[j] for j in numbers(reply, len(chunk))][:MAX_PICKED]
    return kept


# --- scoring ----------------------------------------------------------------

def answered(rows, question):
    """Was the fact in the candidate set?

    Scores embed_text, not line. It scored line until 2026-09-08, which made
    every write-side and read-side number in batches 3-13 an understatement:
    the sentence field is written by the Archivist, stored in the archive and
    read by the embedder, and the scorer alone ignored it. The measured cost
    of that was 6pp of ceiling and 10pp of strict retrieval (raw_v4). Numbers
    from before the fix are not comparable to numbers after it."""
    blob = " ".join(embed_text(r).lower() for r in rows)
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
