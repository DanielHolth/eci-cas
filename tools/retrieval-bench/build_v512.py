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
RENAMES = {
    # `admin` was a generic word, and a generic word is a catch-all with a
    # head start: anything vaguely official reads as admin to a writer and
    # to a bi-encoder alike. It measured that way too (H 2.89, all ten
    # shipped sources). Round 1 narrowed the contents; this narrows the
    # name. A record is a thing that exists and can be produced on demand,
    # which is exactly what the drawer now holds.
    "admin": "record",

    # Round 3. `worklife` was forced, and the probe that showed it: ask the
    # shelf where "work-culture", "work-norms", "work-environment" and
    # "work-social" belong and every one of them tied at the noise floor
    # (worklife/work-travel 0.875, worklife/work-holiday 0.870,
    # worklife/other 0.865, work/workplace 0.864 -- nine millipoints across
    # four unrelated drawers). "My office is open-plan and noisy" landed on
    # plan/appointment. The character of a workplace had no address.
    #
    # The cause is that worklife was three things in a bag: the calendar,
    # the load, and the doing. Re-cut so the axis is the CONTRACT versus
    # the PLACE. Same probes after: 0.902, 0.910, 0.896, and the office
    # sentence at 0.845 -- separation instead of ties.
    "worklife": "workplace",
}

# Applied in order, then cleared once they are in the file. Kept here rather
# than applied by hand so a change is a diff of intent, not of layout.
EDITS = {
    # Round 2. The rename forces two topics out.
    #
    # `record` as a topic inside a category called record says nothing --
    # record/record is an address with no content. `correspondence` is the
    # reconsideration the rename asked for: a letter is not a record of
    # anything, it is the delivery of one, and it was the topic through
    # which unrelated official-sounding rows entered the drawer in the
    # first place. A letter about a renewal is the renewal; a letter about
    # a bill is spending. Nothing needs a drawer for the envelope.
    #
    # In their place: `permit`, which was homeless (a parking permit, a
    # burn permit, a work visa is travel but a residence permit is not),
    # and `where-kept`, because "where is my passport" is a real question
    # about a record and no other drawer answers it.
    #
    # `expiry` also goes, and it was measured out rather than argued out:
    # 26 rows from 8 sources at 26% concentration, the single worst drawer
    # left on the shelf. It is aspect-shaped -- a licence expires, so does
    # a subscription, a course, a passport, a warranty -- so it collected
    # the expiring of things that live elsewhere. `deed` replaces it and
    # the category falls 104 rows to 86, H 2.85 to 2.74. Renewal stays:
    # renewing is something you do TO a record you hold.
    "record": "passport licence identification certificate registration membership"
              " contract renewal deed permit document will reference proof where-kept",

    # identity/record is now ambiguous against the category of the same
    # name. `gender` was missing from identity anyway, next to pronoun.
    "identity": "name nickname age birthdate origin nationality language pronoun"
                " appearance height handedness marital gender signature document-name",

    # Round 3, with the worklife -> workplace rename above.
    #
    # work/workplace goes because the category now carries the name -- the
    # same namespace collision the record rename produced. `notice` takes
    # the slot: it is a term of the post and nothing else held it.
    "work": "employer role title duty team department contract start-date notice"
            " hours responsibility policy benefit work-history pay-band",

    # What LEFT rather than moved here: work-holiday and work-travel to
    # travel, sick-day to health, meeting/tool/process to project (which
    # already holds project-tool), workload and balance folded into
    # friction and wellbeing, which is what they were describing anyway.
    "workplace": "culture norms environment office colleague manager morale politics"
                 " schedule shift remote leave overtime friction wellbeing",

    # Round 4, and it is a SMALL edit on purpose -- see the header note on
    # what the gloss test found. `routine` and `activity` were vacuous
    # aspect words that no sentence ever contains; `rest-day` is rest;
    # `hobby-skill` was a duplicate of `hobby` that beat it on the one
    # probe leisure won. In their place four nouns people actually write:
    # competition, equipment, technique, puzzle.
    "leisure": "hobby craft game puzzle outdoor gardening collecting making club"
               " event competition equipment technique volunteering interest",
}

GROUPS = [
    ("the person", ["identity", "character", "mood"]),
    ("the body", ["health", "care", "fitness", "rest"]),
    ("the dwelling", ["food", "home", "upkeep", "belongings"]),
    ("machines", ["vehicle", "device", "digital"]),
    ("people", ["family", "social", "occasion", "pet"]),
    ("work", ["work", "workplace", "project", "career", "learning"]),
    ("money", ["income", "spending", "finance"]),
    ("time", ["travel", "record"]),
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
    for old, new in RENAMES.items():
        if old in cats:
            assert new not in cats, "rename target %r already exists" % new
            cats = {(new if c == old else c): t for c, t in cats.items()}
    for c, topics in EDITS.items():
        assert c in cats, "EDITS names an unknown category %r" % c
        cats[c] = topics.split() + ["other"]
    check(cats)
    write(cats)
    print("%d categories, %d pairs" % (len(cats), sum(map(len, cats.values()))))


if __name__ == "__main__":
    main()
