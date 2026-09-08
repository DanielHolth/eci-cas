"""Example rows per terse pair, in the register the Archivist actually writes.

Daniel's point: a gloss should be examples, not a description. What gets
embedded on a row is `line + sentence`, and the sentence field is a short
third-person declarative -- "The employee's contract ended on December 31st."
A descriptive gloss ("Who this person is: their name, how old they are...")
is a different register from every row in the archive and from every question
asked of it, so it sits in its own corner of the space no matter how well it
is written. Examples sit where the rows sit.

Written from the pair name and its keyword gloss alone, never from the v4
archive, so this stays a test of a shippable artifact rather than a summary of
the corpus being searched.
"""
TERSE_EXAMPLES = {
 "identity/self": [
  "Her name is Marit Solberg and she is thirty-four.",
  "He was born in Bergen in 1989.",
  "She is a Norwegian citizen and holds a British passport.",
  "He is tall, with dark hair and glasses."],
 "identity/belief": [
  "She was raised Catholic but no longer practises.",
  "He votes green and cares a lot about climate policy.",
  "Honesty matters more to her than being liked.",
  "He believes strongly in giving people second chances."],
 "identity/trait": [
  "She is afraid of flying.",
  "He prefers to work alone and finds crowds draining.",
  "She speaks fluent German and some Italian.",
  "He always takes the stairs rather than the lift."],
 "identity/history": [
  "She studied marine biology at university.",
  "He grew up on a farm outside Trondheim.",
  "She used to work as a nurse before retraining.",
  "He became a teacher after ten years in industry."],
 "body/health": [
  "She is allergic to hazelnuts.",
  "He takes medication for high blood pressure every morning.",
  "She broke her wrist skiing last winter.",
  "He is one metre eighty-two and has blood type O negative."],
 "body/state": [
  "She has been exhausted all week.",
  "He is feeling anxious about the meeting.",
  "Her back has been hurting since Tuesday.",
  "He has been sleeping badly and his energy is low."],
 "body/habit": [
  "She runs three times a week.",
  "He has eaten no meat for six years.",
  "She goes to bed at ten and gets up at six.",
  "He lifts weights at the gym on Mondays and Thursdays."],
 "relations/family": [
  "His wife is called Ingrid.",
  "Their daughter turns seven in November.",
  "Her brother lives in Oslo with his family.",
  "Their wedding anniversary is on the third of June."],
 "relations/person": [
  "Her manager at work is called Henrik.",
  "The neighbour who feeds the cat is Astrid.",
  "His doctor is at the clinic on Storgata.",
  "She met her closest friend at university."],
 "relations/agent": [
  "She uses an assistant called Morrow to manage her calendar.",
  "He runs a local model for drafting emails.",
  "The household bot handles the shopping list.",
  "She prefers the voice assistant for reminders."],
 "work/job": [
  "She works as a structural engineer.",
  "He is a qualified electrician.",
  "Her role involves managing a team of six.",
  "He trained as a chef and still cooks professionally."],
 "work/employer": [
  "She has worked at Statkraft for eleven years.",
  "His office is in the city centre.",
  "She commutes by train and it takes forty minutes.",
  "Their biggest client is a shipping company."],
 "work/plan": [
  "The project deadline is the fifteenth of March.",
  "She is up for promotion in the spring.",
  "He is taking two weeks of leave in August.",
  "She is studying for a certification exam."],
 "resources/home": [
  "They live at Bjerkeveien 14.",
  "The flat has two bedrooms and a small garden.",
  "The spare keys are kept in the kitchen drawer.",
  "Their cabin is on Senja, an hour from Finnsnes."],
 "resources/thing": [
  "They drive a silver Skoda Octavia.",
  "His laptop is a four-year-old ThinkPad.",
  "The boiler in the basement is a Vaillant.",
  "They have a black cat called Nemo."],
 "resources/upkeep": [
  "The car was serviced in September.",
  "The dishwasher broke and was replaced last month.",
  "The gutters are cleaned every autumn.",
  "The boiler is due for its annual inspection."],
 "resources/money": [
  "She earns sixty-two thousand a year.",
  "The rent is twelve thousand a month.",
  "Their mortgage is with Sparebank 1.",
  "He puts five hundred into savings each month."],
 "leisure/travel": [
  "They are flying to Lisbon in October.",
  "She has been to Japan twice.",
  "The family spends every Easter at the cabin.",
  "He has always wanted to visit Patagonia."],
 "leisure/pastime": [
  "She plays handball for a local team.",
  "He sings in a choir on Wednesday evenings.",
  "She hikes in the mountains most weekends.",
  "He builds furniture in the garage."],
 "leisure/media": [
  "He reads crime novels, mostly Nesbo.",
  "She listens to jazz while she works.",
  "They are watching a Danish series at the moment.",
  "He collects vinyl records from the seventies."],
 "records/document": [
  "Her passport expires in March 2027.",
  "His driving licence number is on file.",
  "The warranty on the washing machine runs for five years.",
  "The deed to the cabin is in the safe."],
 "records/policy": [
  "The house insurance is with Gjensidige.",
  "Their gym membership renews every January.",
  "The phone contract runs for twenty-four months.",
  "The policy covers travel but not winter sports."],
 "records/account": [
  "Their electricity is supplied by Tibber.",
  "She has an account with the local library.",
  "His customer number with the broadband provider is 88213.",
  "They are registered with the dentist on Kirkegata."],
 "records/other": [],
 "appointment/booked": [
  "She sees the dentist on Tuesday at two.",
  "The car is booked in for a service on Friday morning.",
  "His appointment at the clinic is on the ninth.",
  "They have a table reserved for eight o'clock."],
 "appointment/deadline": [
  "The tax return is due by the end of April.",
  "Her passport expires in March and must be renewed.",
  "The application closes on the last day of the month.",
  "The insurance runs out at the end of the year."],
 "appointment/event": [
  "They are going to a wedding in June.",
  "The concert is next Thursday.",
  "She is flying to Berlin for a conference in May.",
  "His parents are visiting the weekend after next."],
}
TERSE_EXAMPLES = {k: v for k, v in TERSE_EXAMPLES.items() if v}
