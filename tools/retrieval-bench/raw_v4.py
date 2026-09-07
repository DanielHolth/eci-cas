"""What a row should store: keys, sentence, or the message it came from.

    python raw_v4.py [k]

Daniel's idea: a write that stores its value both as keywords and as the
original text might make more writes correct and more reads correct at once,
and might carry two facts in one row instead of needing the extractor to split
them.

Two separate claims, and they need separate measurements.

**The ceiling.** Whether the fact survived the write at all. `answered` in
arms_v4 joins `line(r)` -- category/topic/subject/key/value, the address --
and asks whether the answer key is in it. The sentence field is not consulted,
though every row has one and the embedder reads it. So `writable 77/87` is a
statement about the address line, not about the archive, and every arm in
every batch has been scored against a ceiling that ignores half of what is
stored. This file recomputes it three ways. No model, no embedder, no k.

**The retrieval.** Whether the extra text helps or hurts finding the row. More
text per row is not free: it dilutes the vector and can pull distractors up.
So the same three storage choices are run through flat cosine.

Result:

    stored              ceiling   strict@5    chars
    line                    91%        77%       48
    line+sent               97%        87%      103
    line+sent+stmt         100%        88%      106

The idea is right and the reason is not the one it was proposed for. Crediting
the sentence -- already written, already stored, already embedded -- moves the
ceiling 91 -> 97 and strict 77 -> 87. That is not a new write format. It is
the scorer catching up with what the archive has held all along, and it means
every write-side loss reported in batches 3-13 was partly a measurement
artifact. In particular the claim that the Archivist drops proper nouns from
two-fact statements is withdrawn: the proper noun is in the sentence, and
`line()` was the thing that dropped it.

Keeping the original message on top buys the last 3pp of ceiling, to 100%, and
1pp of strict. So storing the raw text does make every write recoverable and
does let one row carry two facts -- but the sentence already gets most of the
way there for free, and the message is the expensive half of the idea.

One confound, stated rather than corrected. `stmt` is the original user
message and only gold rows have one -- padding is generated as fields plus a
sentence and never had a message. So the stmt arm gives the rows holding
answers a longer, more conversational text than the distractors get, and
questions are conversational. That flatters it. The ceiling column is immune
(it is keyword containment, not similarity), so read the ceiling as the
result and the retrieval column as an upper bound with a thumb on the scale.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, gold_index, shape

# name -> what one row's stored text is.
STORES = [
    ("line",        lambda r: rb.line(r)),
    ("line+sent",   lambda r: rb.line(r) + ". " + (r.get("sentence") or "")),
    ("line+sent+stmt", lambda r: rb.line(r) + ". " + (r.get("sentence") or "")
                                + " " + (r.get("stmt") or "")),
]


def hit(text, question):
    t = text.lower()
    return any(all(tok in t for tok in alt) for alt in ANSWERS[question])


def main(k=5):
    a, rows = rb.archive(CACHE)
    gold = gold_index(a)
    print("archive: " + shape(rows))

    flat_rows = [r for p in sorted(rows) for r in rows[p]]
    qs = sorted(ANSWERS)
    e = Embedder()
    qv = e.encode(qs, kind="query")
    print("embedding %d rows x %d stores with %s ...\n"
          % (len(flat_rows), len(STORES), e.model_id), flush=True)

    print("  %-16s %10s %10s %8s" % ("stored", "ceiling", "strict@%d" % k, "chars"))
    for name, f in STORES:
        # Ceiling: of the rows the write pass actually produced for this
        # question's statement, does any one of them contain the whole key?
        # This is arms_v4's `writable` with the stored text swapped in.
        ceiling = sum(any(hit(f(r), q) for p in gold[q] for r in rows[p] if r.get("stmt"))
                      for q in qs)
        texts = [f(r) for r in flat_rows]
        M = e.encode(texts, kind="passage")
        strict = 0
        for q, v in zip(qs, qv):
            top = np.argsort(-(M @ v))[:k]
            strict += any(hit(f(flat_rows[i]), q) for i in top)
        n = len(qs)
        print("  %-16s %9d%% %9d%% %8.0f" % (
            name, 100 * ceiling // n, 100 * strict // n,
            sum(len(t) for t in texts) / float(len(texts))))

    print("\n  ceiling  = share of %d questions whose fact survived the write," % len(qs))
    print("             given this storage choice. No retrieval involved.")
    print("  strict   = one of the top %d rows carries the whole key." % k)
    print("  chars    = mean stored text per row, the cost side.")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
