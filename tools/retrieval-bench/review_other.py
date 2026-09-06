"""A/B for the second pass over "other".

The pressure valve works both ways. "other" exists so a fact with no good
folder is not forced into a loose fit -- but a run of the shipped prompts put
Sofie's school start in learning/other while learning/school existed, and
Lars in relations/other while relations/sibling existed. The valve is being
reached for when a folder is right there.

Two variants, both costing one extra call per "other" row and nothing on any
other row:

  topic     ask for the topic again with "other" removed from the list. A
            model that cannot see the escape has to look at the folders; if
            it still cannot place the fact it answers "none" and code puts
            the row back in "other".

  category  ask for the CATEGORY again with the chosen drawer removed, then
            run the normal topic call inside whatever drawer comes back.
            The first version of this bench showed why: every "other" in a
            bad rep was a fact in the wrong drawer already -- a diet fact in
            household, a person in work -- and no folder inside a wrong
            drawer is right. "other" is not the model being lazy there, it
            is the model being honest about the call before it.

Extraction runs ONCE per statement per rep and both arms file the same rows.
Extraction on a 4B varies enough run to run to swamp a filing change, and
this comparison is about filing.

  python review_other.py [reps]
"""
import sys, re, collections
import bench
from bench import CAT, VOCAB, call, strip, norm, extract
from filing_key import KEY

REVIEW = """A fact, and the folders inside the "{cat}" drawer. Every folder that exists is
listed. Reply with one folder name from the list and nothing else.

{topics}

This fact was already looked at once and no folder was chosen. Read it again.
One of these is usually closer than nothing: pick the folder someone would
open months later to find this fact. Only if not one of them could hold it at
all, reply "none".

The message it was said in: {text}
The fact: {fact}"""


SCOPE = {}
for _l in CAT["category"].splitlines():
    _m = re.match(r"^(\w+) {2,}(.*)", _l)
    if _m and _m.group(1) in VOCAB:
        SCOPE[_m.group(1)] = _l.strip()
        _last = _m.group(1)
    elif _l.startswith("           ") and SCOPE:
        SCOPE[_last] += " " + _l.strip()


def scope(c):
    """The drawer's line from the shipped prompt, so the re-ask is not
    a bare name list -- the scope lines are what stopped body and admin
    trading appointments turn to turn."""
    return SCOPE.get(c, c)


def as_fact(row):
    return " ".join([row.get("subtopic", ""), row["subject"], row["key"], "=", row["value"]])


def pick(prompt):
    return norm(strip(call(prompt, 24)))


def file_pair(row, text, review):
    """The shipped two calls, plus a third only when the answer is "other".

    review is None (shipped), "topic", or "category".
    """
    fact = as_fact(row)
    raw = pick(CAT["category"].replace("{text}", text).replace("{fact}", fact))
    cat = next((c for c in VOCAB if c in raw), None)
    if cat is None:
        return "unfiled/unfiled"
    topics = VOCAB[cat]
    raw = pick(CAT["topic"].replace("{cat}", cat).replace("{topics}", "  ".join(topics))
               .replace("{text}", text).replace("{fact}", fact))
    topic = next((t for t in raw.split() if t in topics), "other")

    if topic != "other" or review is None:
        return f"{cat}/{topic}"

    if review == "topic":
        real = [t for t in topics if t != "other"]
        raw = pick(REVIEW.replace("{cat}", cat).replace("{topics}", "  ".join(real))
                   .replace("{text}", text).replace("{fact}", fact))
        # "none", silence, or a folder that is not on the list all mean the
        # reviewer declined -- and declining is what keeps "other" a valve.
        return f"{cat}/{next((t for t in raw.split() if t in real), 'other')}"

    # "category": the drawer that produced "other" is the one under suspicion,
    # so it is the option taken away.
    others = [c for c in VOCAB if c != cat]
    raw = pick(RECAT.replace("{drawers}", chr(10).join(scope(c) for c in others))
               .replace("{rejected}", cat).replace("{text}", text).replace("{fact}", fact))
    cat2 = next((c for c in others if c in raw), None)
    if cat2 is None:
        return f"{cat}/other"
    raw = pick(CAT["topic"].replace("{cat}", cat2).replace("{topics}", "  ".join(VOCAB[cat2]))
               .replace("{text}", text).replace("{fact}", fact))
    topic2 = next((t for t in raw.split() if t in VOCAB[cat2]), "other")
    # A second "other" is the answer standing: leave the fact where the
    # shipped path put it rather than moving it on a guess.
    return f"{cat}/other" if topic2 == "other" else f"{cat2}/{topic2}"


ARMS = (None, "topic", "category")


RECAT = """Ten drawers of a filing cabinet, minus the "{rejected}" drawer: nothing in
there fits this fact, which usually means the drawer itself was the wrong
one. Reply with one drawer name and nothing else.

{drawers}

The message it was said in: {text}
The fact: {fact}"""


def run(reps):
    tally = {a: [] for a in ARMS}
    for rep in range(reps):
        rows_per = [(stmt, extract(stmt, PROMPT)) for stmt, _ in bench.corpus.DEV]
        for review in ARMS:                   # interleaved, same rows every arm
            ok = n = oth = 0
            misses = []
            for stmt, rows in rows_per:
                for row in rows:
                    pair = file_pair(row, stmt, review)
                    n += 1
                    oth += pair.endswith("/other")
                    if pair in KEY[stmt]:
                        ok += 1
                    else:
                        misses.append(f"{pair:24} {as_fact(row)}")
            tally[review].append((ok, n, oth))
            arm = review or "shipped"
            print(f"rep{rep+1} {arm:8} filed {ok}/{n} = {ok/n:.0%}   other={oth}")
            for m in misses:
                print("        miss", m)
    print()
    for review in ARMS:
        r = tally[review]
        print(f"{review or 'shipped':8} mean {sum(o/n for o,n,_ in r)/len(r):.0%}"
              f"   other/rep {sum(o for _,_,o in r)/len(r):.1f}")


if __name__ == "__main__":
    PROMPT = bench.load("archivist.txt")["main"]
    run(int(sys.argv[1]) if len(sys.argv) > 1 else 3)
