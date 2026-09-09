"""Two reads, and where diversity is worth paying for.

One ranked list has to answer two questions at once -- what is true now, and
what changed -- and it answers neither well (batch 24). This splits them:

  A  superseded_by IS NULL. What is true. The read that feeds an answer.
  B  unrestricted. What changed. The read that feeds `change` and hindsight.

and then asks whether deliberate diversity adds anything on top of the
thread-collapse that is already there:

  mmr    penalise a candidate by its similarity to what is already selected.
  noise  jitter the scores. Included because it was proposed, and because a
         thing that only degrades rank should be shown degrading rank rather
         than argued about.

superseded_by is modelled perfectly here -- a row is current iff it carries
its thread's latest value. That is the consolidator being right every time,
so read A's numbers are a ceiling, and the gap to it is the consolidator's
error, not the read rule's.

`eras` counts distinct (thread, value) pairs in the five: for read B it is the
measure that matters, because covering one subject twice at two different
values is the point rather than the failure.
"""
import argparse, random, statistics as st
from collections import defaultdict
import numpy as np

import build_v5
from thread_sweep import thread
from read_dedup import queries, slots, TOPK

THRESH = 0.86


def select(order, sims, key_of, mode, rng, lam=0.7, sigma=0.02, V=None):
    if mode == "plain":
        return slots(order, key_of)
    if mode == "noise":
        jitter = {i: sims[i] + rng.gauss(0, sigma) for i in order}
        return slots(sorted(order, key=lambda i: -jitter[i]), key_of)
    # mmr
    out, used, pool = [], set(), list(order)
    while pool and len(out) < TOPK:
        best, best_s = None, -9
        for i in pool[:120]:
            k = key_of(i)
            if k in used:
                continue
            red = max((float(V[i] @ V[j]) for j in out), default=0.0)
            s = lam * sims[i] - (1 - lam) * red
            if s > best_s:
                best, best_s = i, s
        if best is None:
            break
        used.add(key_of(best))
        out.append(best)
        pool.remove(best)
    return out


def score(rows, picks, tkey, current):
    threads = {rows[i]["thread"] for i in picks}
    eras = {(rows[i]["thread"], rows[i]["value"]) for i in picks}
    vals = [rows[i]["value"] for i in picks if rows[i]["thread"] == tkey]
    has = current in vals
    return len(threads), len(eras), has, (bool(vals) and not has)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=8000)
    ap.add_argument("--seeds", type=int, default=3)
    a = ap.parse_args()

    from embed import Embedder
    emb = Embedder()
    rng = random.Random(11)
    agg = defaultdict(list)

    for seed in range(a.seeds):
        r = random.Random(20260909 + seed * 7919)
        rows = build_v5.build(r, a.rows)
        texts = [x["text"] for x in rows]
        V = np.vstack([emb.encode(texts[i:i + 256])
                       for i in range(0, len(texts), 256)]).astype("float32")
        qs = queries(rows)
        Q = emb.encode([s for _, s, _ in qs]).astype("float32")
        lab = thread(V, THRESH, "first", set())[0]
        latest = {}
        for x in rows:
            k = x["thread"]
            if k not in latest or x["date"] > latest[k]["date"]:
                latest[k] = x
        live = np.array([rows[i]["value"] == latest[rows[i]["thread"]]["value"]
                         for i in range(len(rows))])

        for qi, (tkey, _s, current) in enumerate(qs):
            sims = V @ Q[qi]
            full = np.argsort(-sims)[:400]
            cur = [i for i in full if live[i]]
            for name, order, key, mode in [
                    ("B flat", full, lambda i: i, "plain"),
                    ("B collapse", full, lambda i: lab[i], "plain"),
                    ("A flat", cur, lambda i: i, "plain"),
                    ("A collapse", cur, lambda i: lab[i], "plain"),
                    ("A +mmr", cur, lambda i: lab[i], "mmr"),
                    ("A +noise", cur, lambda i: lab[i], "noise")]:
                agg[name].append(
                    score(rows, select(list(order), sims, key, mode, rng, V=V),
                          tkey, current))
        print("seed %d done" % seed, flush=True)

    head = "%-12s %9s %6s %9s %7s" % ("arm", "distinct", "eras", "current",
                                      "stale")
    print("\n%d rows, %d seeds, collapse at %.2f\n%s\n%s"
          % (a.rows, a.seeds, THRESH, head, "-" * len(head)))
    for name in ["B flat", "B collapse", "A flat", "A collapse", "A +mmr",
                 "A +noise"]:
        rs = agg[name]
        print("%-12s %9.2f %6.2f %9.3f %7.3f"
              % (name, st.mean(x[0] for x in rs), st.mean(x[1] for x in rs),
                 st.mean(x[2] for x in rs), st.mean(x[3] for x in rs)))


if __name__ == "__main__":
    main()
