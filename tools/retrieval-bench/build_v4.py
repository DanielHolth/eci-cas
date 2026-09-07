"""Build the v4 archive for one tier, and report the shape it actually got.

Separate from read_bench because building is now a long job with a plan
behind it, and the plan is worth printing before and after. The v3 archives
were three rows a pair and needed no supervision; a v4 build is hundreds of
padding calls against a distribution that the model can fail to reach.

    python build_v4.py                 # plan only, generates nothing
    python build_v4.py --build         # build (long)

Tier-tagged on purpose: `.archive_v4_<tier>.json`. The archive is written by
the tier's own model, so comparing two tiers end-to-end over two archives
mixes a write difference with a read difference and can attribute neither.
Tagging is what makes the cross arm possible later -- one tier's readers over
another tier's archive, same rows, only the reader changing. Cheap now,
a retrofit afterwards.

What "the shape it actually got" means. `pad_rows` asks in batches and
dedupes, and a small model asked for sixty facts about one narrow folder runs
out of ideas well before sixty. A pair that stalls ends up shorter than
planned. That is acceptable -- it is still lumpy, which is the property being
bought -- but it must be visible, because "the fat band is 50-80 rows" is a
claim this file either delivers or reports failing to.
"""
import json, os, sys, collections

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import bench
import read_bench as rb
import retrieval_v4 as corpus

TIER = os.environ.get("BENCH_TIER", "minimal")
CACHE = ROOT + "tools/retrieval-bench/.archive_v4_%s.json" % TIER

BANDS = (("fat", 50, 80), ("middle", 10, 25), ("thin", 1, 4))


def band(n):
    for name, lo, hi in BANDS:
        if lo <= n <= hi:
            return name
    return "?"


def report(sizes, title):
    by = collections.Counter(band(n) for n in sizes.values())
    total = sum(sizes.values())
    print("\n%s" % title)
    for name, lo, hi in BANDS:
        got = [n for n in sizes.values() if band(n) == name]
        print("  %-7s %3d pairs  %5d rows  %s" % (
            name, by[name], sum(got),
            "%d-%d" % (min(got), max(got)) if got else "-"))
    print("  %-7s %3d pairs  %5d rows" % ("total", len(sizes), total))
    return total


def plan():
    pairs = rb.all_pairs()
    sizes = rb.plan_sizes(pairs)
    total = report(sizes, "planned (padding only, before gold rows land)")
    # Padding is asked in batches of at most six, so the call count is what
    # the wall clock and any paid tier's invoice track -- not the row count.
    calls = sum(-(-n // 6) for n in sizes.values())
    print("\n  padding calls   ~%d" % calls)
    print("  gold calls       %d  (%d statements x archivist + cataloger)"
          % (2 * len(corpus.STATEMENTS), len(corpus.STATEMENTS)))
    print("  questions        %d direct, %d null, %d oblique"
          % (sum(len(q) for _, q in corpus.STATEMENTS),
             len(corpus.NULLS), len(corpus.OBLIQUE)))
    return sizes, total


def main():
    sizes, _ = plan()
    if "--build" not in sys.argv:
        print("\nplan only. re-run with --build to generate %s"
              % os.path.basename(CACHE))
        return
    if os.path.exists(CACHE):
        print("\n%s exists; delete it to rebuild." % os.path.basename(CACHE))
        return

    print("\nbuilding %s ..." % os.path.basename(CACHE), flush=True)
    a, rows = rb.archive(CACHE, sizes=sizes, src=corpus)

    got = {p: len(rs) for p, rs in rows.items()}
    report(got, "actual (gold + padding, as built)")
    short = sorted((p, sizes.get(p, 3), got.get(p, 0)) for p in sizes
                   if got.get(p, 0) < sizes[p] * 0.7)
    if short:
        print("\n  %d pairs came up short of plan (model stalled):" % len(short))
        for p, want, have in short[:12]:
            print("    %-34s planned %2d  got %2d" % (p, want, have))
        if len(short) > 12:
            print("    ... and %d more" % (len(short) - 12))
    print("\ngold rows %d over %d pairs" % (
        len(a["gold"]), len(set(g["pair"] for g in a["gold"]))))


if __name__ == "__main__":
    main()
