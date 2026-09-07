"""The two vector arms. No model calls, so no interleaving and no reps.

    python cosine_v4.py [k]        # k = rows handed on, default 5

    cosine    librarian picks the pairs, vectors pick the rows inside them
    flat      no librarian at all; vectors rank every row in the archive

Separate from arms_v4.py on purpose. The three arms there each spend an LLM
call per question and drift with the server, which is why they interleave and
repeat. These two are arithmetic over a fixed matrix: same input, same output,
every time. Repeating them would buy nothing and averaging them with a
stochastic arm would hide which half the variance came from.

What this is here to settle. The pairs were built to narrow the search, and
batches 3-12 only ever compared one shelf to another -- never to its absence.
`flat` is that absence. If flat matches or beats cosine, the Librarian call
and the closed vocabulary are not paying for retrieval, whatever else they
pay for (Merged() addressing, the embedder-unavailable fallback, human
legibility -- all of which survive either way).

The corpus is built to make this hard rather than easy: near-miss clusters
mean six things expire, four touch Bodo, five name weekdays, so a flat sweep
that ranks on surface overlap alone gets punished exactly where a real
archive would punish it.

Cost note, since `rows` is a column everywhere else here: both arms are told
to hand on k rows, so they cost the same downstream by construction. The
comparison is purely whether the right row is among them. Fixing k is also
what makes them comparable to each other -- a threshold arm would trade the
two columns against each other again and answer a different question.
"""
import sys, collections
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import read_bench as rb
import retrieval_v4 as corpus
from answers_v4 import ANSWERS
from embed import Embedder
from arms_v4 import CACHE, answered, gold_index, shape


def answered_row(rows, question):
    """Strict: one row carries the whole key.

    `answered` joins the candidate set into a single blob, which is right for
    the LLM arms -- Intent sees every row at once and can compose across them.
    It is wrong as a retrieval measure: a two-token key can be satisfied by
    one token in the first row and the other in the fifth, and the chance of
    that rises with k, so the joint score flatters whichever arm returns more
    rows. Flat always returns k; cosine often returns fewer, having been
    handed a small pair. Both numbers are printed rather than one replacing
    the other, because they answer different questions: joint is what Intent
    would get away with, strict is whether the fact was actually found.
    """
    return any(any(all(t in rb.line(r).lower() for t in alt) for alt in ANSWERS[question])
               for r in rows)


def main(k=5):
    a, rows = rb.archive(CACHE)
    index = sorted(rows)
    gold = gold_index(a)
    print("archive: " + shape(rows))

    # One flat table of every row in the archive, and the pair each came from.
    # The pair is carried, not used to filter -- `flat` ignores it entirely and
    # `cosine` uses it only to mask. Both read the identical vectors, so any
    # gap between them is the narrowing and nothing else.
    flat_rows, owner = [], []
    for p in index:
        for r in rows[p]:
            flat_rows.append(r)
            owner.append(p)
    owner = np.array(owner)

    e = Embedder()
    print("embedding %d rows with %s ..." % (len(flat_rows), e.model_id), flush=True)
    matrix = e.encode([rb.embed_text(r) for r in flat_rows], kind="passage")

    qs = sorted(ANSWERS)
    turns = qs + list(corpus.NULLS)
    qv = e.encode(turns, kind="query")
    print("embedded. %d turns, %d dims\n" % (len(turns), matrix.shape[1]), flush=True)

    tally = collections.Counter()
    for turn, v in zip(turns, qv):
        is_null = turn not in ANSWERS
        scores = matrix @ v

        # flat: the whole archive is the candidate pool.
        top = np.argsort(-scores)[:k]
        # cosine: the Librarian's pairs first, then vectors inside them. The
        # selection call is the same one arms_v4 makes, so the two families
        # share a selection stage and differ only after it.
        opened = rb.select(turn, index)
        mask = np.isin(owner, list(opened)) if opened else np.zeros(len(owner), bool)
        pool = np.flatnonzero(mask)
        ctop = pool[np.argsort(-scores[pool])[:k]] if len(pool) else np.array([], int)

        for name, idx in (("flat", top), ("cosine", ctop)):
            got = [flat_rows[i] for i in idx]
            tally[name + ".rows"] += len(got)
            if is_null and len(idx):
                # A null turn states no fact, so there is nothing correct to
                # return. top-k always returns k rows, so a fixed-k vector arm
                # cannot score on the nulls column the LLM arms are graded on;
                # what it can report is how confident it was while being wrong.
                tally[name + ".null_top"] += float(scores[idx[0]])
                tally[name + ".null_n"] += 1
            elif not is_null:
                tally[name + ".answer"] += answered(got, turn)
                tally[name + ".strict"] += answered_row(got, turn)
                tally[name + ".top"] += float(scores[idx[0]]) if len(idx) else 0.0
        if not is_null:
            hit = bool(gold[turn] & set(opened))
            tally["select"] += hit
            # Cosine can only rank what the Librarian handed it, so its score
            # is two stages multiplied. Conditioning on the questions where
            # selection actually opened the right pair separates them: this is
            # how good the vector ranking is *inside* a correct selection, and
            # it is the only number here comparable to flat's.
            if hit:
                tally["cosine.answer_sel"] += answered(
                    [flat_rows[i] for i in ctop], turn)

    n, nn = len(qs), len(corpus.NULLS)
    print("questions %d   nulls %d   k %d" % (n, nn, k))
    print("  select %d/%d = %d%%   (librarian, shared with arms_v4)"
          % (tally["select"], n, 100 * tally["select"] // n))
    print("")
    print("  %-8s %8s %8s %8s %10s %10s"
          % ("arm", "joint", "strict", "rows", "top sim", "null sim"))
    for name in ("cosine", "flat"):
        print("  %-8s %7d%% %7d%% %8.1f %10.3f %10.3f" % (
            name,
            100 * tally[name + ".answer"] // n,
            100 * tally[name + ".strict"] // n,
            tally[name + ".rows"] / float(n + nn),
            tally[name + ".top"] / float(n),
            tally[name + ".null_top"] / float(max(tally[name + ".null_n"], 1))))
    print("")
    print("  cosine, counting only the %d questions where selection opened the"
          % tally["select"])
    print("  right pair: %d%%  -- the ranking inside a correct narrowing."
          % (100 * tally["cosine.answer_sel"] // max(tally["select"], 1)))
    print("")
    print("  joint    = of %d questions, key found across the k rows together" % n)
    print("  strict   = same, but one row had to carry the whole key")
    print("  null sim is over turns the arm returned anything for (cosine: %d of %d)."
          % (tally["cosine.null_n"], nn))
    print("  rows     = mean handed on; fixed at k by construction")
    print("  top sim  = mean best similarity on a real question")
    print("  null sim = same on a turn stating no fact. The gap between these")
    print("             two is what a threshold would have to live in.")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 5)
