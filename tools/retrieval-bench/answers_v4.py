# Answer key for retrieval_v4's direct questions.
#
# Same contract as answers_v3.py: per question, a list of alternatives; an
# alternative is a tuple of tokens that must ALL appear somewhere in the rows
# handed back, lowercased. A tuple is how a two-part question is scored --
# "Where does Marit live?" needs the person and the place, so a row that
# fuses them passes and a row dropping either does not.
#
# Kept out of tests/corpora/retrieval_v4.py so the committed corpus stays as
# written, and because this key is a bench artefact: it encodes what counts
# as answered, which is a scoring decision rather than a fixture.
#
# One rule followed throughout and worth stating, because v4's whole point is
# near-miss clusters: where several statements share a word, the key never
# accepts that shared word alone. "March" appears in the passport and in the
# house insurance, so neither is scored on "march" -- the passport wants
# "2027" and the insurance wants "insurance". A key that accepted the shared
# token would score a wrong row as a hit, which is precisely the confusion
# the clusters exist to detect.
ANSWERS = {
 # expires, renews, runs out -- "march", "2028" and "renew" are all shared
 "When does my passport expire?":            [("2027",)],
 "When does my licence need renewing?":      [("2028",)],
 "When does the house insurance renew?":     [("insurance", "march")],
 "When does the gym membership renew?":      [("gym", "january"), ("membership", "january")],
 "Is the car still under warranty?":         [("warranty",)],
 "When does Ingrid's passport expire?":      [("2031",)],

 # Bodo -- the place name is shared by four, so none is scored on it alone
 "Where does Marit live?":                   [("marit", "bodo")],
 "Do I have siblings?":                      [("sister",), ("marit",)],
 "How often do I travel for work?":          [("fly", "month"), ("flight", "month"), ("bodo", "month")],
 "Where did we stay in Bodo?":               [("scandic",)],
 "Which office handles the northern contracts?": [("bodo", "contract"), ("bodo", "office")],

 # 2021 -- the year is shared by four
 "When was the heat pump installed?":        [("heat", "2021"), ("pump", "2021")],
 "When did we repaint the house?":           [("paint", "2021")],
 "When was I promoted?":                     [("promot", "2021"), ("senior", "2021")],
 "When was Nella born?":                     [("nella", "2021")],

 # the week -- every weekday is shared with at least one other row
 "What do I do on Wednesdays?":              [("choir",), ("sing",), ("tenor",)],
 "Do I sing?":                               [("choir",), ("sing",), ("tenor",)],
 "When does Ingrid have handball?":          [("handball", "tuesday")],
 "When do the bins go out?":                 [("bin", "thursday")],
 "When do we walk the dog?":                 [("breakfast",)],
 "Which day do I work from home?":           [("home", "friday")],

 # health
 "Am I allergic to anything?":               [("penicillin",)],
 "Can I take penicillin?":                   [("penicillin",)],
 "Is Ingrid allergic to anything?":          [("hazelnut",)],
 "What medication do I take?":               [("blood", "pressure"), ("tablet",)],
 "Do I get seasick?":                        [("seasick",)],
 "Do I have any back trouble?":              [("lift",), ("back", "lift")],
 "Do I smoke?":                              [("smok",)],
 "When is my next dental appointment?":      [("six", "month"), ("dentist", "month")],

 # people
 "Who has our spare key?":                   [("bjorn",)],
 "Who handles the invoices?":                [("ingvild",)],
 "Who feeds the cat when we are away?":      [("astrid",)],
 "Who did our bathroom?":                    [("rune",)],
 "Who is my manager?":                       [("henrik",)],

 # the house
 "What is wrong with the dishwasher?":       [("overload",), ("leak",)],
 "What is wrong with the downstairs toilet?":[("jiggle",)],
 "Do we rent anything out?":                 [("basement",), ("rent",)],
 "What is in the loft?":                     [("christmas",), ("decoration",)],
 "What is our address?":                     [("bjerkeveien",)],
 "What is wrong with the garage door?":      [("stick",), ("cold",)],
 "How do we heat the living room?":          [("woodburner",)],

 # vehicles
 "What car do we drive?":                    [("skoda",), ("octavia",)],
 "What colour is our car?":                  [("silver",)],
 "When do the winter tyres go on?":          [("tyre", "november")],
 "What time is my bus?":                     [("07:20",), ("0720",), ("07",)],
 "What colour is Ingrid's bicycle?":         [("bicycle", "red"), ("bike", "red")],

 # work
 "Where do I work?":                         [("statkraft",)],
 "How long have I worked there?":            [("eleven",), ("11",)],
 "What is my job?":                          [("hydrolog", "engineer")],
 "When is payday?":                          [("fifteenth",)],
 "How much leave do I have left?":           [("twelve",)],
 "When is the Monday stand-up?":             [("half", "eight"), ("08:30",)],
 "What did I study?":                        [("marine", "biology")],
 "Where did I study?":                       [("uit",)],

 # money
 "What are we saving for?":                  [("kitchen",)],
 "Who is our mortgage with?":                [("sparebank",)],
 "What do we pay for streaming?":            [("340",)],
 "Where is my pension?":                     [("klp",)],
 "What happened to the electricity bill?":   [("doubl",)],

 # family
 "When is Ingrid's birthday?":               [("ingrid", "november")],
 "How old is my daughter?":                  [("seven",)],
 "Is my father alive?":                      [("2019",), ("passed",), ("death",)],
 "Where does my mother live?":               [("grew",), ("childhood",)],
 "What does Kristin do?":                    [("teach",), ("secondary",)],
 "What is Ingrid scared of?":                [("thunder",)],
 "Where did we get married?":                [("trondheim",)],

 # leisure
 "What do I read?":                          [("crime",), ("novel",)],
 "Who is my favourite author?":              [("nesbo",)],
 "What sport do I play?":                    [("badminton",)],
 "Where is our cabin?":                      [("senja",)],
 "How far is the cabin from Finnsnes?":      [("hour", "finnsnes"), ("hour", "cabin"), ("hour", "senja")],
 "What do I brew?":                          [("beer",)],
 "What hobby does Kristin have?":            [("pottery",)],
 "Which football team do I follow?":         [("rosenborg",)],

 # travel
 "Where are we going on holiday?":           [("lofoten",)],
 "What is my rule about stopovers?":         [("stopover",)],
 "How do we travel south?":                  [("night", "train")],
 "Does Ingrid get carsick?":                 [("carsick",), ("car", "sick")],

 # learning
 "What languages do I know?":                [("german",)],
 "What course am I taking?":                 [("modelling",), ("course", "hydrolog")],
 "When did Ingrid start piano?":             [("piano", "august")],
 "What language is Kristin learning?":       [("spanish",)],

 # preferences
 "What food do I dislike?":                  [("coriander",)],
 "How do I take my coffee?":                 [("black",)],
 "What do we eat on Fridays?":               [("fish",)],
 "What am I bad at?":                        [("name",)],
 "How should people contact me?":            [("text",), ("message",)],
}
