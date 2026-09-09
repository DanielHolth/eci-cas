"""v5 -- a longitudinal archive, authored structurally, rendered second.

Pre-registered in docs/roadmap.md. v4 cannot ask a Characterise question:
its statements are one-shot, undated, unrepeated and single-speaker. This
builds the instrument that can, and it builds it big, because two of the
claims under test are claims about volume:

  * threading's representative set "grows with distinct subjects rather than
    with utterances", which is unfalsifiable at 78 rows;
  * rarity as a keyword filter, which admits nearly everything in a corpus
    of 78 (batch 22) and only becomes a filter when a corpus has a
    background.

**Structure first, text second.** Every fact exists as a record -- thread,
subject, value, speaker, timestamp -- before any sentence is written, so the
key is a property of the generator rather than a judgement about prose. That
is what makes the Characterise key ("these terms must appear, these must
not") and the threading key (which utterances belong to one thread) exact
rather than annotated.

**What generation cannot buy.** Templates give idiom no real archive has,
and an arm that wins here on phrasing regularity has not won. This corpus is
therefore pointed only at questions about *structure* -- threading errors,
representative growth, aggregate ranking -- and never at extraction quality,
which v4 owns and which is measured against hand-written text on purpose.
Paraphrase pools are deliberately wide and the adversarial pairs are authored
by hand for the same reason: a restatement must not be a string match, and
two different threads must be allowed to look alike.
"""
import json, os, random, sys, datetime as dt

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, ".v5_corpus.json")
VEC = os.path.join(HERE, ".v5_vectors.npy")

START = dt.date(2016, 1, 1)
END = dt.date(2026, 9, 1)
SPAN = (END - START).days

# ---------------------------------------------------------------------------
# The evaluation core: hand-authored threads, including the pairs that exist
# to break a threshold. Successive values inside one thread supersede their
# predecessors; separate keys must never merge.
# ---------------------------------------------------------------------------

CORE = [
 ("car.mine", "my car", [
    ("Subaru", ["I drive a Subaru", "my car is a Subaru",
                "the Subaru has been solid so far",
                "I have had the Subaru for years now"]),
    ("Tesla", ["my new car is a Tesla", "I drive a Tesla now",
               "swapped the old estate for a Tesla",
               "the Tesla is so much better than the old one"])]),
 # the pair the consolidator exists for: near-identical surface, different
 # subject, and a false merge here makes "what car do I drive" wrong.
 ("car.wife", "my wife's car", [
    ("Volvo", ["my wife's new car is a Volvo", "Ingrid drives a Volvo",
               "the Volvo is my wife's, not mine",
               "her car is the Volvo in the drive"])]),

 ("city", "where I live", [
    ("Bergen", ["we live in Bergen", "home is Bergen these days",
                "Bergen has been home for a while"]),
    ("Oslo", ["we have moved to Oslo", "I live in Oslo now",
              "Oslo is home since the move"])]),
 ("city.parents", "where my parents live", [
    ("Bodo", ["my parents live in Bodo", "mum and dad are up in Bodo",
              "the folks are still in Bodo"])]),

 ("job", "my job", [
    ("hydrologist", ["I work as a hydrologist",
                     "my job is hydrology at the institute",
                     "I have been a hydrologist for years"]),
    ("consultant", ["I have gone consultant",
                    "I consult independently now",
                    "left the institute, consulting these days"])]),

 ("dog", "our dog", [
    ("Buddy", ["our dog Buddy is getting old", "Buddy needs a walk",
               "Buddy is the best dog we have had"]),
    ("Rex", ["the new dog is called Rex", "Rex chewed the rug again",
             "we got Rex after we lost Buddy"])]),

 ("phone", "my phone", [
    ("Pixel", ["I use a Pixel", "my phone is a Pixel"]),
    ("iPhone", ["switched to an iPhone", "my phone is an iPhone now"])]),

 ("series", "what we are watching", [
    ("Borgen", ["we are watching Borgen", "Borgen again tonight"]),
    ("Succession", ["we started Succession",
                    "Succession is the current one"])]),

 ("allergy", "my allergy", [
    ("penicillin", ["I am allergic to penicillin",
                    "penicillin is the one I cannot take"])]),
 ("allergy.child", "Ingrid's allergy", [
    ("hazelnuts", ["Ingrid is allergic to hazelnuts",
                   "hazelnuts are the problem for Ingrid"])]),
]

# ---------------------------------------------------------------------------
# The habit layer -- what Characterise is for. Frequencies are authored, and
# the hinge is the date a former habit stops and never resumes.
# (key, terms, count, window, paraphrases); window: all | pre | post
# ---------------------------------------------------------------------------

HINGE = dt.date(2021, 6, 1)

HABITS = [
 ("swim", ["swim", "pool"], 14, "all",
  ["swam before work", "did my lengths at the pool",
   "swimming again this morning", "a slow swim, but a swim"]),
 ("coffee", ["coffee", "black"], 18, "all",
  ["black coffee, no sugar", "made coffee before anyone was up",
   "coffee first, then everything else"]),
 ("smoke", ["smoke", "cigarette"], 9, "pre",
  ["stepped out for a cigarette", "smoking too much this week",
   "a cigarette on the balcony"]),
 ("running", ["run", "running"], 11, "post",
  ["went for a run", "running has stuck, oddly",
   "a slow run after work"]),
 # the decoy: real, mentioned, and not a habit. An arm ranking by raw count
 # should be tempted by it only if the counts are close, so it sits near the
 # floor rather than being absent.
 ("sushi", ["sushi"], 3, "all",
  ["we had sushi", "sushi for once", "sushi again, twice in a month"]),
]

SPEAKERS = ["user", "user", "user", "assistant", "ingrid"]

# Background filler. Its job is to give the corpus a term distribution -- the
# thing batch 22 could not measure -- and to give the threading sweep a real
# number of distinct subjects to hold representatives for.
FILLER_SUBJ = ["the boiler", "the bins", "the car tyres", "the roof",
    "the loft", "the fence", "the kitchen tap", "the garden shed",
    "the printer", "the dishwasher", "the router", "the bike", "the kayak",
    "the freezer", "the hedge", "the gutters", "the porch light",
    "the washing machine"]
FILLER_PRED = ["needs looking at", "is playing up again", "was finally sorted",
    "cost more than it should have", "will have to wait until spring",
    "is on the list", "held up better than expected"]
FILLER_TOPIC = ["the ferry timetable", "the school run",
    "the neighbours hedge", "the price of electricity",
    "the queue at the post office", "the weather", "the road works",
    "the new bakery", "the bus that never comes"]


def rand_date(rng, window="all"):
    if window == "pre":
        lo, hi = 0, (HINGE - START).days
    elif window == "post":
        lo, hi = (HINGE - START).days, SPAN
    else:
        lo, hi = 0, SPAN
    return START + dt.timedelta(days=rng.randrange(lo, hi))


def build(rng, filler_target):
    rows = []

    def add(text, thread, subject, value, when, kind, speaker=None):
        rows.append({"text": text, "thread": thread, "subject": subject,
                     "value": value, "date": when.isoformat(), "kind": kind,
                     "speaker": speaker or rng.choice(SPEAKERS)})

    # Core threads: each value restated several times inside its own era, so
    # redundancy and supersession are both present and separable.
    for key, subject, values in CORE:
        cuts = sorted(rand_date(rng) for _ in range(len(values) - 1))
        eras, lo = [], START
        for c in cuts + [END]:
            eras.append((lo, c))
            lo = c
        for (value, paras), (a, b) in zip(values, eras):
            span = max((b - a).days, 1)
            for _ in range(rng.randrange(4, 13)):
                when = a + dt.timedelta(days=rng.randrange(span))
                add(rng.choice(paras), key, subject, value, when, "core")

    for key, terms, count, window, paras in HABITS:
        for _ in range(count):
            add(rng.choice(paras), "habit." + key, key, key,
                rand_date(rng, window), "habit", speaker="user")

    while len(rows) < filler_target:
        if rng.random() < 0.4:
            s = rng.choice(FILLER_SUBJ)
            add("%s %s" % (s, rng.choice(FILLER_PRED)), "filler." + s, s, s,
                rand_date(rng), "filler")
        else:
            t = rng.choice(FILLER_TOPIC)
            add("we talked about %s" % t, "filler." + t, t, t,
                rand_date(rng), "filler")

    rows.sort(key=lambda r: r["date"])
    for i, r in enumerate(rows):
        r["ordinal"] = i
    return rows


def main():
    target = int(sys.argv[1]) if len(sys.argv) > 1 else 20000
    rng = random.Random(20260909)
    rows = build(rng, target)
    threads = sorted({r["thread"] for r in rows})
    print("v5: %d utterances, %d threads, %s..%s"
          % (len(rows), len(threads), rows[0]["date"], rows[-1]["date"]))
    json.dump({"rows": rows, "hinge": HINGE.isoformat(),
               "habits": [[k, t, c, w] for k, t, c, w, _ in HABITS]},
              open(OUT, "w", encoding="utf-8"))
    print("wrote", OUT, flush=True)

    from embed import Embedder
    import numpy as np
    e = Embedder()
    texts = [r["text"] for r in rows]
    vecs, B = [], 256
    for i in range(0, len(texts), B):
        vecs.append(e.encode(texts[i:i + B]))
        print("  embedded %d/%d" % (min(i + B, len(texts)), len(texts)),
              flush=True)
    np.save(VEC, np.vstack(vecs).astype("float32"))
    print("wrote", VEC, "model", e.name)


if __name__ == "__main__":
    main()
