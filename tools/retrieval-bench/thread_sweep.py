"""Threading, benched -- the arm the inversion rests on and never measured.

docs/roadmap.md makes threading load-bearing: repetition costing one slot,
"what car do I drive" answering with *now*, and the consolidator's gate all
run through a write-time cosine sweep against one representative per thread.
The threshold is picked in prose ("start near 0.9 rather than 0.85 and let
the corpus argue it down") and the corpus has never been asked.

Three things are being settled here, and they are not the same question:

  merge   two different subjects glued into one thread. The roadmap is right
          that this is the asymmetric error -- a false merge makes *now*
          wrong and read time cannot undo it -- so it is the headline, and
          the adversarial pairs (my car / my wife's car, where I live / where
          my parents live) are reported by name rather than folded into an
          average. An average over a corpus with hundreds of easy threads
          will hide exactly the failure this exists to find.
  split   one subject scattered across several threads. Recoverable: it
          restores today's behaviour, which is duplicates.
  growth  "the set it scans grows with distinct subjects rather than with
          utterances". This is an assumption about human speech stated as a
          fact, and the whole cost argument for write-time threading depends
          on it. Measured as representatives against utterances at
          checkpoints, so the shape of the curve is visible and not just its
          endpoint.

**Replication is by corpus, not by re-running.** The sweep is deterministic:
the same vectors and the same threshold give the same threads every time, so
repeating it measures nothing. What varies is which paraphrase each
restatement drew and when it landed, so a seed is a fresh corpus, embedded
fresh. That is where the run time goes, and it is the honest place for it.

Results append to .v5_threads.jsonl as each seed finishes, so a run stopped
early is still a result. --budget stops cleanly at a wall-clock limit.

    python thread_sweep.py --rows 20000 --seeds 12 --budget 4500
"""
import argparse, itertools, json, os, random, time
import numpy as np

import build_v5

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, ".v5_threads.jsonl")

# Named pairs whose merge is the unrecoverable error. Left of the slash is
# the thread whose *now* goes wrong when the merge happens.
ADVERSARIAL = [("car.mine", "car.wife"),
               ("city", "city.parents"),
               ("allergy", "allergy.child")]

STRATEGIES = ["first", "centroid", "newest"]
CHECKPOINTS = [500, 1000, 2000, 5000, 10000, 20000]


def thread(vecs, threshold, strategy, checkpoints):
    """The write-time sweep, in arrival order. Returns labels and growth.

    One representative per thread, as specified. `first` freezes it at the
    thread's opening row, `centroid` is the running mean renormalised, and
    `newest` is the last member -- which is the chaining the roadmap warns
    about, included because a warning is not a measurement.
    """
    n = vecs.shape[0]
    reps = np.zeros((max(n // 4, 64), vecs.shape[1]), dtype="float32")
    sums = np.zeros_like(reps)
    counts = np.zeros(reps.shape[0], dtype="int32")
    labels = np.empty(n, dtype="int32")
    growth, k = [], 0

    for i in range(n):
        v = vecs[i]
        if k:
            sims = reps[:k] @ v
            j = int(np.argmax(sims))
            hit = sims[j] >= threshold
        else:
            hit = False
        if hit:
            labels[i] = j
            counts[j] += 1
            sums[j] += v
            if strategy == "centroid":
                c = sums[j]
                reps[j] = c / (np.linalg.norm(c) or 1.0)
            elif strategy == "newest":
                reps[j] = v
        else:
            if k == reps.shape[0]:
                reps = np.vstack([reps, np.zeros_like(reps)])
                sums = np.vstack([sums, np.zeros_like(sums)])
                counts = np.concatenate([counts, np.zeros_like(counts)])
            labels[i] = k
            reps[k] = v
            sums[k] = v
            counts[k] = 1
            k += 1
        if (i + 1) in checkpoints:
            growth.append([i + 1, k])
    return labels, k, growth


def pair_scores(truth, pred):
    """Pairwise precision and recall over the contingency table.

    Counting pairs directly is quadratic; counting them from the table is
    not, and at twenty thousand rows that is the difference between a metric
    and a second bench.
    """
    from collections import Counter
    joint = Counter(zip(truth, pred))
    tsize = Counter(truth)
    psize = Counter(pred)

    def c2(x):
        return x * (x - 1) // 2

    tp = sum(c2(v) for v in joint.values())
    same_pred = sum(c2(v) for v in psize.values())
    same_true = sum(c2(v) for v in tsize.values())
    prec = tp / same_pred if same_pred else 1.0
    rec = tp / same_true if same_true else 1.0
    return prec, rec


def adversarial_merges(rows, labels):
    """Did a named pair land in one thread, and how far did it get.

    Reported as the fraction of the smaller thread's rows that were absorbed,
    not as a boolean: one stray row is a nuisance and half the thread is the
    failure mode.
    """
    out = {}
    for a, b in ADVERSARIAL:
        la = [labels[i] for i, r in enumerate(rows) if r["thread"] == a]
        lb = [labels[i] for i, r in enumerate(rows) if r["thread"] == b]
        if not la or not lb:
            continue
        shared = set(la) & set(lb)
        worst = 0.0
        for lab in shared:
            n = min(sum(1 for x in la if x == lab),
                    sum(1 for x in lb if x == lab))
            worst = max(worst, n / min(len(la), len(lb)))
        out["%s|%s" % (a, b)] = round(worst, 3)
    return out


def now_correct(rows, labels):
    """For each authored core thread, is its newest row the current value.

    This is the one thing the pair store was good at, and the property the
    inversion has to recover without a schema. Scored per thread: take the
    predicted thread containing most of the true thread's rows, and ask
    whether its newest member carries the value that is actually current.
    """
    from collections import Counter, defaultdict
    by_true = defaultdict(list)
    for i, r in enumerate(rows):
        if r["kind"] == "core":
            by_true[r["thread"]].append(i)
    hits = total = 0
    for key, idx in by_true.items():
        current = max((rows[i] for i in idx),
                      key=lambda r: r["date"])["value"]
        dominant = Counter(labels[i] for i in idx).most_common(1)[0][0]
        members = [i for i in range(len(rows)) if labels[i] == dominant]
        newest = max(members, key=lambda i: rows[i]["date"])
        total += 1
        hits += rows[newest]["value"] == current
    return hits / total if total else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=20000)
    ap.add_argument("--seeds", type=int, default=12)
    ap.add_argument("--budget", type=float, default=4500,
                    help="wall-clock seconds; stops cleanly between seeds")
    ap.add_argument("--lo", type=float, default=0.70)
    ap.add_argument("--hi", type=float, default=0.99)
    ap.add_argument("--step", type=float, default=0.01)
    a = ap.parse_args()

    from embed import Embedder
    emb = Embedder()
    thresholds = [round(x, 4) for x in
                  np.arange(a.lo, a.hi + 1e-9, a.step)]
    checkpoints = {c for c in CHECKPOINTS if c <= a.rows} | {a.rows}
    started = time.time()
    print("thread_sweep: %d rows, %d seeds, %d thresholds x %d strategies, "
          "budget %.0fs" % (a.rows, a.seeds, len(thresholds),
                            len(STRATEGIES), a.budget), flush=True)

    for seed in range(a.seeds):
        if time.time() - started > a.budget:
            print("budget reached, stopping after %d seeds" % seed, flush=True)
            break
        t0 = time.time()
        rng = random.Random(20260909 + seed * 7919)
        rows = build_v5.build(rng, a.rows)
        texts = [r["text"] for r in rows]
        vecs = np.vstack([emb.encode(texts[i:i + 256])
                          for i in range(0, len(texts), 256)]).astype("float32")
        truth = [r["thread"] for r in rows]
        n_true = len(set(truth))
        print("seed %d: %d rows, %d true threads, embedded in %.0fs"
              % (seed, len(rows), n_true, time.time() - t0), flush=True)

        with open(OUT, "a", encoding="utf-8") as fh:
            for strategy, th in itertools.product(STRATEGIES, thresholds):
                labels, k, growth = thread(vecs, th, strategy, checkpoints)
                prec, rec = pair_scores(truth, list(labels))
                rec_out = {
                    "seed": seed, "rows": a.rows, "strategy": strategy,
                    "threshold": th, "threads_true": n_true,
                    "threads_pred": k,
                    "merge_err": round(1 - prec, 4),
                    "split_err": round(1 - rec, 4),
                    "now_correct": round(now_correct(rows, labels), 4),
                    "adversarial": adversarial_merges(rows, labels),
                    "growth": growth,
                    "embedder": emb.name,
                }
                fh.write(json.dumps(rec_out) + "\n")
                fh.flush()
        print("seed %d done in %.0fs -> %s"
              % (seed, time.time() - t0, os.path.basename(OUT)), flush=True)

    print("elapsed %.0fs" % (time.time() - started), flush=True)


if __name__ == "__main__":
    main()
