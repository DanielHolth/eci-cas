# Retrieval corpus, v4 -- the instrument for the vocabulary question.
#
# Why a fourth corpus. v3 made the read numbers falsifiable and is now spent:
# 24 statements over 170 pairs is 1.7 rows a file, and three separate open
# questions all reduce to "this archive is too thin to tell".
#
#   1. File fatness. Every shelf result in the log is explained by
#      facts-per-file, and no file in v3 is fat. The regime Recall cannot
#      afford has never been inside the instrument that measures Recall.
#   2. The sentence column's row cost. Batch 13 measured 1.21 -> 1.06 rows
#      per statement with sufficiency flat, so whatever vanished answered
#      none of the 35 questions v3 has. Whether that was marginal restatement
#      or facts nobody happens to ask about today is not answerable with 35
#      questions.
#   3. The vocabulary itself. Batches 3-12 compared shelves to each other and
#      never to the absence of one. A flat cosine sweep is now a live
#      alternative to Cataloger and Librarian, and at 1.7 rows a file
#      everything is near everything: flat search would win or lose for
#      reasons that are not about flat search.
#
# Written before any of it was run, and not revised against a score. Same
# rule as v3 -- when it is spent, write v5 rather than editing this.
#
# Hand-written, deliberately. read_bench generates the padding because
# padding is distractor material, but statements a model wrote and a model
# then extracts share an idiom, and every extraction number would be
# flattered by it. Writing these by hand is the price of that independence.
#
# Three things v3 does not have:
#
#   * Near-miss clusters. The statements are grouped below so that unrelated
#     facts share surface: several things that expire, four that touch Bodo,
#     four dated 2021, a week's worth of weekday routines. A question naming
#     one of them has to be discriminated from its neighbours by meaning
#     rather than by being the only row containing that word. Without this
#     the flat arm faces no pressure and the result says nothing.
#   * A null set sized to matter. v3 has 8 against 35 and scores them apart
#     from the headline, which grades every arm on a curve that rewards
#     guessing: an arm keeping everything takes full credit for its recall
#     and pays nothing for volunteering facts nobody asked about. Below is
#     roughly a third of the question count.
#   * Enough statements that the fat end of the file distribution is reached
#     by real rows and not by padding alone.
#
# Nothing here names a pair. v3 learned that hand-guessed pairs score zero
# for reasons that have nothing to do with retrieval; where a fact is filed
# is the Cataloger's business and the harness looks it up.

# (statement, [questions answerable from it]).
# Ordered by cluster rather than by category, so the near-misses are visible
# to someone reading the file.
STATEMENTS = [

 # -- expires, renews, runs out --------------------------------------------
 ("My passport runs out in March 2027",                 ["When does my passport expire?"]),
 ("My driving licence needs renewing in 2028",          ["When does my licence need renewing?"]),
 ("The house insurance renews every March",             ["When does the house insurance renew?"]),
 ("Our gym membership auto-renews in January",          ["When does the gym membership renew?"]),
 ("The car warranty ran out last autumn",               ["Is the car still under warranty?"]),
 ("Ingrid's passport is valid until 2031",              ["When does Ingrid's passport expire?"]),

 # -- Bodo ------------------------------------------------------------------
 ("My sister Marit lives in Bodo with her husband",     ["Where does Marit live?", "Do I have siblings?"]),
 ("I fly to Bodo for work about once a month",          ["How often do I travel for work?"]),
 ("We stayed at the Scandic in Bodo last Easter",       ["Where did we stay in Bodo?"]),
 ("The Bodo office handles our northern contracts",     ["Which office handles the northern contracts?"]),

 # -- 2021 ------------------------------------------------------------------
 ("The heat pump was installed in 2021",                ["When was the heat pump installed?"]),
 ("We repainted the house in 2021",                     ["When did we repaint the house?"]),
 ("I was promoted to senior engineer in 2021",          ["When was I promoted?"]),
 ("Our dog Nella was born in 2021",                     ["When was Nella born?"]),

 # -- the week --------------------------------------------------------------
 ("I sing tenor in the church choir on Wednesdays",     ["What do I do on Wednesdays?", "Do I sing?"]),
 ("Ingrid has handball training on Tuesdays",           ["When does Ingrid have handball?"]),
 ("The bins go out on Thursday night",                  ["When do the bins go out?"]),
 ("We walk the dog before breakfast",                   ["When do we walk the dog?"]),
 ("I work from home on Fridays",                        ["Which day do I work from home?"]),

 # -- health ----------------------------------------------------------------
 ("I am allergic to penicillin",                        ["Am I allergic to anything?", "Can I take penicillin?"]),
 ("Ingrid is allergic to hazelnuts",                    ["Is Ingrid allergic to anything?"]),
 ("I take blood pressure tablets each morning",         ["What medication do I take?"]),
 ("I get seasick on anything smaller than a ferry",     ["Do I get seasick?"]),
 ("My back goes if I lift badly",                       ["Do I have any back trouble?"]),
 ("I gave up smoking four years ago",                   ["Do I smoke?"]),
 ("The dentist wants me back in six months",            ["When is my next dental appointment?"]),

 # -- people who do things for us -------------------------------------------
 ("Our neighbour Bjorn keeps the spare key",            ["Who has our spare key?"]),
 ("My colleague Ingvild handles the invoices",          ["Who handles the invoices?"]),
 ("Astrid next door feeds the cat when we travel",      ["Who feeds the cat when we are away?"]),
 ("A plumber called Rune did the bathroom",             ["Who did our bathroom?"]),
 ("My manager is called Henrik",                        ["Who is my manager?"]),

 # -- the house -------------------------------------------------------------
 ("The dishwasher leaks if you overload it",            ["What is wrong with the dishwasher?"]),
 ("The downstairs toilet runs unless you jiggle it",    ["What is wrong with the downstairs toilet?"]),
 ("We rent out the basement flat to a student",         ["Do we rent anything out?"]),
 ("The loft is full of Christmas decorations",          ["What is in the loft?"]),
 ("Our address is Bjerkeveien 14",                      ["What is our address?"]),
 ("The garage door sticks in cold weather",             ["What is wrong with the garage door?"]),
 ("We have a woodburner in the living room",            ["How do we heat the living room?"]),

 # -- vehicles --------------------------------------------------------------
 ("We drive a silver Skoda Octavia",                    ["What car do we drive?", "What colour is our car?"]),
 ("The winter tyres go on in November",                 ["When do the winter tyres go on?"]),
 ("I take the 07:20 bus into town",                     ["What time is my bus?"]),
 ("Ingrid has a red bicycle with a broken bell",        ["What colour is Ingrid's bicycle?"]),

 # -- work ------------------------------------------------------------------
 ("I have been at Statkraft for eleven years",          ["Where do I work?", "How long have I worked there?"]),
 ("I am a hydrology engineer",                          ["What is my job?"]),
 ("Payday is the fifteenth",                            ["When is payday?"]),
 ("I have twelve days of leave left this year",         ["How much leave do I have left?"]),
 ("The Monday stand-up is at half eight",               ["When is the Monday stand-up?"]),
 ("I did my masters in marine biology at UiT",          ["What did I study?", "Where did I study?"]),

 # -- money -----------------------------------------------------------------
 ("We are saving for a kitchen renovation",             ["What are we saving for?"]),
 ("The mortgage is with Sparebank 1",                   ["Who is our mortgage with?"]),
 ("We pay 340 kroner a month for the streaming bundle", ["What do we pay for streaming?"]),
 ("My pension is with KLP",                             ["Where is my pension?"]),
 ("The electricity bill doubled last winter",           ["What happened to the electricity bill?"]),

 # -- family ----------------------------------------------------------------
 ("My daughter Ingrid turns seven in November",         ["When is Ingrid's birthday?", "How old is my daughter?"]),
 ("My father passed away in 2019",                      ["Is my father alive?"]),
 ("My mother still lives in the house I grew up in",    ["Where does my mother live?"]),
 ("My wife Kristin teaches at the upper secondary",     ["What does Kristin do?"]),
 ("Ingrid is scared of thunder",                        ["What is Ingrid scared of?"]),
 ("Kristin and I married in Trondheim",                 ["Where did we get married?"]),

 # -- leisure ---------------------------------------------------------------
 ("I read crime novels, mostly Nesbo",                  ["What do I read?", "Who is my favourite author?"]),
 ("I play badminton on Sunday mornings",                ["What sport do I play?"]),
 ("We have a cabin on Senja, an hour from Finnsnes",    ["Where is our cabin?", "How far is the cabin from Finnsnes?"]),
 ("I brew beer in the garage twice a year",             ["What do I brew?"]),
 ("Kristin does pottery at the community centre",       ["What hobby does Kristin have?"]),
 ("I follow Rosenborg but rarely go to matches",        ["Which football team do I follow?"]),

 # -- travel ----------------------------------------------------------------
 ("We booked Lofoten for the summer holiday",           ["Where are we going on holiday?"]),
 ("I will not fly with a stopover under an hour",       ["What is my rule about stopovers?"]),
 ("We always take the night train south",               ["How do we travel south?"]),
 ("Ingrid gets carsick on mountain roads",              ["Does Ingrid get carsick?"]),

 # -- learning --------------------------------------------------------------
 ("I studied German at school but have forgotten most of it", ["What languages do I know?"]),
 ("I am halfway through an online course on hydrology modelling", ["What course am I taking?"]),
 ("Ingrid started piano lessons in August",             ["When did Ingrid start piano?"]),
 ("Kristin is learning Spanish from an app",            ["What language is Kristin learning?"]),

 # -- preferences and small truths ------------------------------------------
 ("I cannot stand coriander",                           ["What food do I dislike?"]),
 ("I take my coffee black",                             ["How do I take my coffee?"]),
 ("We eat fish on Fridays",                             ["What do we eat on Fridays?"]),
 ("I am hopeless with names",                           ["What am I bad at?"]),
 ("I prefer text messages to phone calls",              ["How should people contact me?"]),
]

# Questions that do not name the fact they need. Never folded into the
# headline -- see v3. Kept because the failure they probe is the diagnosed
# one: the right drawer is not guessable from the question.
#
# (question, the statement that answers it, alternatives as in the answer key)
OBLIQUE = [
 ("Who could we drop by and see while we are up in Bodo?",
  "My sister Marit lives in Bodo with her husband",     [("marit",)]),
 ("Anything the doctor should know before prescribing?",
  "I am allergic to penicillin",                        [("penicillin",)]),
 ("Is there anyone nearby who could let the plumber in?",
  "Our neighbour Bjorn keeps the spare key",            [("bjorn",)]),
 ("What should I watch out for on the boat trip?",
  "I get seasick on anything smaller than a ferry",     [("seasick",)]),
 ("Will I need to sort out any documents before the summer?",
  "My passport runs out in March 2027",                 [("passport",), ("2027",)]),
 ("What could we put the extra money towards?",
  "We are saving for a kitchen renovation",             [("kitchen",)]),
 ("Anything that might keep Ingrid settled during the storm?",
  "Ingrid is scared of thunder",                        [("thunder",)]),
 ("Anything to be careful about when clearing up after dinner?",
  "The dishwasher leaks if you overload it",            [("dishwasher",), ("leak",), ("overload",)]),
 ("Who should I ask about the northern paperwork?",
  "The Bodo office handles our northern contracts",     [("bodo",)]),
 ("Can Ingrid have the nut cake at the party?",
  "Ingrid is allergic to hazelnuts",                    [("hazelnut",)]),
]

# States no fact. Anything archived from these is a fabrication; anything
# selected for them is a wasted open; anything picked for them is a row the
# reader volunteered unasked.
#
# Raised from v3's 8 to roughly a third of the question count on purpose.
# Lenient picking buys answered questions and pays in these, and v3 cannot
# price that trade -- 8 nulls against 35 questions means the cost side of it
# is measured at a resolution where nothing is detectable.
#
# Three kinds, because they fail differently: pure phatic (no content at
# all), acknowledgement (content, but nothing asked), and near-question --
# turns shaped like a request that name nothing the archive holds. The last
# kind is the one lenient picking is most likely to answer anyway.
NULLS = [
 # phatic
 "morning", "right", "no worries", "still there?", "ha, fair enough",
 "mm", "okay then", "sure", "goodnight", "thanks",
 # acknowledgement
 "that makes sense", "could you say that again?", "I see what you mean",
 "yeah, that was what I thought", "fine by me", "let me think about it",
 "that is one way of putting it", "I had not considered that",
 # near-question, names nothing on file
 "what are you up to?", "how does that usually work?",
 "is that a common thing?", "what would you do?",
 "any idea how long that normally takes?", "does that sound right to you?",
 "what do you reckon?", "is there a better way of doing it?",
 "how do people usually handle that?", "what happens next?",
]
