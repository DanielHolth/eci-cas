# Retrieval corpus, v3 -- the read-side instrument.
#
# Why a third corpus. v2 is 12 statements against a +/-10pp noise floor, and
# its HELD half was scored repeatedly as the design moved. Every read result
# in docs/roadmap.md except the -35pp hierarchical loss is smaller than the
# floor of the instrument that produced it: "identical", "+0%" and "0 for 4"
# are undetectable, not disproven. This corpus exists to make the read
# numbers falsifiable.
#
# Written before any of it was run, and not revised against a score. When it
# is spent, write v4 rather than editing this.
#
# Three things v2 could not measure:
#
#   1. Volume. 24 statements, 35 direct questions -- twice v2's 17. A 10pp
#      difference is three or four questions here. Still not comfortable;
#      reps are what close the rest of the gap.
#   2. Distraction with content. The padding pairs in the v2 measurements
#      were empty files, so a wrong pick returned nothing and the number was
#      an upper bound. The statements below spread over many pairs on
#      purpose, and read_bench populates the rest, so a wrong pick returns
#      something plausible instead.
#   3. Oblique questions -- see OBLIQUE.

# (statement, [questions answerable from it]).
STATEMENTS = [
 ("My daughter Ingrid turns seven in November",       ["When is Ingrid's birthday?", "How old is my daughter?"]),
 ("We drive a silver Skoda Octavia",                  ["What car do we drive?", "What colour is our car?"]),
 ("I am allergic to penicillin",                      ["Am I allergic to anything?", "Can I take penicillin?"]),
 ("Our cabin is on Senja, about an hour from Finnsnes", ["Where is our cabin?", "How far is the cabin from Finnsnes?"]),
 ("I sing tenor in the church choir on Wednesdays",   ["What do I do on Wednesdays?", "Do I sing?"]),
 ("My colleague Ingvild handles the invoices",        ["Who handles the invoices?"]),
 ("The heat pump was installed in 2021",              ["When was the heat pump installed?"]),
 ("I did my masters in marine biology at UiT",        ["What did I study?", "Where did I study?"]),
 ("My driving licence needs renewing in 2028",        ["When does my licence expire?"]),
 ("We walk the dog before breakfast",                 ["When do we walk the dog?", "Do we have a dog?"]),
 ("I get seasick on anything smaller than a ferry",   ["Do I get seasick?"]),
 ("My sister Marit lives in Bodo with her husband",   ["Where does Marit live?", "Do I have siblings?"]),
 ("I have been at Statkraft for eleven years",        ["Where do I work?", "How long have I worked there?"]),
 ("We are saving for a kitchen renovation",           ["What are we saving for?"]),
 ("Ingrid is scared of thunder",                      ["What is Ingrid scared of?"]),
 ("I read crime novels, mostly Nesbo",                ["What do I read?", "Who is my favourite author?"]),
 ("Our neighbour Bjorn keeps the spare key",          ["Who has our spare key?"]),
 ("I take the 07:20 bus into town",                   ["How do I get to work?", "What time is my bus?"]),
 ("My father passed away in 2019",                    ["Is my father alive?"]),
 ("We booked Lofoten for the summer holiday",         ["Where are we going on holiday?"]),
 ("I gave up smoking four years ago",                 ["Do I smoke?"]),
 ("The dishwasher leaks if you overload it",          ["What is wrong with the dishwasher?"]),
 ("I studied German at school but have forgotten most of it", ["What languages do I know?"]),
 ("We rent out the basement flat to a student",       ["Do we rent anything out?"]),
]

# Questions that do not name the fact they need. The system is not expected
# to answer these; the score is a separate bonus tier and must never be
# folded into the headline number.
#
# They are here because the failure they probe is the diagnosed one: the
# right drawer is not guessable from the question (the -35pp hierarchical
# result). A question that names a place and wants a person is the case a
# subject index would have to earn its keep on.
#
# (question, [pairs that would be a good open], [tokens that would answer it])
OBLIQUE = [
 ("Who should we drop by and visit while we are staying in Bodo?",
  ["relations/family", "relations/sibling", "relations/other"], ["marit"]),
 ("Anything I should tell the doctor before they prescribe something?",
  ["health/allergy", "health/condition", "health/other"], ["penicillin"]),
 ("Is there anyone nearby I can ask to let the plumber in?",
  ["relations/neighbour", "relations/other"], ["bjorn"]),
 ("What should I not do on the boat trip?",
  ["health/condition", "health/other"], ["seasick"]),
 ("Will I need to renew any documents before the trip?",
  ["identity/document", "identity/other"], ["licence", "2028"]),
 ("What could we spend the extra money on?",
  ["finance/saving", "household/renovation", "finance/other"], ["kitchen"]),
 ("Something to keep Ingrid calm during the storm tonight?",
  ["relations/family", "identity/fear"], ["thunder"]),
 ("Anything to be careful about when loading up after dinner?",
  ["household/appliance", "household/other"], ["dishwasher"]),
]

# States no fact. Anything archived from these is a fabrication; anything
# selected for them is a wasted open.
NULLS = ["morning", "that makes sense", "could you say that again?",
         "no worries", "what are you up to?", "ha, fair enough",
         "right", "still there?"]
