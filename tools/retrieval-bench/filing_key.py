"""Which drawers are defensible for each DEV statement.

Not one right answer per statement -- several folders are genuinely
reasonable for the same fact, and a key that insisted on my favourite would
measure my taste rather than the model's filing. The set is deliberately
generous: what it is built to catch is gross misfiling and reaching for
"other" when a real folder exists, not near-misses.

The one place it is strict is "My manager is called Petter Aas". cataloger.txt
decides that tie in writing -- "A person goes in relations. A fact about the
job goes in work." -- so work/* is scored wrong there on purpose. A tie rule
nothing enforces is a comment.
"""
KEY = {
 "My daughter Sofie starts school in August": {
     "relations/child", "relations/family", "learning/school", "learning/plan"},
 "I drive a green Volvo estate": {"household/vehicle"},
 "I cannot eat shellfish": {"body/diet", "body/allergy"},
 "Our holiday cabin is near Roros": {
     "household/property", "travel/accommodation", "travel/destination"},
 "I play bass in a covers band on Thursdays": {
     "leisure/music", "leisure/instrument", "leisure/club", "leisure/routine",
     "leisure/hobby"},
 "My manager is called Petter Aas": {"relations/colleague", "relations/contact"},
 "The boiler was serviced in September": {
     "household/maintenance", "household/repair", "household/appliance"},
 "I studied geology at NTNU": {
     "learning/university", "learning/subject", "learning/school",
     "learning/qualification"},
 "My passport expires next March": {
     "admin/passport", "admin/renewal", "admin/deadline"},
 "We usually eat dinner around seven": {
     "leisure/routine", "body/diet", "identity/habit"},
 "I am terrified of heights": {"identity/fear"},
 "My brother Lars lives in Tromso": {
     "relations/sibling", "relations/family", "relations/contact"},
}
