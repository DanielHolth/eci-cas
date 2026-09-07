"""Does the writer file better when it is shown the reader's dictionary?

The gloss shipped for the selector only. That leaves the two halves of the
system holding different dictionaries: Librarian sees "renewal (expires,
expiry, renew, runs out)", Cataloger sees "renewal". This asks whether the
same words help the side that decides where the fact goes.

It runs before consolidation on purpose. Merging topics is known to cost the
writer -- lean files 29/35 against full's 30/35 -- and the suspected cause is
exactly this, abstract merged names handed to the writer bare. Measuring the
gloss on the 170-pair shelf first means the merged shelf has a baseline to be
read against instead of two variables landing together.

Method, as README.md requires: extraction runs ONCE per rep and both arms
file the same rows, and the category call is made ONCE per row and shared,
so the only thing that differs between arms is the topic call. `category`
is therefore identical by construction and is printed as a check, not a
result -- if it ever splits, the harness is broken. Judged on sign consistency across reps against a 10pp
floor, not on the mean.

Scored against filing_key.KEY, the deliberately generous set of defensible
drawers: what this catches is gross misfiling and reaching for "other" when a
real folder exists, not near-misses.

  python write_gloss_ab.py [reps]
"""
import collections, sys
import bench
from bench import CAT, VOCAB, extract, norm, strip, call
from filing_key import KEY
from topic_gloss import GLOSS

ARMS = [("plain", False), ("gloss", True)]


def as_fact(row):
    return " ".join([row.get("subtopic", ""), row["subject"], row["key"],
                     "=", row["value"]])


def file_category(fact, text):
    """The drawer call, made ONCE per row and shared by both arms.

    The gloss describes topics and leaves this prompt byte-identical, so
    calling it per arm would only sample it twice: the smoke run drifted 81%
    to 100% here on a prompt that never changed. Freezing the upstream stage
    is what the arena does for the same reason.
    """
    raw = norm(strip(call(CAT["category"].replace("{text}", text)
                          .replace("{fact}", fact), 24)))
    return next((c for c in VOCAB if c in raw), None)


def file_topic(cat, fact, text, use_gloss):
    """bench.file_fact's second call, with the folder list optionally glossed.

    Kept here rather than pushed into bench.py: this is an arm, not a
    feature, and bench.file_fact is what every other script measures against.
    """
    if use_gloss:
        g = GLOSS.get(cat, {})
        topics = "\n".join(
            t + (" (" + g[t] + ")" if t in g else "") for t in VOCAB[cat])
    else:
        topics = "  ".join(VOCAB[cat])

    raw = norm(strip(call(CAT["topic"].replace("{cat}", cat)
                          .replace("{topics}", topics)
                          .replace("{text}", text).replace("{fact}", fact), 24)))
    return next((t for t in raw.split() if t in VOCAB[cat]), "other")


def run(reps=3):
    prompt = bench.load("archivist.txt")["main"]
    stmts = [s for s, _ in bench.corpus.DEV if s in KEY]
    per = {name: collections.Counter() for name, _ in ARMS}
    prev = {name: collections.Counter() for name, _ in ARMS}

    for rep in range(reps):
        for stmt in stmts:
            rows = extract(stmt, prompt)          # once, shared by both arms
            for row in rows:
                fact = as_fact(row)
                cat = file_category(fact, stmt)   # once, shared by both arms
                for name, g in ARMS:
                    pair = ("unfiled/unfiled" if cat is None
                            else cat + "/" + file_topic(cat, fact, stmt, g))
                    t = per[name]
                    t["n"] += 1
                    t["pair"] += pair in KEY[stmt]
                    t["cat"] += pair.split("/")[0] in {p.split("/")[0]
                                                       for p in KEY[stmt]}
                    t["other"] += pair.endswith("/other")
        line = "rep%d  " % (rep + 1)
        for name, _ in ARMS:
            t, p = per[name], prev[name]
            line += "%-6s pair %2d cat %2d other %2d (of %2d)   " % (
                name, t["pair"] - p["pair"], t["cat"] - p["cat"],
                t["other"] - p["other"], t["n"] - p["n"])
            prev[name] = t.copy()
        print(line, flush=True)

    print(flush=True)
    for name, _ in ARMS:
        t = per[name]
        n = max(t["n"], 1)
        print("%-6s  pair %d%%   category %d%%   other %d%%   (%d rows)" % (
            name, 100 * t["pair"] // n, 100 * t["cat"] // n,
            100 * t["other"] // n, n), flush=True)


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 3)
