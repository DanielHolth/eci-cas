"""Write the v512 shelf into src/EciCas.Host/instructions/cataloger.txt.

    python wire_v512.py

The vocabulary and the gloss block are generated from the two files in
docs/vocabulary/ rather than pasted, for the reason build_v512.py exists:
512 wrapped topics edited by hand loses one silently, and a gloss line that
loses its pair does not error -- the option simply goes out without its
words. Here both come from the same parse the bench measured.

Three of cataloger.txt's four sections are rewritten. `## topic` is not: it
is templated on {cat} and {topics} and does not know how big the shelf is.

The CATEGORY scope lines are the part that is written rather than derived,
and they earn their length -- without them "an appointment" split evenly
between two drawers from turn to turn, and a pair that moves is a pair
written twice and found once. Thirty-two of them is a longer prompt than
ten, which is a real cost on a 4B and the thing to watch first if filing
regresses.
"""
import io, sys, textwrap

ROOT = __file__.rsplit("tools", 1)[0]
DEST = ROOT + "src/EciCas.Host/instructions/cataloger.txt"
sys.path[:0] = [ROOT + "tools/retrieval-bench"]
from build_v512 import parse, GROUPS
from build_gloss import load_gloss

SCOPE = [
    ("identity", "who the user is on paper and in person: name, age, origin,"
                 " nationality, languages, pronouns, appearance"),
    ("character", "what the user is like: traits, temperament, humour, values,"
                  " principles, beliefs, fears, hopes"),
    ("mood", "how the user feels and has been feeling: stress, worry, joy,"
             " grief, anger, energy, motivation"),
    ("health", "the body and what is wrong with it: conditions, symptoms,"
               " injuries, allergies, diagnoses, recovery"),
    ("care", "treating health: medication, doctors, dentists, therapists,"
             " appointments, prescriptions, referrals"),
    ("fitness", "exercise and training: sport, gym, running, cycling,"
                " routines, goals, progress"),
    ("rest", "sleep and recovery: bedtime, waking, naps, dreams, insomnia,"
             " fatigue, winding down"),
    ("food", "what the user eats and drinks: diet, restrictions, dishes,"
             " recipes, cooking, meals, groceries"),
    ("home", "the dwelling itself: address, rooms, layout, garden,"
             " neighbourhood, moving, rent, ownership"),
    ("upkeep", "keeping the home working: repairs, maintenance, appliances,"
               " heating, plumbing, damp, contractors"),
    ("belongings", "things the user owns: furniture, decor, clothing, tools,"
                   " storage, valuables, gifts"),
    ("vehicle", "cars, bicycles and driving: model, registration, service,"
                " fuel, parking, the commute"),
    ("device", "physical hardware: phone, computer, tablet, camera, printer,"
               " router, chargers, faults"),
    ("digital", "accounts and data: logins, services, subscriptions, apps,"
                " files, photos, email, privacy"),
    ("family", "the family as people: partner, children, parents, siblings,"
               " relatives, in-laws"),
    ("social", "people known outside the family: friends, groups, neighbours,"
               " community, favours"),
    ("occasion", "dated social events: birthdays, anniversaries, weddings,"
                 " funerals, holidays, visits, gifts"),
    ("pet", "animals the user keeps: name, breed, feeding, vet, walks,"
            " behaviour, insurance"),
    ("work", "the post itself: employer, role, title, duties, team, contract,"
             " hours, notice, pay band"),
    ("workplace", "what the job is like day to day: culture, office,"
                  " colleagues, manager, shifts, leave, friction"),
    ("project", "pieces of work with an end: clients, tasks, milestones,"
                " blockers, deliverables, handover"),
    ("career", "where the job is going: ambitions, promotion, appraisals,"
               " applications, interviews, mentors"),
    ("learning", "education and skills being acquired: school, courses,"
                 " degrees, exams, practice, resources"),
    ("income", "money coming in: salary, payday, rate, bonus, pension,"
               " freelance, grants, payslips"),
    ("spending", "money going out: expenses, bills, subscriptions, purchases,"
                 " prices, budgets, refunds"),
    ("finance", "money held and owed: accounts, banks, cards, loans,"
                " mortgage, savings, investments, tax"),
    ("travel", "journeys and being away: trips, destinations, flights,"
               " accommodation, bookings, luggage, visas"),
    ("record", "documents that exist and can be produced on demand: passport,"
               " licence, certificates, contracts, permits, and where they"
               " are kept"),
    ("leisure", "what the user does for pleasure: hobbies, crafts, games,"
                " gardening, collecting, making, clubs"),
    ("media", "what the user reads, watches and listens to: books, film,"
              " television, music, podcasts, artists"),
    ("moment", "things that happened and are remembered: milestones,"
               " memories, anecdotes, firsts, losses, turning points"),
    ("plan", "what has not happened yet: intentions, wishes, someday ideas,"
             " things pending, deadlines, next steps"),
]

TIES = """Ties, decided once so they are decided the same way every time:
- A person the user knows goes in family or social. What goes in work and
  workplace is the job, not the people in it: a manager by name is social,
  a manager's behaviour is workplace.
- The terms of the job go in work. What the job is like to be in goes in
  workplace.
- What is earned goes in income, what is paid goes in spending, what is held
  or owed goes in finance. The paper proving any of them goes in record.
- A thing owned goes in belongings. The document that proves owning it goes
  in record.
- Hardware goes in device. What lives on the hardware goes in digital.
- A vehicle goes in vehicle. A journey made in it goes in travel.
- An animal goes in pet, including its illnesses. health is the body of the
  user and of nobody else.
- A skill already held goes in character. A skill being acquired goes in
  learning.
- An appointment goes with what it is for, never with when it is.
- Something that has already happened goes in moment. Something that has not
  happened yet goes in plan.
- A pastime the user DOES goes in leisure. One the user takes in goes in
  media."""

HEAD_NOTE = """#
# THE SHELF IS 512 PAIRS as of batch 20, up from 170. Thirty-two categories
# of sixteen topics, generated from docs/vocabulary/v512.txt and
# v512-gloss.txt by tools/retrieval-bench/wire_v512.py -- edit those and
# re-run, never this block. The reasoning for every category is in the
# header of v512.txt and the measurement is batch 20 of RESULTS.md: filed
# and read back against the shipped 170 at matched rows reached, 512 wins at
# every budget once its drawers have rows in them (+11.5pp at the cheap end,
# CI [+2.3, +20.7]) and is level cold. It gets there on a third of the rows
# per file, which is the facts-per-file mechanism that has decided every
# shelf question in the log.
#
# The gloss block below is no longer optional and batch 19 is why: with bare
# paths a twenty-sentence routing probe reads 10/20 and with glosses 18/20.
# `leisure` alone went 1 of 11 to 10 of 11 WITHOUT its topics changing. A
# bi-encoder compares a sentence to a sentence and a path is not one, so a
# pair with no gloss line is a drawer nothing reaches.
#
# The category scope lines are now thirty-two rather than ten. That is a
# longer prompt on a 4B and it is the first thing to look at if filing
# regresses."""


def section(text, name):
    """The `## name` block, header comments and all, from cataloger.txt."""
    for block in text.split("\n## ")[1:]:
        if block.split("\n", 1)[0].strip() == name:
            return "## " + block
    raise KeyError(name)


def main():
    cats = parse(ROOT + "docs/vocabulary/v512.txt")
    gloss = load_gloss()
    src = io.open(DEST, encoding="utf-8").read().replace("\r\n", "\n")

    head, rest = src.split("\n## ", 1)
    rest = "\n## " + rest
    if "THE SHELF IS 512 PAIRS" not in head:
        head = head.rstrip("\n") + "\n" + HEAD_NOTE

    vocab = [section(rest, "vocabulary").split("\n\n")[0].split("identity:")[0].rstrip()]
    for group, members in GROUPS:
        for c in members:
            wrapped = textwrap.wrap(" ".join(cats[c]), 74, subsequent_indent="  ",
                                    break_on_hyphens=False, break_long_words=False)
            vocab.append("%s: %s" % (c, wrapped[0]))
            vocab.extend(wrapped[1:])

    gl = [section(rest, "gloss").split("\n\n")[0].rstrip(), ""]
    n_gloss = 0
    for group, members in GROUPS:
        for c in members:
            for t in cats[c]:
                if t == "other":
                    continue
                p = "%s/%s" % (c, t)
                assert p in gloss, "no gloss for %s" % p
                gl.append("%s: %s" % (p, gloss[p]))
                n_gloss += 1
        gl.append("")

    cat = (section(rest, "category").split("\n\n")[0].rstrip()
           .replace("Ten drawers", "Thirty-two drawers")
           .replace("evenly between body and admin", "evenly between care and record"))
    body = [cat, ""]
    width = max(len(c) for c, _ in SCOPE) + 2
    for c, why in SCOPE:
        w = textwrap.wrap(why, 78 - width, subsequent_indent=" " * width)
        body.append("%-*s%s" % (width, c, w[0]))
        body.extend(w[1:])
    body += ["", TIES, "", "The message it was said in: {text}", "The fact: {fact}"]

    out = "\n".join([head.rstrip("\n"), "", "\n".join(vocab), "",
                     "\n".join(gl).rstrip("\n"), "", "\n".join(body), "",
                     section(rest, "topic").rstrip("\n")])
    io.open(DEST, "w", encoding="utf-8", newline="\n").write(out + "\n")
    print("%d categories, %d pairs, %d gloss lines -> %s"
          % (len(cats), sum(len(t) for t in cats.values()), n_gloss, DEST))


if __name__ == "__main__":
    main()
