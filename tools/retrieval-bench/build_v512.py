"""Rewrite docs/vocabulary/v512.txt from its own parsed contents.

    python build_v512.py

The vocabulary is edited through this script rather than by hand. Editing
512 wrapped topics in a text file loses one silently -- the first draft
shipped 34 categories of 15 topics and nobody noticed until it was parsed.
Here the shape is asserted before anything is written: exactly 32
categories, exactly 15 topics plus `other`, no duplicate topic inside a
category, no category named twice.

Round-trips: reads the current file, applies EDITS, writes it back. The
prose header is preserved verbatim -- only the vocabulary block is
regenerated -- so the reasoning and the data stay in one reviewable file.
"""
import io, sys, textwrap

ROOT = __file__.rsplit("tools", 1)[0]
PATH = ROOT + "docs/vocabulary/v512.txt"

# Applied in order, then cleared once they are in the file. Kept here rather
# than applied by hand so a change is a diff of intent, not of layout.
EDITS = {
    # Round 1, after five outside reviews and bleed_v5. Each change is here
    # because a measurement backed it, not because a reviewer asked.
    #
    # admin was the measured catch-all: 45 rows drawn from all ten shipped
    # categories with its largest source at 28%, the flattest source mix on
    # the shelf. It was holding three different things -- records, dated
    # obligations, and counterparties. Narrowed to records; the obligations
    # go to plan, where a deadline was already going to live.
    "admin": "passport licence identification certificate registration membership"
             " contract renewal expiry record document will reference proof correspondence",
    "plan": "intention plan-goal wish idea someday bucket-list saving-for waiting-on"
            " pending research option appointment deadline commitment next-step",

    # work and worklife were second and third worst (H 2.75 and 2.64), which
    # is the reviewers' "split by aspect, not by subject" showing up as a
    # number. Re-cut so the boundary is the post versus the working week:
    # terms of employment stay in work, everything about doing the week
    # moves to worklife.
    "work": "employer role title duty team department workplace start-date contract"
            " hours responsibility policy benefit work-history pay-band",
    "worklife": "schedule shift remote leave work-holiday sick-day overtime meeting"
                " workload balance friction work-travel tool process wellbeing",

    # device/digital, asked for independently by three reviewers and visible
    # here as digital at H 2.36 over eight sources. The boundary is now
    # physical hardware versus data and accounts, so app and backup move.
    "device": "phone computer tablet watch camera console printer router speaker"
              " peripheral setup fault upgrade accessory charger",
    "digital": "account login service subscription app file photo email profile"
               " privacy storage sync backup credential notification",

    # moment was the loudest consensus flag and the corpus did not convict
    # it -- H 1.73 at 60% concentration, mid-table. It keeps its acquittal
    # but loses the two topics that were aspect-shaped rather than
    # subject-shaped, and that overlapped plan outright.
    "moment": "milestone memory anecdote first loss achievement regret turning-point"
              " story photo place year lesson era retelling",
}

GROUPS = [
    ("the person", ["identity", "character", "mood"]),
    ("the body", ["health", "care", "fitness", "rest"]),
    ("the dwelling", ["food", "home", "upkeep", "belongings"]),
    ("machines", ["vehicle", "device", "digital"]),
    ("people", ["family", "social", "occasion", "pet"]),
    ("work", ["work", "worklife", "project", "career", "learning"]),
    ("money", ["income", "spending", "finance"]),
    ("time", ["travel", "admin"]),
    ("everything a life is actually made of", ["leisure", "media", "moment", "plan"]),
]


def parse(path=PATH):
    cats, cur = {}, None
    body = io.open(path, encoding="utf-8").read().split("## vocabulary", 1)[-1]
    for raw in body.split("\n"):
        line = raw.rstrip()
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith(" ") and ":" in line:
            cur, rest = line.split(":", 1)
            cats[cur] = rest.split()
        elif cur:
            cats[cur] += line.split()
    return cats


def check(cats):
    assert len(cats) == 32, "expected 32 categories, got %d" % len(cats)
    named = [c for g in GROUPS for c in g[1]]
    assert sorted(named) == sorted(cats), "GROUPS and the file disagree: %s" % (
        set(named) ^ set(cats))
    for c, topics in cats.items():
        assert topics[-1] == "other", "%s does not end in other" % c
        assert len(topics) == 16, "%s has %d topics, want 16" % (c, len(topics))
        assert len(set(topics)) == 16, "%s repeats a topic" % c
    return cats


def write(cats, path=PATH):
    head = io.open(path, encoding="utf-8").read().split("## vocabulary")[0]
    out = [head.rstrip("\n"), "", "## vocabulary", ""]
    for group, members in GROUPS:
        out.append("# -- %s %s" % (group, "-" * max(3, 66 - len(group))))
        for c in members:
            wrapped = textwrap.wrap(" ".join(cats[c]), 74, subsequent_indent="  ",
                                    break_on_hyphens=False, break_long_words=False)
            out.append("%s: %s" % (c, wrapped[0]))
            out.extend(wrapped[1:])
        out.append("")
    io.open(path, "w", encoding="utf-8", newline="\n").write("\n".join(out).rstrip("\n") + "\n")


def main():
    cats = parse()
    for c, topics in EDITS.items():
        assert c in cats, "EDITS names an unknown category %r" % c
        cats[c] = topics.split() + ["other"]
    check(cats)
    write(cats)
    print("%d categories, %d pairs" % (len(cats), sum(map(len, cats.values()))))


if __name__ == "__main__":
    main()
