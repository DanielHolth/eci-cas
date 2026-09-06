"""A/B on the one miss that is not noise: a person known through the job.

Across four reps of review_other.py, "My manager is called Petter Aas" was
filed under work in almost every rep and under almost every folder there --
work/career, work/history, work/employer, identity/name. That is not run to
run variance, it is a rule losing an argument. cataloger.txt already decides
the tie ("A person goes in relations. A fact about the job goes in work.")
and the model already read it. The rule is right and it is not winning.

The guess being tested: the two halves of that line are a distinction the
model has to apply, and the case it has to apply it to -- a person whose only
connection to the user is the job -- is the exact case the line does not name.
So B names it.

Category only. The topic call is downstream of the drawer and scoring it here
would fold two decisions into one number again.

Extraction runs once per rep and both arms file the same rows.

  python cat_tie.py [reps]
"""
import sys
import bench
from bench import CAT, VOCAB, extract
from review_other import pick, as_fact
from filing_key import KEY

# RESULT: B better or equal in 5 of 5 reps, better in 3, 88% -> 96%. B is now
# the shipped line, so the arms below are read the other way round: "shipped"
# is B, and A is reconstructed to keep the comparison re-runnable.
A = """- A person goes in relations. A fact about the job goes in work."""

B = """- A person goes in relations, including a person the user only knows
  through the job: a manager's or a colleague's name is relations, not work.
  What goes in work is the job itself -- the role, the duties, the hours,
  the workplace."""

CATS = {stmt: {p.split("/")[0] for p in pairs} for stmt, pairs in KEY.items()}


def category(fact, text, prompt):
    raw = pick(prompt.replace("{text}", text).replace("{fact}", fact))
    return next((c for c in VOCAB if c in raw), None)


def run(reps):
    base = CAT["category"]
    assert B in base, "shipped cataloger.txt no longer has the person/job tie line"
    arms = {"shipped": base, "unnamed": base.replace(B, A)}
    tally = {k: [] for k in arms}
    for rep in range(reps):
        rows_per = [(stmt, extract(stmt, PROMPT)) for stmt, _ in bench.corpus.DEV]
        for name, prompt in arms.items():
            ok = n = 0
            misses = []
            for stmt, rows in rows_per:
                for row in rows:
                    got = category(as_fact(row), stmt, prompt)
                    n += 1
                    if got in CATS[stmt]:
                        ok += 1
                    else:
                        misses.append(f"{str(got):12} {as_fact(row)}")
            tally[name].append((ok, n))
            print(f"rep{rep+1} {name:8} drawer {ok}/{n} = {ok/n:.0%}")
            for m in misses:
                print("        miss", m)
    print()
    for name, r in tally.items():
        print(f"{name:8} mean {sum(o/n for o, n in r)/len(r):.0%}")


if __name__ == "__main__":
    PROMPT = bench.load("archivist.txt")["main"]
    run(int(sys.argv[1]) if len(sys.argv) > 1 else 5)
