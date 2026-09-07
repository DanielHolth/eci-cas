"""Does asking for the sentence damage the extraction it rides on?

The sentence column is meant to help the reader. This asks what it costs the
writer, and it runs before any read arm because a loss here would sit
underneath every number measured afterwards and look like a read result.

Two prompts, same corpus, same scorer: archivist.txt as it is now, against
the same file one commit earlier. The old arm is read out of git rather than
kept as a literal copy here -- a frozen double is exactly what bench.py's
header refuses, and the point of the comparison is the shipped file, not a
paraphrase of it.

What is scored is unchanged and deliberately blind to the new field:

  value    -- bench.sufficient, over subtopic/subject/key/value only. The
              sentence restates the fact in full words, so a scorer that saw
              it would report a jump that is nothing but the model's own
              paraphrase read back.
  rows     -- rows per statement. The failure this is really watching for is
              a model that spends its budget writing prose and returns fewer
              facts, or drops a field and loses the row to the parser.
  written  -- rows that actually carry a sentence, on the new arm. Optionality
              is a mitigation, and an unmeasured mitigation is a hope: if this
              is low the read arm is measuring almost nothing.
  null     -- fabrications on NULLS. More shape to fill is more invitation to
              fill it, and this file's own header records a model completing
              the form with no fact in it.

Judged on sign consistency across reps against the usual 10pp floor.

  python write_sentence_ab.py [reps]
"""
import collections, subprocess, sys
import bench

# The commit that added the field. Its parent is the last version of the
# prompt without it, which is the whole of the "old" arm.
SENTENCE_COMMIT = "33d8ba3"
PROMPT_PATH = "src/EciCas.Host/instructions/archivist.txt"


def prompt_at(rev):
    """The shipped prompt as of a revision, sectioned the way bench.load does."""
    raw = subprocess.run(["git", "show", "%s:%s" % (rev, PROMPT_PATH)],
                         cwd=bench.ROOT, capture_output=True, text=True,
                         encoding="utf-8", check=True).stdout
    out, cur = {}, "main"
    for ln in raw.splitlines(True):
        if ln.startswith("## "):
            cur = ln[3:].strip()
        elif not ln.startswith("#"):
            out[cur] = out.get(cur, "") + ln
    return out["main"].strip()


def run(reps=3):
    arms = [("old", prompt_at(SENTENCE_COMMIT + "~1")),
            ("new", bench.load("archivist.txt")["main"])]

    # A prompt that lost the field entirely would still score well on value
    # and quietly make the read arm meaningless, so this is checked once and
    # said out loud rather than inferred from the table.
    for name, p in arms:
        print("%-4s prompt asks for sentence: %s" % (name, "sentence=" in p))
    print(flush=True)

    per = {n: collections.Counter() for n, _ in arms}
    prev = {n: collections.Counter() for n, _ in arms}
    for rep in range(reps):
        for stmt, qs in bench.corpus.STATEMENTS:
            for name, prompt in arms:
                rows = bench.extract(stmt, prompt)
                t = per[name]
                t["stmts"] += 1
                t["rows"] += len(rows)
                t["sent"] += sum(bool(r.get("sentence")) for r in rows)
                for q in qs:
                    t["n"] += 1
                    t["value"] += bench.sufficient(rows, q)
        line = "rep%d  " % (rep + 1)
        for name, _ in arms:
            t, p = per[name], prev[name]
            line += "%-4s value %2d/%2d  rows %2d  sent %2d    " % (
                name, t["value"] - p["value"], t["n"] - p["n"],
                t["rows"] - p["rows"], t["sent"] - p["sent"])
            prev[name] = t.copy()
        print(line, flush=True)

    print(flush=True)
    for name, prompt in arms:
        t = per[name]
        n, r = max(t["n"], 1), max(t["rows"], 1)
        print("%-4s  value %d%%   rows/stmt %.2f   with sentence %d%%   fabricated on nulls %d/%d" % (
            name, 100 * t["value"] // n, t["rows"] / max(t["stmts"], 1),
            100 * t["sent"] // r, bench.fabrications(prompt), len(bench.corpus.NULLS)),
            flush=True)


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 3)
