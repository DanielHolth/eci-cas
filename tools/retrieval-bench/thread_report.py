"""Read .v5_threads.jsonl. Runs against a partial file on purpose.

The sweep appends a seed at a time, so this is meant to be pointed at a run
still going. It prints the seed count it is summarising over, because a
number over three seeds and the same number over thirty are not the same
claim and the header is the only place that stays true.

Ordering of the columns is the ordering of the argument: the merge column is
the one that decides the threshold, and everything else is context for it.
"""
import json, os, statistics as st, sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, ".v5_threads.jsonl")


def load(path=SRC):
    rows = []
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if line:
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                pass          # a torn last line while the sweep is writing
    return rows


def main():
    rows = load(sys.argv[1] if len(sys.argv) > 1 else SRC)
    if not rows:
        print("no results yet")
        return
    seeds = sorted({r["seed"] for r in rows})
    print("%d records over %d seeds, %d rows each, %s"
          % (len(rows), len(seeds), rows[0]["rows"], rows[0]["embedder"]))

    by = defaultdict(list)
    for r in rows:
        by[(r["strategy"], r["threshold"])].append(r)

    def mean(rs, key):
        return st.mean(r[key] for r in rs)

    def adv(rs):
        # the worst named pair, averaged over seeds -- an average across the
        # pairs would let two clean pairs pay for one glued one.
        vals = []
        for r in rs:
            vals.append(max(r["adversarial"].values()) if r["adversarial"] else 0.0)
        return st.mean(vals)

    head = ("%-9s %6s %8s %8s %8s %8s %9s"
            % ("strategy", "thr", "merge", "split", "adv", "now", "threads"))
    print()
    print(head)
    print("-" * len(head))
    for strategy in ("first", "centroid", "newest"):
        keys = sorted(k for k in by if k[0] == strategy)
        for k in keys:
            rs = by[k]
            print("%-9s %6.2f %8.3f %8.3f %8.3f %8.3f %9.0f"
                  % (strategy, k[1], mean(rs, "merge_err"),
                     mean(rs, "split_err"), adv(rs), mean(rs, "now_correct"),
                     mean(rs, "threads_pred")))
        print()

    # The growth claim, at the strategy and threshold that look safest, so
    # the curve is read where the design would actually sit.
    safe = [k for k in by
            if adv(by[k]) == 0.0 and mean(by[k], "merge_err") < 0.05]
    if safe:
        k = min(safe, key=lambda k: mean(by[k], "split_err"))
        print("growth at the safest point (%s, %.2f): "
              "utterances -> representatives" % k)
        curves = defaultdict(list)
        for r in by[k]:
            for n, reps in r["growth"]:
                curves[n].append(reps)
        for n in sorted(curves):
            print("   %8d %8.0f" % (n, st.mean(curves[n])))
    else:
        print("no point yet with a clean adversarial table and merge < 5%")


if __name__ == "__main__":
    main()
