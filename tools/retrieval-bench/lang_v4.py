"""Which embedder, when the archive is not in English. No LLM, no archive build.

    python lang_v4.py

The shipped archivist.txt writes the sentence in the language of the message,
so a Norwegian household produces a Norwegian archive. Every number measured
so far is on an English corpus read by English questions, which cannot see
this at all: bge-small-en-v1.5 beats multilingual-e5-small by 4-6pp there, and
that comparison is uninformative about the case the product actually has.

The test does not need a new corpus or a write pass. Twenty v4 statements are
restated in Norwegian by hand and dropped into the real 1559-row English
archive as extra rows. The questions stay in English. If the fact is findable,
the Norwegian row must outrank 1559 English distractors -- including its own
near-miss cluster, since v4 was built so that six things expire and five name
weekdays.

That is the cross-lingual case in its harshest honest form and the one the
product hits first: a Norwegian speaker whose ECI has been told things in
Norwegian, asking in either language. A model with no shared multilingual
space cannot do it at any k.

Result, and it is not close:

    embedder            top1   top5   top20   mean rank (of 1579)
    bge-small-en          0%    15%     15%       256.1
    multilingual-e5      30%    90%     95%         4.5

bge does not have a shared multilingual space, so a Norwegian row is not near
its English question at any k -- 256th of 1579 is noise. This reverses the
English-corpus comparison (bge 92/81/92 against e5 88/75/88) and settles the
ship model: multilingual-e5-small, paying 4-6pp on English to not lose the
archive outright the first time someone speaks Norwegian to it.

Read top5 rather than top1. The archive holds English near-misses on these
same topics by construction, so an English row outranking the Norwegian one
is often a correct answer rather than a miss; top1 undercounts e5 and cannot
flatter bge, which is 256 ranks away from either.

Hand-written rather than machine-translated on purpose. A translation model in
the loop would put its own vocabulary between the statement and the embedder,
and a bad translation would read as a bad embedder.
"""
import sys
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from arms_v4 import CACHE

# (English question, Norwegian sentence as the archive would hold it).
# Drawn from v4 statements that sit inside a near-miss cluster where possible,
# so the Norwegian row has English competition on the same topic rather than
# winning by being the only row about anything similar.
PAIRS = [
    ("When does my passport expire?",        "Passet mitt går ut i mars 2027."),
    ("When is Ingrid's birthday?",           "Datteren min Ingrid fyller sju i november."),
    ("Where do I work?",                     "Jeg har jobbet hos Statkraft i elleve år."),
    ("What is our address?",                 "Adressen vår er Bjerkeveien 14."),
    ("What car do we drive?",                "Vi kjører en sølvfarget Skoda Octavia."),
    ("Which day do I work from home?",       "Jeg jobber hjemmefra på fredager."),
    ("When do the winter tyres go on?",      "Vinterdekkene settes på i november."),
    ("Where did I study?",                   "Jeg tok mastergraden min i marinbiologi ved UiT."),
    ("What is my daughter allergic to?",     "Ingrid er allergisk mot hasselnøtter."),
    ("Who feeds the cat when we travel?",    "Astrid ved siden av mater katten når vi reiser."),
    ("When is payday?",                      "Lønningsdagen er den femtende."),
    ("Who is my manager?",                   "Sjefen min heter Henrik."),
    ("Where is our cabin?",                  "Vi har en hytte på Senja, en time fra Finnsnes."),
    ("What bus do I take?",                  "Jeg tar bussen klokka 07:20 inn til byen."),
    ("Who did the bathroom?",                "En rørlegger som het Rune gjorde badet."),
    ("What is the mortgage with?",           "Boliglånet er hos Sparebank 1."),
    ("How many days of leave do I have?",    "Jeg har tolv feriedager igjen i år."),
    ("What do I read?",                      "Jeg leser krimbøker, mest Nesbø."),
    ("When is the Monday stand-up?",         "Mandagsmøtet er klokka halv ni."),
    ("Where does Marit live?",               "Marit bor i Bodø."),
]

MODELS = [("bge-small-en-v1.5", "bge-small-en"),
          ("multilingual-e5-small", "multilingual-e5"),
          ("embeddinggemma-300m", "embeddinggemma")]


def main():
    from embed import Embedder
    _, rows = rb.archive(CACHE)
    # Sentence only, on both sides. Elsewhere rows embed as line+sentence, but
    # line() is the category/topic/keys header and those are English for every
    # row including the Norwegian ones -- the Cataloger files into a fixed
    # English vocabulary whatever language the message was in. Including it
    # would hand the English distractors a header the Norwegian targets also
    # have, measuring the shelf rather than the sentence. Bare sentences are
    # the harshest and cleanest form of the question being asked here.
    distractors = [(r.get("sentence") or rb.line(r)) for p in sorted(rows) for r in rows[p]]
    targets = [no for _, no in PAIRS]
    qs = [en for en, _ in PAIRS]
    print("%d Norwegian rows hidden in %d English rows, %d English questions\n"
          % (len(targets), len(distractors), len(qs)))

    print("  %-18s %8s %8s %8s %10s" % ("embedder", "top1", "top5", "top20", "mean rank"))
    for path, label in MODELS:
        e = Embedder(ROOT + "models/embedding/" + path)
        M = e.encode(targets + distractors, kind="passage")
        qv = e.encode(qs, kind="query")
        ranks = []
        for i, v in enumerate(qv):
            sc = M @ v
            # Rank of this question's own Norwegian row among everything.
            ranks.append(int((sc > sc[i]).sum()) + 1)
        ranks = np.array(ranks)
        print("  %-18s %7d%% %7d%% %7d%% %10.1f" % (
            label,
            100 * int((ranks <= 1).sum()) // len(ranks),
            100 * int((ranks <= 5).sum()) // len(ranks),
            100 * int((ranks <= 20).sum()) // len(ranks),
            ranks.mean()))

    print("\n  rank = position of the Norwegian row holding the answer, among")
    print("         all %d rows. 1 is perfect." % (len(targets) + len(distractors)))


if __name__ == "__main__":
    main()
