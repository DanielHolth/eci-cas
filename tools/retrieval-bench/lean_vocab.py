# The consolidated vocabulary: 170 pairs down to 40.
#
# Why. The v3 baseline is category 79%, select 36%. The reader almost always
# knows the drawer and then loses a coin-flip between two or three folders in
# it that mean nearly the same thing -- licence/renewal/certificate/deadline,
# appliance/repair/tool/maintenance, course/university/qualification/subject.
# Those are not distinctions a 4B can hold on one-word names, and every one
# of them is a way for the writer and the reader to disagree.
#
# So: three real topics per category and the "other" valve, chosen so that
# two topics in the same category are never plausible answers to the same
# question. Where a merge had to lose something, it lost precision inside a
# folder rather than adding a folder -- Recall reads the whole row, so a
# coarse pair with the right row in it beats a precise pair nobody opens.
#
# The ten categories are deliberately untouched. `select_cat` is the anchor
# that makes a lean run comparable to the baseline at all; moving both levels
# at once would leave nothing fixed to measure against.
#
# The obvious cost, stated up front so the numbers are read honestly: 40
# pairs means ~5 rows per pair instead of ~1.5 at this corpus size, and every
# opened file hands Recall more to sift. If `select` rises and `answer` does
# not, that is where it went, and the fix is Recall's, not the vocabulary's.
LEAN = {
 # name age birth origin nationality appearance | belief religion politics
 # value | language fear preference allegiance habit humour personality
 "identity": ["self", "belief", "trait", "other"],
 # condition injury allergy symptom vision hearing dental mental weight |
 # medication practitioner appointment treatment vaccination | diet sleep fitness
 "body": ["health", "care", "habit", "other"],
 # property room address garden storage pet | furniture appliance electronics
 # vehicle tool decor plant | maintenance repair cleaning utility
 "household": ["home", "thing", "upkeep", "other"],
 # family parent sibling child partner friend colleague neighbour contact care |
 # birthday anniversary gift visit | conflict history
 "relations": ["person", "event", "history", "other"],
 # role duty project schedule meeting client tool performance |
 # employer location workplace commute | promotion leave career history
 "work": ["job", "employer", "history", "other"],
 # income salary payday benefit | expense bill subscription purchase price
 # budget tax | debt loan mortgage saving investment pension
 "money": ["income", "spending", "asset", "other"],
 # school university course subject qualification exam teacher book
 # certification study | skill language | plan progress
 "learning": ["education", "skill", "plan", "other"],
 # hobby sport team instrument music cooking outdoor craft game collection
 # routine | reading film television | club event social
 "leisure": ["activity", "media", "social", "other"],
 # trip destination holiday experience visit | accommodation flight transport
 # booking luggage plan companion | preference frequency
 "travel": ["trip", "plan", "preference", "other"],
 # passport licence certificate registration document reference will |
 # insurance contract account membership provider warranty | renewal deadline
 "admin": ["document", "policy", "deadline", "other"],
}

# Same contract as topic_gloss.GLOSS, in the question's vocabulary. A merged
# folder needs this more than a narrow one did: "thing" is not a word anybody
# would put in a question, so the name alone carries less than "appliance"
# did. The lean arm is therefore run both ways -- bare and glossed -- because
# consolidation and gloss could easily be the same win counted twice.
LEAN_GLOSS = {
 "identity": {
  "self": "name, age, born, from, nationality, looks",
  "belief": "thinks, faith, politics, values, principles",
  "trait": "afraid of, likes, prefers, habit, personality, speaks",
 },
 "body": {
  "health": "illness, allergic, injury, pain, eyesight, teeth, seasick",
  "care": "doctor, medication, appointment, treatment, jab",
  "habit": "eats, sleeps, exercise, diet, avoids",
 },
 "household": {
  "home": "house, flat, lives at, room, garden, pet, spare key, located",
  "thing": "car, dishwasher, boiler, heat pump, furniture, laptop, owns",
  "upkeep": "broken, leaks, serviced, installed, repair, bills, cleaning",
 },
 "relations": {
  "person": "brother, sister, daughter, wife, friend, neighbour, colleague, manager",
  "event": "birthday, anniversary, visit, present, turns",
  "history": "passed away, died, fell out, used to, back then",
 },
 "work": {
  "job": "does, responsible for, project, shifts, meetings, role",
  "employer": "works at, company, office, commute, gets to work",
  "history": "years at, used to work, promoted, leave, career",
 },
 "money": {
  "income": "paid, salary, earns, payday, allowance",
  "spending": "costs, bill, bought, subscription, budget, tax",
  "asset": "saving for, mortgage, loan, owes, pension, investment",
 },
 "learning": {
  "education": "studied, degree, university, school, course, exam, uit, ntnu",
  "skill": "can do, good at, speaks, learned to, fluent",
  "plan": "wants to study, applying, halfway through",
 },
 "leisure": {
  "activity": "plays, sings, runs, cooks, hobby, every week, on Wednesdays, gave up",
  "media": "reads, novels, author, watches, series, film",
  "social": "club, member, meets up, concert, going to",
 },
 "travel": {
  "trip": "went to, going to, holiday, visiting, Lofoten",
  "plan": "booked, hotel, cabin, flight, ferry, bus, packs, travels with",
  "preference": "likes travelling by, avoids, how often",
 },
 "admin": {
  "document": "passport, licence, certificate, papers, registered, reference",
  "policy": "insurance, contract, account, membership, supplier, warranty",
  "deadline": "expires, expiry, renew, runs out, due by, valid until",
 },
}
