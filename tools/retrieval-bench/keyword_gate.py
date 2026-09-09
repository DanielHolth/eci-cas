# The first Characterise gate -- deterministic keyword extraction, scored.
#
# Pre-registered in docs/roadmap.md ("Characterise -- pre-registered"). The
# lexical half of Find, the whole of Characterise, and the consolidator's
# disagreement test all stand on one extractor that uses no model. If the
# tokens an answer needs do not survive it, none of that is worth a corpus.
#
# No server, no model, no cache. Runs in a second and is deterministic, so
# there is no interleaving and no noise floor to argue about: the arms differ
# by construction, not by sampling.
#
# Two axes, reported together and never collapsed:
#
#   keep   fraction of v4's direct questions whose answer tokens all survive
#          extraction of the statement they came from. The ceiling of the
#          lexical arm -- a token dropped here is unrecoverable downstream.
#   yield  mean keywords per statement. The cost side. Keeping everything
#          scores keep=100% and is useless: a keyword set the size of the
#          sentence discriminates nothing and flattens the contrast ranking
#          that Characterise ranks by. An arm is only interesting if it holds
#          keep high while yield falls.
#
# Nulls are scored too. A null is a sentence stating no fact, so keywords out
# of one are rows of background entering the corpus term counts -- which is
# exactly what the contrast ranking has to see through.

import re, sys, math
from collections import Counter

sys.path.insert(0, __import__("os").path.join(
    __import__("os").path.dirname(__file__), "..", ".."))

from tests.corpora.retrieval_v4 import STATEMENTS, NULLS, OBLIQUE
from answers_v4 import ANSWERS

# Closed-class English, plus the conversational filler a spoken archive is
# made of. Hand-written rather than imported: the dependency is not worth it
# and a list nobody can read is a list nobody can correct.
STOP = set("""
a an the this that these those there here it its it's
i me my mine myself we us our ours you your yours he him his she her hers
they them their theirs who whom whose which what where when why how
is am are was were be been being do does did doing done have has had having
will would shall should can could may might must
and or but if then else so because as than of at by for with about against
between into through during before after above below to from up down in out
on off over under again further once not no nor only own same too very just
s t don't now
one two get got go going went make made take took come came say said
thing things something anything nothing lot lots bit
""".split())

WORD = re.compile(r"[A-Za-z0-9'À-ſ]+")


def tokens(text):
    """Surface tokens with their original casing and position."""
    return [(m.group(0), m.start()) for m in WORD.finditer(text)]


def _norm(w):
    return w.lower().strip("'")


def document_frequency(corpus):
    df = Counter()
    for text in corpus:
        df.update({_norm(w) for w, _ in tokens(text)})
    return df


def extract(text, df, arm, rare_max=2):
    """Deterministic keyword extraction. Returns lowercased keywords.

    Arms differ only in which of the three admission rules are on:
      cap    token capitalised and not sentence-initial (names, places)
      num    token contains a digit (dates, amounts, numbers)
      rare   token's document frequency in the corpus is at or below rare_max
    """
    out = []
    for w, pos in tokens(text):
        n = _norm(w)
        if not n or n in STOP:
            continue
        capitalised = w[0].isupper() and pos > 0
        numeric = any(c.isdigit() for c in w)
        rare = df.get(n, 0) <= rare_max
        keep = ("cap" in arm and capitalised) or \
               ("num" in arm and numeric) or \
               ("rare" in arm and rare) or \
               ("all" in arm)
        if keep:
            out.append(n)
    return out


def satisfied(alternatives, keywords):
    """One alternative is a tuple of tokens that must ALL be present.

    Substring containment, matching answers_v4's own contract: the key holds
    stems ("promot", "bin") so a keyword contains the key token rather than
    equalling it.
    """
    blob = " ".join(keywords)
    return any(all(tok in blob for tok in alt) for alt in alternatives)


ARMS = [
    ("all",            "every non-stopword -- the useless ceiling"),
    ("cap+num",        "the rule as written in the roadmap, minus rarity"),
    ("cap+num+rare2",  "as written, rare = df <= 2"),
    ("cap+num+rare1",  "as written, rare = df <= 1 (hapax only)"),
    ("rare2",          "rarity alone, no casing signal"),
]


def arm_flags(name):
    f = set()
    if name == "all":
        return {"all"}
    if "cap" in name:
        f.add("cap")
    if "num" in name:
        f.add("num")
    if "rare" in name:
        f.add("rare")
    return f


def rare_max_of(name):
    m = re.search(r"rare(\d)", name)
    return int(m.group(1)) if m else 0


def main():
    corpus = [s for s, _ in STATEMENTS] + list(NULLS)
    df = document_frequency(corpus)

    print(f"corpus: {len(STATEMENTS)} statements, {len(NULLS)} nulls, "
          f"{len(df)} distinct tokens")
    questions = [(s, q) for s, qs in STATEMENTS for q in qs if q in ANSWERS]
    missing = [q for s, qs in STATEMENTS for q in qs if q not in ANSWERS]
    if missing:
        print(f"warning: {len(missing)} questions absent from the key: {missing[:3]}")
    print(f"scored:  {len(questions)} direct questions, "
          f"{len(OBLIQUE)} oblique\n")

    head = f"{'arm':16} {'keep':>6} {'yield':>7} {'oblique':>8} {'nullyield':>10}"
    print(head)
    print("-" * len(head))

    detail = {}
    for name, _ in ARMS:
        flags, rmax = arm_flags(name), rare_max_of(name)
        kws = {s: extract(s, df, flags, rmax) for s in corpus}

        hits = [(s, q) for s, q in questions if satisfied(ANSWERS[q], kws[s])]
        obl = [t for q, s, alt in OBLIQUE
               if satisfied(alt, kws.get(s, extract(s, df, flags, rmax)))
               for t in (1,)]

        keep = 100.0 * len(hits) / len(questions)
        yld = sum(len(kws[s]) for s, _ in STATEMENTS) / len(STATEMENTS)
        nyld = sum(len(kws[s]) for s in NULLS) / len(NULLS)
        oblq = 100.0 * len(obl) / len(OBLIQUE)

        print(f"{name:16} {keep:5.1f}% {yld:7.2f} {oblq:7.1f}% {nyld:10.2f}")
        detail[name] = (kws, {q for _, q in hits})

    # What each arm loses, which is the only part worth reading twice.
    base = detail["all"][1]
    print()
    for name, _ in ARMS:
        if name == "all":
            continue
        lost = sorted(base - detail[name][1])
        print(f"{name}: {len(lost)} lost")
        for q in lost[:8]:
            s = next(s for s, qs in STATEMENTS if q in qs)
            print(f"    {q}")
            print(f"      key  {ANSWERS[q]}")
            print(f"      kept {detail[name][0][s]}")
        print()


if __name__ == "__main__":
    main()
