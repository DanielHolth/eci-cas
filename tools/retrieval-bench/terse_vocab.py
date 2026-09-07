"""The terse shelf: 170 pairs down to 34, eight categories.

Seven categories cut by SUBJECT and one, `appointment`, cut by TIME. That is
deliberate and it is the only orthogonal drawer: "future and committed"
changes what you do with a fact -- it can be missed, it needs surfacing
before it happens, it expires -- where location, for instance, does not.
Place is a value, not an address, so there is no location drawer.

What merged and why:

  body/care -> appointment, relations
      appointment/booked took the visits and relations/person took the
      practitioner, which left care hollow. body/state fills the gap the
      split opened: health is what is wrong, habit is what you do, state is
      how you are doing -- the register a companion actually hears.

  admin -> records
      Renamed, not merged. admin is a mood and moods attract; records is a
      claim about the row's form -- a formal record, held by someone else,
      with an identifier -- which is a test the writer can apply. It had
      already lost its verbs to appointment/deadline.

  money + household -> resources   household/thing and money/asset were
      near-duplicates; a car was both and the writer had no rule to choose.
      Named `resources` and not `property`, which reads as real estate and
      collides with admin/document.

  travel -> leisure/travel        travel was a category holding three topics
      that were near-ties with leisure's. It keeps a drawer, not a category:
      its pull toward records (bookings, passports) is real and merging does
      not remove it, it stops pretending travel owns it.

  leisure/social -> relations, leisure/pastime
      A club is a pastime and the people in it are relations. Same shape as
      the records rule: the thing, and the people or paperwork about it.
      `activity` was killed before it bloated -- it was a near-synonym for
      hobby and an attractor by name, and promoting it to a category would
      only have renamed leisure and left the same topic question.

  no leisure/preference          it collides with identity/trait, which
      already holds likes and prefers. A cross-category tie is the expensive
      kind: the reader opens whole categories.

  learning -> work, leisure, identity
      The opposite operation to the merges above: this SPLITS a drawer whose
      contents genuinely differ, so it creates ties instead of removing one.
      It only works with the rule stated -- see TIES.

  admin/deadline + body/appointment + work/schedule -> appointment
      Not a new category so much as a re-cut of three, on the time axis.

The gloss matters more here than it did on the shipped shelf. `passport` is
already the word a question uses; `document` is not. Batch 8 measured the
gloss at zero on 170 concrete pairs, which is the baseline this shelf gets
read against: a gain here cannot be the gloss being generally useful,
because there it was not.

`other` closes every category and carries no gloss, on purpose -- a
definition would make it a folder that is about something, and its job is to
be the one that is not. Write-side only, as on every shelf.
"""

TERSE = {
    "identity":    ["self", "belief", "trait", "history", "other"],
    "body":        ["health", "state", "habit", "other"],
    "relations":   ["family", "person", "agent", "other"],
    "work":        ["job", "employer", "plan", "other"],
    "resources":   ["home", "thing", "upkeep", "money", "other"],
    "leisure":     ["travel", "pastime", "media", "other"],
    "records":     ["document", "policy", "account", "other"],
    "appointment": ["booked", "deadline", "event", "other"],
}

# Same contract as topic_gloss.GLOSS and lean_vocab.LEAN_GLOSS: the words are
# in the QUESTION's vocabulary, not the folder's. A gloss that explains a
# topic to a librarian is worthless; both stages are matching a turn against
# a name, so the gloss has to contain the words the turn would use.
TERSE_GLOSS = {
    "identity": {
        "self":    "name, called, age, born, from, nationality, looks like",
        "belief":  "believes, faith, religion, politics, values, principles",
        "trait":   "afraid of, likes, prefers, personality, speaks, habit",
        "history": "studied, grew up, used to, childhood, became, back then",
    },
    "body": {
        "health": "illness, condition, allergy, cannot eat, injury, medication, "
                  "symptom, blood type, eyesight, how tall",
        "state":  "tired, exhausted, stressed, anxious, low, run down, in pain, "
                  "recovering, sleeping badly, feeling, energy, mood",
        "habit":  "eats, diet, sleep, exercise, training, fitness, weight",
    },
    "relations": {
        "family": "wife, husband, partner, daughter, son, brother, sister, parent, "
                  "mother, father, cousin, in-law, birthday, anniversary",
        "person": "friend, colleague, manager, boss, neighbour, doctor, teacher, "
                  "who is, knows, met",
        "agent":  "assistant, companion, bot, model, agent, morrow, uses to help",
    },
    "work": {
        "job":      "job, role, works as, does for a living, duties, qualified, trained as",
        "employer": "company, works at, office, workplace, commute, employer, client",
        "plan":     "project, deadline at work, career, promotion, leave, studying for, course",
    },
    "resources": {
        "home":   "house, flat, address, lives at, room, kitchen, garden, cabin, "
                  "kept in, stored in, where it is",
        "thing":  "car, drives, bike, phone, laptop, appliance, boiler, tool, pet, dog, cat, owns",
        "upkeep": "serviced, repaired, fixed, replaced, cleaned, maintenance, broke",
        "money":  "earns, salary, income, pays, bill, cost, price, rent, debt, loan, "
                  "saving, pension, budget",
    },
    "leisure": {
        "travel":  "trip, holiday, flying to, destination, abroad, cabin, RV, "
                   "goes to, been to, wants to visit",
        "pastime": "hobby, plays, sport, runs, hikes, cooks, makes, builds, band, "
                   "club, team, choir, member of, every week",
        "media":   "music, listens, film, watches, series, book, reads, game, collects",
    },
    "records": {
        "document": "passport, licence, certificate, warranty, will, deed, "
                    "reference number, paperwork",
        "policy":   "insurance, contract, membership, subscription, cover, terms, plan with",
        "account":  "account with, provider, supplier, registered with, utility, "
                    "customer number, signed up",
    },
    "appointment": {
        "booked":   "appointment, booked, sees the, at 2pm, on tuesday, scheduled, slot",
        "deadline": "due, expires, by the end of, must be done, runs out, renew by, last day",
        "event":    "going to, flying to, wedding in, concert, coming up, next month, trip to",
    },
}

# The rules a merge cannot carry on its own. Each one is a tie the writer will
# meet on this shelf and cannot resolve from the names, which is what
# cataloger.txt already does for the manager case: a tie rule nothing
# enforces is a comment.
TIES = """\
Learning goes by what it was for. Learned for a job goes in work. Learned for
yourself goes in leisure. What you studied and became goes in identity.

A dated fact goes by whether it has happened. Future and committed goes in
appointment. Past and done goes in the drawer for its subject: a boiler
serviced in September is upkeep, a boiler service booked for September is an
appointment.

A recurring date is not an appointment. A birthday belongs to the person it
is for, in relations, because it never expires and an appointment does. The
appointment is derived from it each year, not stored.

A thing goes in resources. The paperwork about it goes in records. The date
it must be renewed by goes in appointment. A car is resources/thing, its
insurance is records/policy, its MOT is appointment/deadline.

An agent is a relation, not a tool. Assistants and companions, the user's own
and other people's, go in relations.

A place is a value, not a folder. File the fact by what kind of fact it is
and let the place sit in the row.
"""


# One line per drawer, in the same register as cataloger.txt's shipped block.
# The category call needs prose, not a bare list: the shipped prompt proves it
# -- the drawer names there carry a sentence each, and the ties below them.
CATS = {
    "identity":    "who the user is: name, age, origin, languages, beliefs,\n"
                   "           values, fears, tastes, personality, and what they used\n"
                   "           to be or studied to become",
    "body":        "the body: illness, conditions, allergies, injuries,\n"
                   "           medication, and how the user is doing -- tired, stressed,\n"
                   "           recovering -- plus diet, sleep and exercise",
    "relations":   "people the user knows as people: family, partner,\n"
                   "           friends, colleagues and professionals by name, the dates\n"
                   "           that belong to a person, and assistants or companions",
    "work":        "the job itself: role, duties, employer, workplace,\n"
                   "           commute, projects, career and what is being studied for it",
    "resources":   "what the user has: the home and where things are kept,\n"
                   "           possessions and vehicles and pets, their upkeep and repairs,\n"
                   "           and money in and out",
    "leisure":     "time off: trips and holidays, hobbies and sport and\n"
                   "           clubs, and what is watched, read, listened to or played",
    "records":     "formal records someone else holds: passport, licence,\n"
                   "           certificate, insurance, contract, membership, account and\n"
                   "           reference numbers",
    "appointment": "what has not happened yet and is committed to: booked\n"
                   "           slots, deadlines and dates that expire, and events the user\n"
                   "           is going to",
}


def category_prompt():
    """The shipped category prompt's shape, built from this shelf.

    bench.file_fact takes a `vocab` override for the topic list but the
    category prompt was a hardcoded prose block naming the shipped ten
    drawers. Lean survived that only because it kept those ten names; the
    first shelf to rename one filed 18 of 31 rows to unfiled/unfiled. So the
    drawers and their ties are generated here, from the same TIES the docs
    and cataloger.txt will carry.
    """
    drawers = "\n".join("%-10s %s" % (c, CATS[c]) for c in TERSE)
    ties = "\n".join("- " + " ".join(p.split())
                     for p in TIES.strip().split("\n\n"))
    return ("Eight drawers of a filing cabinet, and a fact someone stated. Reply "
            "with one\ndrawer name and nothing else.\n\n" + drawers +
            "\n\nTies, decided once so they are decided the same way every time:\n"
            + ties + "\n\nThe message it was said in: {text}\nThe fact: {fact}")
