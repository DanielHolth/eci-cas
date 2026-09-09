"""Five slots, and what they get spent on.

An append-only archive says the same thing many times, so a flat top-k spends
its slots on the loudest fact rather than the k most useful ones. The roadmap
claims threading makes "repetition cost one slot"; this measures it, and it
measures the price of being wrong about that, because collapsing by thread
means a false merge no longer only breaks *now* -- it hides the second fact
behind the first at read time.

Arms:
  flat      top-5 by cosine, as today.
  collapse  walk the ranked list and take a row only if its predicted thread
            has not already taken a slot. Threading at threshold T supplies
            the key.
  oracle    collapse by the *true* thread -- the ceiling, and the number that
            says how much of the gap is the threshold's fault.

Measures, per query:
  distinct  true threads covered by the five rows (5 is perfect, 1 is the
            failure this exists to find).
  current   a row carrying the value that is current *now* is in the five.
  stale     a superseded value is in the five and the current one is not --
            worse than missing, because it answers confidently and wrongly.
"""
import argparse, random, statistics as st
from collections import defaultdict
import numpy as np

import build_v5
from thread_sweep import thread

TOPK = 5


def queries(rows):
    """One query per authored subject, with its current value as the key.

    The subject phrase, not a question: v5 authors no questions, and inventing
    them here would make this a bench about phrasing. What is under test is
    slot allocation, which the anchor's exact wording does not decide.
    """
    latest = {}
    for r in rows:
        if r["kind"] in ("core", "habit"):
            k = r["thread"]
            if k not in latest or r["date"] > latest[k]["date"]:
                latest[k] = r
    return [(t, r["subject"], r["value"]) for t, r in sorted(latest.items())]


def slots(order, key_of, newest=None):
    """One slot per key. `newest` maps a key to the newest row of that thread.

    Cosine decides *which threads* answer; it must not decide which row of a
    thread answers. The nearest member of a thread is routinely a superseded
    value -- "I drive a Subaru" sits closer to the anchor `my car` than "my
    new car is a Tesla" does -- so a slot filled by rank answers confidently
    with an old fact. Filled by recency it answers with the current one.
    """
    out, used = [], set()
    for i in order:
        k = key_of(i)
        if k in used:
            continue
        used.add(k)
        out.append(newest[k] if newest and k in newest else i)
        if len(out) == TOPK:
            break
    return out


def score(rows, picks, thread_key, current):
    threads = {rows[i]["thread"] for i in picks}
    vals = [rows[i]["value"] for i in picks if rows[i]["thread"] == thread_key]
    has = current in vals
    return len(threads), has, (bool(vals) and not has)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=8000)
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--thresholds", default="0.86,0.88,0.90,0.92")
    a = ap.parse_args()
    ths = [float(x) for x in a.thresholds.split(",")]

    from embed import Embedder
    emb = Embedder()
    agg = defaultdict(list)

    for seed in range(a.seeds):
        rng = random.Random(20260909 + seed * 7919)
        rows = build_v5.build(rng, a.rows)
        texts = [r["text"] for r in rows]
        V = np.vstack([emb.encode(texts[i:i + 256])
                       for i in range(0, len(texts), 256)]).astype("float32")
        qs = queries(rows)
        Q = emb.encode([s for _, s, _ in qs]).astype("float32")
        labels = {t: thread(V, t, "first", set())[0] for t in ths}
        true_lab = [r["thread"] for r in rows]

        def newest_by(lab):
            best = {}
            for i, r in enumerate(rows):
                k = lab[i]
                if k not in best or r["date"] > rows[best[k]]["date"]:
                    best[k] = i
            return best

        newest_true = newest_by(true_lab)
        newest_pred = {t: newest_by(labels[t]) for t in ths}

        for qi, (tkey, _subj, current) in enumerate(qs):
            order = np.argsort(-(V @ Q[qi]))[:400]
            arms = [("flat", lambda i: i, None),
                    ("oracle", lambda i: true_lab[i], None),
                    ("oracle+now", lambda i: true_lab[i], newest_true)]
            for t in ths:
                key = (lambda L: (lambda i: L[i]))(labels[t])
                arms.append(("collapse@%.2f" % t, key, None))
                arms.append(("+now@%.2f" % t, key, newest_pred[t]))
            for name, key, nw in arms:
                agg[name].append(
                    score(rows, slots(order, key, nw), tkey, current))
        print("seed %d done" % seed, flush=True)

    head = "%-16s %9s %9s %9s" % ("arm", "distinct", "current", "stale")
    print("\n%d rows, %d seeds, %d queries/seed\n%s\n%s"
          % (a.rows, a.seeds, len(agg["flat"]) // a.seeds, head, "-" * len(head)))
    names = ["flat"]
    for t in ths:
        names += ["collapse@%.2f" % t, "+now@%.2f" % t]
    names += ["oracle", "oracle+now"]
    for name in names:
        rs = agg[name]
        print("%-16s %9.2f %9.3f %9.3f"
              % (name, st.mean(r[0] for r in rs), st.mean(r[1] for r in rs),
                 st.mean(r[2] for r in rs)))


if __name__ == "__main__":
    main()
