# Appendix — the category/topic pairs, before and after

The file name is the index. One parquet file per `(Category, Topic)`, no
subject index, so this list is the whole address space the archive has: a
fact filed outside it is not findable, and a pair the Librarian is never
shown is not openable.

Two shelves are recorded here. **Shipped** is the vocabulary in
`src/EciCas.Host/instructions/cataloger.txt` and everything measured up to
batch 8. **Lean** is the consolidated shelf from
`tools/retrieval-bench/lean_vocab.py`, benchmarked but not shipped.

`other` closes every category on both shelves. It is write-side only: the
Cataloger may file into it, the Librarian is never shown it, and code opens
it beside its parent category. It carries no gloss on either shelf, by
design -- a definition would make it a folder that is about something, and
its whole job is to be the one that is not.

Counts include `other`. Gloss lines do not.

| shelf | categories | pairs | glossed |
|---|---|---|---|
| shipped | 10 | 170 | 160 |
| lean | 10 | 40 | 30 |

## Shipped — 170 pairs

### identity (18)

- `identity/name` — called, full name, spelling
- `identity/age` — how old, birthday year, born
- `identity/birth` — born, birthplace, date of birth
- `identity/origin` — from, grew up, hometown
- `identity/nationality` — citizen, passport country
- `identity/language` — speaks, fluent, mother tongue
- `identity/appearance` — looks, height, hair, glasses
- `identity/personality` — temperament, character, introvert
- `identity/belief` — thinks, opinion, worldview
- `identity/religion` — faith, church, practising
- `identity/politics` — votes, party, political
- `identity/value` — matters to, principle, cares about
- `identity/fear` — afraid, scared, phobia, terrified
- `identity/preference` — likes, prefers, favourite, hates
- `identity/allegiance` — supports, fan of, team loyalty
- `identity/habit` — always, usually, tends to
- `identity/humour` — jokes, finds funny
- `identity/other`

### body (18)

- `body/condition` — illness, chronic, suffers from, seasick
- `body/injury` — broke, sprained, hurt, accident
- `body/allergy` — allergic, reaction, intolerant
- `body/medication` — takes, prescription, pills, dose
- `body/diet` — eats, avoids, vegetarian, cannot eat
- `body/sleep` — bedtime, insomnia, hours slept
- `body/fitness` — exercise, training, gym, running
- `body/weight` — kilos, heavier, lost weight
- `body/vision` — eyesight, glasses, lenses
- `body/hearing` — deaf, hearing aid, ears
- `body/dental` — teeth, dentist, filling
- `body/mental` — anxiety, stress, therapy, mood
- `body/practitioner` — doctor, GP, specialist, clinic
- `body/appointment` — booked, seeing the doctor, scheduled
- `body/treatment` — surgery, therapy, course of
- `body/vaccination` — jab, vaccinated, booster
- `body/symptom` — pain, ache, feels, dizzy
- `body/other`

### household (18)

- `household/property` — house, flat, own, rent, mortgage
- `household/room` — kitchen, bedroom, upstairs
- `household/garden` — outside, lawn, plants, shed
- `household/furniture` — sofa, table, bed, wardrobe
- `household/appliance` — washing machine, dishwasher, heat pump, boiler, fridge
- `household/electronics` — tv, laptop, phone, router
- `household/vehicle` — car, drives, bike, van
- `household/tool` — drill, ladder, toolbox
- `household/maintenance` — serviced, installed, checked, upkeep
- `household/repair` — broken, leaks, fix, fault
- `household/cleaning` — tidy, hoover, laundry
- `household/utility` — power, water, heating, broadband
- `household/pet` — dog, cat, walks, feeds
- `household/plant` — watering, indoor plants
- `household/storage` — attic, cellar, boxes, keeps
- `household/decor` — paint, curtains, pictures
- `household/address` — lives at, where is, located, nearby, spare key
- `household/other`

### relations (17)

- `relations/family` — relatives, household, our family
- `relations/parent` — mother, father, mum, dad
- `relations/sibling` — brother, sister
- `relations/child` — son, daughter, kids
- `relations/partner` — wife, husband, spouse, girlfriend
- `relations/friend` — mate, best friend, pal
- `relations/colleague` — co-worker, workmate, manager, boss
- `relations/neighbour` — next door, up the road, nearby
- `relations/birthday` — turns, born in, birthday
- `relations/anniversary` — wedding, years together
- `relations/contact` — number, email, how to reach
- `relations/conflict` — fell out, argument, not speaking
- `relations/care` — looks after, helps, elderly
- `relations/gift` — present, bought for, wants
- `relations/visit` — drop by, staying with, coming over
- `relations/history` — passed away, used to, back then, died
- `relations/other`

### work (17)

- `work/employer` — works at, company, firm
- `work/role` — job title, position, works as
- `work/duty` — responsible for, handles, does
- `work/project` — working on, deadline, deliverable
- `work/schedule` — shifts, hours, days worked
- `work/location` — office, site, works from
- `work/commute` — gets to work, bus, train, drive in
- `work/meeting` — standup, review, calls
- `work/client` — customer, account
- `work/performance` — review, feedback, appraisal
- `work/promotion` — raise, promoted, step up
- `work/leave` — holiday days, sick leave, off
- `work/career` — years at, background, path
- `work/workplace` — the office itself, desk, building
- `work/tool` — software, system, uses at work
- `work/history` — used to work, previous job, how long
- `work/other`

### money (18)

- `money/income` — earns, comes in, revenue
- `money/salary` — paid, wage, monthly pay
- `money/payday` — paid on, the 15th, end of month
- `money/expense` — spends, costs, outgoings
- `money/bill` — invoice, utility bill, due
- `money/subscription` — monthly, streaming, membership fee
- `money/debt` — owes, credit card
- `money/loan` — borrowed, repayment
- `money/mortgage` — house loan, interest, term
- `money/saving` — saving for, put aside, rainy day
- `money/investment` — shares, fund, stocks
- `money/pension` — retirement, contributions
- `money/budget` — affordable, spending plan
- `money/purchase` — bought, buying, spend on
- `money/price` — costs, how much
- `money/tax` — tax return, deduction
- `money/benefit` — allowance, support payment
- `money/other`

### learning (15)

- `learning/school` — at school, pupil, class, taught
- `learning/university` — degree at, studied at, campus, uit, ntnu
- `learning/course` — enrolled, module, evening class
- `learning/subject` — field, discipline, studied what
- `learning/qualification` — masters, bachelor, degree in, diploma
- `learning/exam` — test, sat, results
- `learning/skill` — can do, learned to, good at
- `learning/language` — speaks, learning, fluent
- `learning/teacher` — tutor, lecturer, instructor
- `learning/study` — revising, reading up, learning
- `learning/book` — textbook, reading for study
- `learning/certification` — certified, licence to practise
- `learning/plan` — wants to study, applying
- `learning/progress` — getting on, halfway through
- `learning/other`

### leisure (18)

- `leisure/hobby` — does for fun, pastime, gave up
- `leisure/sport` — plays, runs, skis, swims
- `leisure/team` — supports, plays for, club side
- `leisure/music` — listens, sings, choir, band
- `leisure/instrument` — plays bass, guitar, piano
- `leisure/game` — board games, video games, chess
- `leisure/reading` — reads, novels, author, books
- `leisure/film` — watches, cinema, movies
- `leisure/television` — series, watches, streaming
- `leisure/cooking` — cooks, bakes, recipes
- `leisure/outdoor` — hiking, cabin trips, fishing
- `leisure/craft` — knitting, woodwork, making
- `leisure/club` — member of, meets, society
- `leisure/event` — concert, festival, going to
- `leisure/routine` — every week, on Wednesdays, before breakfast, regularly
- `leisure/social` — meets up, pub, friends round
- `leisure/collection` — collects, owns a set of
- `leisure/other`

### travel (15)

- `travel/trip` — went to, going to, journey
- `travel/destination` — place visited, where to
- `travel/accommodation` — hotel, cabin, stays at
- `travel/flight` — flies, airline, airport
- `travel/transport` — train, ferry, boat, car hire, bus
- `travel/booking` — booked, reserved, confirmed
- `travel/plan` — planning to go, itinerary
- `travel/visit` — visiting, seeing people, dropping in
- `travel/holiday` — summer holiday, break, vacation
- `travel/luggage` — packs, suitcase, bag
- `travel/companion` — travels with, goes with
- `travel/experience` — loved it, what happened there
- `travel/preference` — likes travelling by, avoids
- `travel/frequency` — often, once a year, rarely
- `travel/other`

### admin (16)

- `admin/passport` — passport, travel document
- `admin/licence` — driving licence, permit
- `admin/insurance` — policy, covered, premium
- `admin/contract` — signed, agreement, terms
- `admin/account` — login, bank account, profile
- `admin/membership` — member of, subscription to
- `admin/registration` — registered, enrolled, on file
- `admin/warranty` — guarantee, covered until
- `admin/certificate` — certificate, official document
- `admin/renewal` — expires, expiry, renew, runs out, valid until, needs renewing
- `admin/deadline` — due by, must be done by, cut-off
- `admin/provider` — supplier, company used, switched to
- `admin/will` — testament, inheritance
- `admin/document` — papers, forms, filed
- `admin/reference` — reference number, case id
- `admin/other`

## Lean — 40 pairs

Each category keeps three real folders and `other`. The names are
abstract where the shipped ones were concrete, which is the whole reason the
gloss matters more here than it did there: `passport` is already the word a
question uses, `document` is not.

### identity (4)

- `identity/self` — name, age, born, from, nationality, looks
- `identity/belief` — thinks, faith, politics, values, principles
- `identity/trait` — afraid of, likes, prefers, habit, personality, speaks
- `identity/other`

### body (4)

- `body/health` — illness, allergic, injury, pain, eyesight, teeth, seasick
- `body/care` — doctor, medication, appointment, treatment, jab
- `body/habit` — eats, sleeps, exercise, diet, avoids
- `body/other`

### household (4)

- `household/home` — house, flat, lives at, room, garden, pet, spare key, located
- `household/thing` — car, dishwasher, boiler, heat pump, furniture, laptop, owns
- `household/upkeep` — broken, leaks, serviced, installed, repair, bills, cleaning
- `household/other`

### relations (4)

- `relations/person` — brother, sister, daughter, wife, friend, neighbour, colleague, manager
- `relations/event` — birthday, anniversary, visit, present, turns
- `relations/history` — passed away, died, fell out, used to, back then
- `relations/other`

### work (4)

- `work/job` — does, responsible for, project, shifts, meetings, role
- `work/employer` — works at, company, office, commute, gets to work
- `work/history` — years at, used to work, promoted, leave, career
- `work/other`

### money (4)

- `money/income` — paid, salary, earns, payday, allowance
- `money/spending` — costs, bill, bought, subscription, budget, tax
- `money/asset` — saving for, mortgage, loan, owes, pension, investment
- `money/other`

### learning (4)

- `learning/education` — studied, degree, university, school, course, exam, uit, ntnu
- `learning/skill` — can do, good at, speaks, learned to, fluent
- `learning/plan` — wants to study, applying, halfway through
- `learning/other`

### leisure (4)

- `leisure/activity` — plays, sings, runs, cooks, hobby, every week, on Wednesdays, gave up
- `leisure/media` — reads, novels, author, watches, series, film
- `leisure/social` — club, member, meets up, concert, going to
- `leisure/other`

### travel (4)

- `travel/trip` — went to, going to, holiday, visiting, Lofoten
- `travel/plan` — booked, hotel, cabin, flight, ferry, bus, packs, travels with
- `travel/preference` — likes travelling by, avoids, how often
- `travel/other`

### admin (4)

- `admin/document` — passport, licence, certificate, papers, registered, reference
- `admin/policy` — insurance, contract, account, membership, supplier, warranty
- `admin/deadline` — expires, expiry, renew, runs out, due by, valid until
- `admin/other`

## Status

The lean shelf is a benchmark artifact, not a decision. It is measured on
the read side (batches 3-7) and has no write-side filing number yet: batch 8
established that baseline on the shipped shelf only, and the comparison it
was built for -- the same scorer against the merged shelf, filed both with
and without the gloss -- has not been run.

Nor is 40 necessarily the final number. The standing intent is a painfully
low pool, and this shelf is the first cut at one.
