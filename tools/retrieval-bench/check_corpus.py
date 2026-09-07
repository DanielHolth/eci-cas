"""Structural checks on a corpus and its answer key. Run before generating.

None of these need a model, and all of them catch a class of error that
otherwise reports as a result. That is the point: a corpus is an instrument,
and an instrument that measures the wrong thing produces numbers with no
warning attached.

Four checks:

  keyed      every question has an answer key entry and vice versa. A
             question the key does not know raises KeyError at scoring time
             (deliberately -- see bench.sufficient), which stops a run late
             rather than never.
  unique     no duplicate statements or questions. A duplicate question is
             scored twice and silently doubles that fact's weight.
  leak       no answer key alternative matches a statement other than its
             own. This is the one v4 exists to survive: the corpus is built
             out of near-miss clusters on purpose, so a key accepting a
             shared token would score a wrong row as a hit and the flat
             arm's discrimination would be untestable. Caught fourteen on
             v4's first draft, of which five were real cluster overlaps and
             nine were substring accidents ("red" inside "scared", "died"
             inside "studied", "run" inside "Rune").
  oblique    every OBLIQUE entry names a statement that exists. The v3
             version of this list hand-guessed pairs and scored 0/8 for that
             reason alone.

Substring matching is what the scorer does, so it is what this checks. A
token that only leaks as a substring is still a leak: the scorer cannot tell
the difference, and neither can the arm reading its output.
"""
import sys

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]


def check(corpus, answers):
    own = {q: s for s, ql in corpus.STATEMENTS for q in ql}
    qs = [q for _, ql in corpus.STATEMENTS for q in ql]
    stmts = [s for s, _ in corpus.STATEMENTS]
    bad = 0

    for label, dupes in (("statement", len(stmts) - len(set(stmts))),
                         ("question", len(qs) - len(set(qs)))):
        if dupes:
            print("DUPLICATE %s x%d" % (label, dupes))
            bad += dupes

    for q in sorted(set(qs) - set(answers)):
        print("UNKEYED   %s" % q)
        bad += 1
    for q in sorted(set(answers) - set(qs)):
        print("ORPHAN    %s" % q)
        bad += 1

    for q, alts in answers.items():
        if q not in own:
            continue
        for other in stmts:
            if other == own[q]:
                continue
            for alt in alts:
                if all(t in other.lower() for t in alt):
                    print("LEAK      %-44s %-26s -> %s" % (q[:44], str(alt), other))
                    bad += 1

    for entry in getattr(corpus, "OBLIQUE", []):
        if entry[1] not in stmts:
            print("NO STMT   %s" % entry[0])
            bad += 1

    nulls = len(getattr(corpus, "NULLS", []))
    print("\nstatements %d  questions %d  nulls %d (%.0f%% of questions)  oblique %d"
          % (len(stmts), len(qs), nulls, 100.0 * nulls / max(len(qs), 1),
             len(getattr(corpus, "OBLIQUE", []))))
    print("problems %d" % bad)
    return bad


if __name__ == "__main__":
    v = sys.argv[1] if len(sys.argv) > 1 else "v4"
    corpus = __import__("retrieval_" + v)
    answers = __import__("answers_" + v).ANSWERS
    sys.exit(1 if check(corpus, answers) else 0)
