"""The threshold as a gate, not as a verdict.

`thread_sweep.py` scores the threshold as if it decided the thread by itself:
argmax above the line joins, and every mistake is permanent. That is not the
design. The roadmap has a consolidator reading the top five candidates and
saying which, if any, the new row belongs to -- so the threshold's job is
recall into a candidate set, and precision is the model's job.

Which changes what is worth measuring, and it moves the answer:

  recall@5   when the row's thread already exists, is it among the five
             candidates above the line. This is the ceiling on the whole
             design: a thread that does not make the candidate set cannot be
             recovered by any model reading it, and the row splits.
  call       fraction of rows with at least one candidate above the line --
             the consolidator's bill, one call per row that has candidates.
  saved      of those, the fraction the roadmap's keyword shortcut answers
             without a call: cosine clears the line and the keyword sets
             agree, so it is a restatement and nothing is in dispute. Uses
             the extractor batch 22 validated.
  oracle     merge, split and now under a *perfect* adjudicator of those five
             candidates. The upper bound the consolidator is being bought to
             approach, and the number that says whether a lower threshold is
             affordable.

The gap between the `auto` and `oracle` rows is exactly what the model is
being paid for. Where they meet, it is being paid for nothing.
"""
import argparse, json, os, random, statistics as st
import numpy as np

import build_v5
from keyword_gate import extract, document_frequency

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, ".v5_gate.jsonl")
TOPK = 5


def run(vecs, rows, threshold, mode, df, rare_max=2):
    """One pass in arrival order. `mode` is 'auto' or 'oracle'.

    auto    join the nearest candidate above the line -- today's arm.
    oracle  join the candidate among the top five that truly belongs, and
            mint a new thread when none of them does. A consolidator that is
            never wrong, which no model is; it bounds the design rather than
            predicting it.
    """
    n, dim = vecs.shape
    cap = 4096
    reps = np.zeros((cap, dim), dtype="float32")
    rep_thread = [None] * cap          # true thread of the representative
    rep_keys = [None] * cap
    labels = np.empty(n, dtype="int32")
    k = 0
    calls = saved = considered = recall_hit = recall_possible = 0
    seen = set()

    # Content-word set, not the rare-filtered one: at twenty thousand rows
    # "rare" admits almost nothing, and the question here is whether two
    # utterances say the same thing, which is a disagreement test over content
    # words rather than over names.
    keywords = [frozenset(extract(r["text"], df, {"all"})) for r in rows]

    for i in range(n):
        v, truth = vecs[i], rows[i]["thread"]
        cand = []
        if k:
            sims = reps[:k] @ v
            above = np.nonzero(sims >= threshold)[0]
            if above.size:
                order = above[np.argsort(-sims[above])][:TOPK]
                cand = list(order)

        if truth in seen:
            recall_possible += 1
            if any(rep_thread[j] == truth for j in cand):
                recall_hit += 1

        if cand:
            considered += 1
            top = cand[0]
            # The roadmap's shortcut: cosine clears the line and the keyword
            # sets agree, so it is a restatement and there is nothing for a
            # model to adjudicate.
            if keywords[i] and keywords[i] == rep_keys[top]:
                saved += 1
                chosen = top
            else:
                calls += 1
                if mode == "oracle":
                    match = [j for j in cand if rep_thread[j] == truth]
                    chosen = match[0] if match else None
                else:
                    chosen = top
        else:
            chosen = None

        if chosen is None:
            if k == cap:
                reps = np.vstack([reps, np.zeros_like(reps)])
                rep_thread += [None] * cap
                rep_keys += [None] * cap
                cap *= 2
            labels[i] = k
            reps[k] = v
            rep_thread[k] = truth
            rep_keys[k] = keywords[i]
            k += 1
        else:
            labels[i] = chosen
        seen.add(truth)

    return {
        "threads_pred": k,
        "recall_at5": recall_hit / recall_possible if recall_possible else 1.0,
        "call_rate": calls / n,
        "saved_rate": saved / considered if considered else 0.0,
        "labels": labels,
    }


def main():
    import thread_sweep as ts
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=8000)
    ap.add_argument("--seeds", type=int, default=5)
    ap.add_argument("--lo", type=float, default=0.84)
    ap.add_argument("--hi", type=float, default=0.96)
    ap.add_argument("--step", type=float, default=0.01)
    a = ap.parse_args()

    from embed import Embedder
    emb = Embedder()
    thresholds = [round(x, 4) for x in np.arange(a.lo, a.hi + 1e-9, a.step)]
    agg = {}

    for seed in range(a.seeds):
        rng = random.Random(20260909 + seed * 7919)
        rows = build_v5.build(rng, a.rows)
        texts = [r["text"] for r in rows]
        vecs = np.vstack([emb.encode(texts[i:i + 256])
                          for i in range(0, len(texts), 256)]).astype("float32")
        df = document_frequency(texts)
        truth = [r["thread"] for r in rows]
        for mode in ("auto", "oracle"):
            for th in thresholds:
                out = run(vecs, rows, th, mode, df)
                labels = list(out.pop("labels"))
                prec, rec = ts.pair_scores(truth, labels)
                out.update({
                    "merge_err": 1 - prec, "split_err": 1 - rec,
                    "now_correct": ts.now_correct(rows, labels),
                    "adversarial": max(
                        ts.adversarial_merges(rows, labels).values() or [0.0]),
                })
                agg.setdefault((mode, th), []).append(out)
        print("seed %d done" % seed, flush=True)

    with open(OUT, "w", encoding="utf-8") as fh:
        for (mode, th), rs in agg.items():
            fh.write(json.dumps({"mode": mode, "threshold": th,
                                 "rows": a.rows, "seeds": a.seeds,
                                 **{k: st.mean(r[k] for r in rs)
                                    for k in rs[0]}}) + "\n")

    head = ("%-7s %6s %9s %8s %8s %8s %8s %8s %8s"
            % ("mode", "thr", "recall@5", "call", "saved", "merge", "split",
               "adv", "now"))
    print("\n%d rows, %d seeds\n%s\n%s"
          % (a.rows, a.seeds, head, "-" * len(head)))
    for mode in ("auto", "oracle"):
        for th in thresholds:
            rs = agg[(mode, th)]
            m = lambda k: st.mean(r[k] for r in rs)
            print("%-7s %6.2f %9.3f %8.3f %8.3f %8.3f %8.3f %8.3f %8.3f"
                  % (mode, th, m("recall_at5"), m("call_rate"),
                     m("saved_rate"), m("merge_err"), m("split_err"),
                     m("adversarial"), m("now_correct")))
        print()


if __name__ == "__main__":
    main()
