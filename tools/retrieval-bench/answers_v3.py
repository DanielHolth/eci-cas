# Answer key for retrieval_v3's direct questions.
#
# Same contract as answers.py: per question, a list of alternatives; an
# alternative is a tuple of tokens that must ALL appear somewhere in the rows
# handed back, lowercased. A tuple is how a two-part question is scored --
# "Where does Marit live?" needs the person and the place, so a row that
# fuses them into one value passes and a row that drops either does not.
#
# Kept out of tests/corpora/retrieval_v3.py so the committed corpus stays as
# it was written, and because this key is a bench artefact: it encodes what
# counts as answered, which is a scoring decision, not a fixture.
ANSWERS = {
 "When is Ingrid's birthday?":            [("november",)],
 "How old is my daughter?":               [("seven",)],
 "What car do we drive?":                 [("skoda",), ("octavia",)],
 "What colour is our car?":               [("silver",)],
 "Am I allergic to anything?":            [("penicillin",)],
 "Can I take penicillin?":                [("penicillin",)],
 "Where is our cabin?":                   [("senja",)],
 "How far is the cabin from Finnsnes?":   [("hour",)],
 "What do I do on Wednesdays?":           [("choir",), ("sing",), ("tenor",)],
 "Do I sing?":                            [("choir",), ("tenor",), ("sing",)],
 "Who handles the invoices?":             [("ingvild",)],
 "When was the heat pump installed?":     [("2021",)],
 "What did I study?":                     [("marine",), ("biology",)],
 "Where did I study?":                    [("uit",)],
 "When does my licence expire?":          [("2028",)],
 "When do we walk the dog?":              [("breakfast",)],
 "Do we have a dog?":                     [("dog",)],
 "Do I get seasick?":                     [("seasick",)],
 "Where does Marit live?":                [("marit", "bodo")],
 "Do I have siblings?":                   [("sister",), ("marit",)],
 "Where do I work?":                      [("statkraft",)],
 "How long have I worked there?":         [("eleven",)],
 "What are we saving for?":               [("kitchen",)],
 "What is Ingrid scared of?":             [("ingrid", "thunder")],
 "What do I read?":                       [("crime",), ("novels",)],
 "Who is my favourite author?":           [("nesbo",)],
 "Who has our spare key?":                [("bjorn",)],
 "How do I get to work?":                 [("bus",)],
 "What time is my bus?":                  [("07:20",), ("7:20",), ("0720",)],
 "Is my father alive?":                   [("2019",), ("passed",), ("died",), ("death",)],
 "Where are we going on holiday?":        [("lofoten",)],
 "Do I smoke?":                           [("smoking",), ("smoke",)],
 "What is wrong with the dishwasher?":    [("leak",), ("overload",)],
 "What languages do I know?":             [("german",)],
 "Do we rent anything out?":              [("basement",), ("student",)],
}
