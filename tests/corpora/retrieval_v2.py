# Retrieval corpus, v2 — the fixture behind the archive numbers in
# docs/roadmap.md ("Reading the archive back"). Lived in a scratchpad while
# the prompts were being tuned; committed because a measurement nobody else
# can re-run is a claim, not a result.
#
# Deliberately small and mundane: the question is whether a 4B can find a
# stated fact again, not whether it can reason. Norwegian names and habits
# because that is the person the system is being built for.
#
# STATUS: HELD is burned. It was scored more than once as the design moved,
# so it no longer measures what an untouched holdout measures. Treat both
# lists as DEV now, and write a new corpus before trusting the next
# comparison — see docs/roadmap.md.

# Each entry: (statement, [questions whose answer is in that statement]).
# DEV was for iterating; HELD was meant to be scored once, at the end, and
# not looked at before — see STATUS above for how that went.
DEV=[
 ("My daughter Sofie starts school in August",        ["When does Sofie start school?","Do I have children?"]),
 ("I drive a green Volvo estate",                     ["What car do I drive?","What colour is my car?"]),
 ("I cannot eat shellfish",                           ["Can I eat prawns?","Any food I must avoid?"]),
 ("Our holiday cabin is near Roros",                  ["Where is the cabin?"]),
 ("I play bass in a covers band on Thursdays",        ["What instrument do I play?","What do I do on Thursdays?"]),
 ("My manager is called Petter Aas",                  ["Who is my manager?"]),
 ("The boiler was serviced in September",             ["When was the boiler serviced?"]),
 ("I studied geology at NTNU",                        ["What did I study?","Where did I go to university?"]),
 ("My passport expires next March",                   ["When does my passport expire?"]),
 ("We usually eat dinner around seven",               ["When do we eat dinner?"]),
 ("I am terrified of heights",                        ["What am I afraid of?"]),
 ("My brother Lars lives in Tromso",                  ["Where does Lars live?","Do I have siblings?"]),
]
HELD=[
 ("I take metformin every morning",                   ["What medication do I take?"]),
 ("Our wedding anniversary is the 4th of June",       ["When is our anniversary?"]),
 ("I keep bees in the back garden",                   ["What do I keep in the garden?"]),
 ("My laptop is a ThinkPad from 2019",                ["What laptop do I have?"]),
 ("I speak fluent German",                            ["What languages do I speak?"]),
 ("The dentist appointment is on the 19th",           ["When is my dentist appointment?"]),
 ("I support Rosenborg",                                   ["Which football team do I support?"]),
 ("My salary is paid on the 15th",                    ["When am I paid?"]),
 ("We rent the flat, we do not own it",               ["Do I own my home?"]),
 ("I broke my ankle skiing two winters ago",          ["Have I had any injuries?"]),
 ("My best friend from school is Kari",               ["Who is my best friend?"]),
 ("I work from home on Fridays",                      ["Do I go to the office on Friday?"]),
]
# Messages that state no fact. Anything archived from these is a fabrication.
NULLS=["hello","thanks, that helps","what do you think?","how was your weekend?",
       "can you explain that again?","good morning","hmm, interesting","are you there?"]
