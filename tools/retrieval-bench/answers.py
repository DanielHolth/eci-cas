# Answer key for the value-sufficiency score (a).
#
# The existing numbers all score (b): did Librarian open the file the write
# side used. Nothing has ever scored whether the row it opens still contains
# the answer -- and four smoke-test rows on 2026-09-06 lost "Volvo", "August"
# and attributed Tromso to the wrong person. This is that missing half.
#
# Per question, the tokens that must appear somewhere in the extracted rows
# (subtopic + subject + key + value, lowercased) for the question to be
# answerable at all. A tuple means every token is required -- "Where does
# Lars live?" needs both the person and the place, so a row that fuses them
# into one value still passes while a row that drops either does not.
# A list of alternatives means any one will do.
#
# Lives here rather than in tests/corpora/retrieval_v2.py so the committed
# corpus stays as it was scored; move it there if this metric proves out.
ANSWERS = {
 "When does Sofie start school?":        [("august",)],
 "Do I have children?":                  [("daughter",), ("sofie",)],
 "What car do I drive?":                 [("volvo",)],
 "What colour is my car?":               [("green",)],
 "Can I eat prawns?":                    [("shellfish",)],
 "Any food I must avoid?":               [("shellfish",)],
 "Where is the cabin?":                  [("roros",)],
 "What instrument do I play?":           [("bass",)],
 "What do I do on Thursdays?":           [("bass",), ("band",), ("thursday",)],
 "Who is my manager?":                   [("petter",)],
 "When was the boiler serviced?":        [("september",)],
 "What did I study?":                    [("geology",)],
 "Where did I go to university?":        [("ntnu",)],
 "When does my passport expire?":        [("march",)],
 "When do we eat dinner?":               [("seven",)],
 "What am I afraid of?":                 [("heights",)],
 "Where does Lars live?":                [("lars", "tromso")],
 "Do I have siblings?":                  [("brother",), ("lars",)],
}
