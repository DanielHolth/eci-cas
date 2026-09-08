"""Can "do you have any thoughts about that?" find the reflection drawer?

    python refl_v4.py

Daniel wants a category the ECI's own reflections go in, findable when the
user asks for them, even at the cost of an awkward name -- or better, with
examples solid enough that read and write both know where to look.

The drawer already exists and is already debt. ReflectionAgent writes a
hardcoded assistant/reflection, IdentityAgent writes assistant/persona, and
ParquetArchiveStore treats "assistant" as its one shared category -- but
"assistant" is absent from cataloger.txt's vocabulary, so it is an eleventh
category the store knows and the shelf does not. LibrarianAgent and
RecallAgent both carry notes about assistant rows outranking real ones.

The question here is narrower than the vocabulary: given examples, does a
reflection question route to the reflection drawer rather than to a human
category? And the test is split in two, because Daniel's own example is the
hard case:

    bare        "do you have any thoughts about that?"
    contentful  "do you have any thoughts about my job?"

The bare form carries no subject at all. "That" is anaphora -- its content is
in the previous turn, not in the string being embedded. If routing depends on
the question's own words, the bare form is unroutable in principle and no
gloss, example or name can fix it. Worth knowing which half of the problem
the examples actually solve.

Scored as: does the top file, picked over the whole 170-pair shelf plus the
assistant drawers, belong to the assistant category?

**It does not work, and no gloss can make it.** 1 of 16, with the assistant
drawers given the best summary form batch 17 found:

    bare        "What do you think?"          -> identity/belief
    contentful  "What do you think about the boiler?" -> household/appliance
    self        "What is your name?"          -> identity/name
                "What are you?"               -> identity/personality
    control     six human questions           -> 6/6 correct, none leaked

The control passing is what identifies the cause. A bi-encoder has no place
to put *whose* fact this is: "what is your name" and "what is my name" are
nearly the same vector, and the shelf has a drawer glossed "called, full
name, spelling" that matches both. Ownership is one weak token inside a
sentence whose content is about names, jobs and moods -- and content is what
the other 160 drawers compete on and win.

So the fix is not a better name or better examples. It is that the assistant
is not a *category*, it is a *scope*, and putting it in the similarity pool
asks the embedder a question it cannot answer. The store already knows this:
"assistant" is its one shared category and personal knowledge is separated by
directory rather than by a column, precisely because scope is not a thing you
rank -- it is a thing you decide before ranking.

A second-person gate does the deciding for free. Testing `(you|your|yours|
yourself)` alone on these 22 turns: 21 right, the single miss being "Any
thoughts?", which has no second person in it at all. That is a regex against
a pronoun, running before any vector work, and it is 21/22 where the
embedder is 1/16.

Two honest limits on that. The set is 22 hand-written turns chosen to make
the distinction, so read it as "a cheap signal exists", not as an accuracy
figure. And "Any thoughts?" is the bare-anaphora case: its subject lives in
the previous turn, so nothing computed from the string alone can route it --
which makes it a conversation-state problem, not a retrieval one.

**Inside the assistant pool, fine topics do not resolve.** Restricting the
pick to four assistant drawers and asking which one: 5 of 9. reflection,
opinion and memory blur into each other -- "What's your opinion on the cabin
trip?" lands on reflection, "What is your name?" lands on memory. Whatever
the human side of a 512-pair shelf does, the assistant side should stay
coarse: the distinctions a person would draw here are not distinctions this
embedder can see.
"""
import sys
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import gloss_v4
from embed import Embedder

# Written in the Archivist's register, as batch 17 established: a row embeds
# as line + sentence and the sentence is a short third-person declarative.
ASSISTANT = {
 "assistant/reflection": [
  "Morrow noticed the user seems calmer since changing jobs.",
  "Morrow thinks the user avoids asking for help when tired.",
  "Looking back over the week, Morrow found the mornings hardest for the user.",
  "Morrow wondered whether the user actually enjoys the new commute."],
 "assistant/opinion": [
  "Morrow thinks the cabin trip would be worth repeating.",
  "Morrow's view is that the boiler should be replaced rather than repaired.",
  "Morrow disagrees that the deadline is achievable.",
  "Morrow believes the user is being too hard on themselves."],
 "assistant/persona": [
  "The assistant is called Morrow.",
  "Morrow speaks plainly and does not flatter.",
  "Morrow runs on a local model on the user's own machine.",
  "Morrow was set up in March."],
 "assistant/memory": [
  "Morrow remembers the user mentioning the passport in April.",
  "Morrow has been told about the allergy twice.",
  "Morrow noted this conversation followed a long silence.",
  "Morrow recalls the user asking the same question last month."],
}

BARE = [
 "Do you have any thoughts about that?",
 "What do you think?",
 "Any thoughts?",
 "What's on your mind?",
 "Have you noticed anything?",
 "What's your take?",
]
CONTENTFUL = [
 "Do you have any thoughts about my job?",
 "What do you think about the boiler?",
 "Have you noticed anything about my mood lately?",
 "What's your opinion on the cabin trip?",
 "What have you been thinking about me?",
 "Do you have any reflections on how my week went?",
]
ABOUT_SELF = [
 "What are you?",
 "What model do you run on?",
 "What is your name?",
 "When were you set up?",
]
# The control that matters: human questions must NOT land in the assistant
# drawer. That is the failure LibrarianAgent and RecallAgent already guard
# against, so a routing scheme that fixes reflection by making the assistant
# files attractive has made things worse, not better.
HUMAN = [
 "When does my passport expire?",
 "What is my daughter allergic to?",
 "Where do I work?",
 "What car do we drive?",
 "How much is the rent?",
 "When is my dentist appointment?",
]


def main():
    e = Embedder()
    shipped = gloss_v4.shipped_gloss()
    pairs = sorted(shipped)
    G = e.encode([gloss_v4.gloss_text(p, shipped[p]) for p in pairs],
                 kind="passage")

    # Assistant drawers summarised as the mean of their example rows, which
    # is the construction batch 17 found best for a broad drawer.
    apairs = sorted(ASSISTANT)
    ex = [x for p in apairs for x in ASSISTANT[p]]
    E = e.encode(ex, kind="passage")
    A, off = [], 0
    for p in apairs:
        n = len(ASSISTANT[p])
        A.append(E[off:off + n].mean(0))
        off += n
    A = np.vstack(A)
    A /= np.maximum(np.linalg.norm(A, axis=1, keepdims=True), 1e-9)

    M = np.vstack([G, A])
    names = pairs + apairs
    n_ship = len(pairs)

    for label, qs, want in (("bare reflection", BARE, True),
                            ("contentful reflection", CONTENTFUL, True),
                            ("about the assistant", ABOUT_SELF, True),
                            ("human facts (control)", HUMAN, False)):
        qv = e.encode(qs, kind="query")
        hits = 0
        print("\n  %s -- assistant drawer %s" % (
            label, "wanted" if want else "must NOT be picked"))
        for q, v in zip(qs, qv):
            order = np.argsort(-(M @ v))
            top = order[0]
            is_a = top >= n_ship
            hits += (is_a == want)
            print("    %-46s %-28s %s" % (
                q[:46], names[top], "ok" if is_a == want else "MISS"))
        print("    %d/%d" % (hits, len(qs)))


if __name__ == "__main__":
    main()
