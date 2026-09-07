"""Does a fact about another person get that person as its subject?

The one thing the sufficiency score cannot see. `subject=brother key=location
value=Tromso` contains every word the answer needs, so it passes on value --
and it is still the wrong address, because Librarian looks for Lars and there
is no Lars in it. This counts subjects directly on the two sentences where a
named person is the target.

Result 2026-09-07, 10 extractions per arm:

    My brother Lars lives in Tromso
      bare       subject=lars   0/10   {brother:4, user:5, user brother:1}
      property   subject=lars  10/11   {lars:10, user:1}
    My daughter Sofie starts school in August
      bare       subject=sofie  3/9    {user:4, sofie:2, daughter sofie:1, ...}
      property   subject=sofie 10/10   {sofie:10}

The bare arm's failure is not picking the wrong person, it is declining to
have one: subject=user on half the runs, and once "son or daughter". That row
is unretrievable and wrong if retrieved, which is the pair of properties that
made it worth a prompt rule.

Narrow on purpose -- two sentences, no corpus. It answers "did the rule take"
and nothing else.

  python subject_probe.py [runs]
"""
import collections, sys
import bench, variant_b

CASES = [("My brother Lars lives in Tromso", "lars"),
         ("My daughter Sofie starts school in August", "sofie")]


def run(n):
    shipped = bench.load("archivist.txt")["main"]
    arms = (("bare", variant_b.apply(shipped)), ("property", shipped))
    for stmt, want in CASES:
        print(stmt)
        for name, prompt in arms:
            subs, hit, tot = collections.Counter(), 0, 0
            for _ in range(n):
                for row in bench.extract(stmt, prompt):
                    subs[row["subject"].lower()] += 1
                    tot += 1
                    hit += want in row["subject"].lower()
            print(f"  {name:9} subject={want} on {hit}/{tot} rows "
                  f"({hit/tot:.0%})  {dict(subs)}")
        print()


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 else 10)
