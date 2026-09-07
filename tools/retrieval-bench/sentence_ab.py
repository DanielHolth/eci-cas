"""Does the sentence give Recall enough surface to stop discarding the row?

Batch 12 localised the largest single loss in the log: with the same shelf
and the same archive, `nopick` answers 78% and the lenient bar 60%. Recall
discarding rows costs 18pp, more than pair selection or any shelf choice
measured. The hypothesis is that it discards them because the row it judges
-- "renewal / passport expiry = 2027-03" -- matches almost nothing the
question says, and that restating the fact as a sentence gives it something
to match against.

Pre-registered, so the result can falsify rather than be interpreted:

  the sentence should lift the FILTERED arms and barely move `nopick`.

`nopick` keeps every row it opened, so more surface cannot help it -- if the
mechanism is right, the gap between the two closes from below. A uniform lift
on both is not this mechanism and the story is wrong. `select` must not move
at all: Librarian chooses over file names and never sees row text, so a
change there is a harness bug, not a result.

The control, which is the part that took two batches to learn:

  * ONE extraction per statement, in one archive both renderings read. The
    arms differ in how a row is shown to Recall and in nothing else. Running
    a second extraction per arm would be a different experiment, and it is
    the experiment that moved 81% -> 100% on a byte-identical prompt in
    write_gloss_ab.py.
  * ONE selection per question, shared by every arm. Same reason.
  * Scoring is address-only for every arm (rb.answered is untouched). If the
    sentence rendering were scored on what it hands downstream, the arm would
    be credited for restating the answer rather than for keeping the row.
  * Padding carries sentences too -- see read_bench.pad_rows. An archive
    where only gold rows had one would let the reader find them by the
    presence of the field.

The archive is its own cache: it is filed by the prompt that writes
sentences, so it is not the same archive as .archive_v3.json and must not be
compared across to it. Everything here is a within-batch comparison.

  python sentence_ab.py [reps]
"""
import sys, collections

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import retrieval_v3 as corpus
from answers_v3 import ANSWERS
from topic_gloss import GLOSS
from arena import LENIENT, whole_category

CACHE = ROOT + "tools/retrieval-bench/.archive_v3_sent.json"


def address(r):
    """What Recall is shown today. rb.line, kept local so the two renderings
    sit side by side and neither is 'the default'."""
    return "%s %s %s = %s" % (r.get("subtopic", "-"), r["subject"], r["key"], r["value"])


def sentence(r):
    """ArchiveRecord.Rendered: the address, then the sentence after an em
    dash where the writer produced one. Both, not either -- a row without a
    sentence must still render, and within one prompt the rows have to look
    like each other."""
    s = r.get("sentence", "")
    return address(r) + (" -- " + s if s else "")


# (name, renderer, recall prompt or None for nopick). The two nopick arms
# would be identical by construction -- with no recall call there is nothing
# for a renderer to change -- so there is one, and it is the ceiling both
# filtered pairs are read against.
ARMS = [
    ("nopick", None, None),
    ("strict+addr", address, rb.REC),
    ("strict+sent", sentence, rb.REC),
    ("lenient+addr", address, LENIENT),
    ("lenient+sent", sentence, LENIENT),
]


def pick(question, rows, prompt, render):
    """rb.pick with the row rendering as a parameter. Same chunking, same
    caps, same parser -- the arm is the string a row turns into."""
    kept = []
    for i in range(0, len(rows), rb.ROWS_PER_WORKER):
        chunk = rows[i:i + rb.ROWS_PER_WORKER]
        listing = "\n".join("%d. %s" % (j, render(r)) for j, r in enumerate(chunk))
        reply = rb.bench.strip(rb.bench.call(
            prompt.replace("{rows}", listing).replace("{text}", question)
                  .replace("{max}", str(rb.MAX_PICKED)), 40))
        kept += [chunk[j] for j in rb.numbers(reply, len(chunk))][:rb.MAX_PICKED]
    return kept


def run(reps=3):
    a, rows = rb.archive(CACHE)
    index = sorted(rows)
    gold = rb.gold_index(a)
    writable = set(q for q in ANSWERS
                   if rb.answered([r for p in gold[q] for r in rows[p] if r.get("stmt")], q))
    with_sentence = sum(bool(r.get("sentence")) for rs in rows.values() for r in rs)
    total = sum(len(rs) for rs in rows.values())

    # Said before the table, because a low number here means the arms below
    # are two names for the same rendering and the batch proves nothing.
    print("%d pairs   writable %d/%d   rows carrying a sentence %d/%d" % (
        len(index), len(writable), len(ANSWERS), with_sentence, total), flush=True)
    print(flush=True)

    t = {name: collections.Counter() for name, _, _ in ARMS}
    prev = {name: collections.Counter() for name, _, _ in ARMS}
    sel = collections.Counter()
    select = whole_category(GLOSS)

    for rep in range(reps):
        for q in sorted(ANSWERS):
            opened = select(q, index, rows)          # once, shared by every arm
            candidates = [r for p in opened for r in rows[p]]
            sel["n"] += 1
            sel["select"] += bool(gold[q] & set(opened))
            for name, render, prompt in ARMS:
                got = candidates if prompt is None else pick(q, candidates, prompt, render)
                t[name]["n"] += 1
                t[name]["answer"] += rb.answered(got, q)
                t[name]["kept"] += len(got)
        for q in corpus.NULLS:
            opened = select(q, index, rows)
            candidates = [r for p in opened for r in rows[p]]
            for name, render, prompt in ARMS:
                got = candidates if prompt is None else pick(q, candidates, prompt, render)
                t[name]["null_n"] += 1
                t[name]["null_ok"] += not got
        print("rep%d  " % (rep + 1) + "   ".join(
            "%-12s %2d" % (name, t[name]["answer"] - prev[name]["answer"])
            for name, _, _ in ARMS), flush=True)
        for name, _, _ in ARMS:
            prev[name] = t[name].copy()

    # One number, not per arm: selection ran once per question and every arm
    # read what it opened. Printed as a check -- it cannot differ between
    # arms, and if a future edit makes it differ the harness is broken.
    print("\nselect (shared by every arm) %d/%d = %d%%" % (
        sel["select"], sel["n"], 100 * sel["select"] // max(sel["n"], 1)))
    print("%-14s %8s %8s %8s" % ("arm", "answer", "nulls", "kept"))
    for name, _, _ in ARMS:
        c = t[name]
        n, nn = max(c["n"], 1), max(c["null_n"], 1)
        print("%-14s %7d%% %7d%% %8.1f" % (
            name, 100 * c["answer"] // n, 100 * c["null_ok"] // nn, c["kept"] / n))


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 3)
