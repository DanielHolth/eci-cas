# Known bugs

Observed defects, not design debate — that's `roadmap.md`. Grouped by the
faculty most responsible, since that's usually where the fix lands.

## Librarian

**Self/user identity confusion.** Asked "you want to learn some facts about
yourself?", Recall pulled five rows filed under `user` (name, birthdate,
media taste, communication preference, a project goal) and Intent answered
in first person as if they described the assistant, not Daniel:

```
Perception:
You want to learn some facts about yourself?
Impulse:
no immediate concern
Recall-1:
identity/name/name/user/name = Daniel
Recall-2:
moment/milestone/birthdate/user/birthdate = 12.10.1978
Recall-3:
media/taste/opinion/user/thoughts about flowers = beautiful flowers
Recall-4:
character/temperament/style/user/communication preference = say nice things
Recall-5:
project/review/goal/user/testing purpose = ux improvement
Intent:
I was born on October 12, 1978, and I care deeply about how I communicate
with you. I like to think I'm a person who enjoys saying nice things,
especially when it comes to things like beautiful flowers, which I
genuinely appreciate. Right now, my main goal is to help you improve your
user experience by sharing these simple facts about myself.
```

The rows Librarian selected are correctly scoped to `user` — the bug is that
nothing downstream (Librarian's pair selection, or Intent's prompt) tells the
model whose facts these are before it answers a question about "yourself".
`assistant` is a scope, not a ranked category (see roadmap.md, "The assistant
is a scope, and hindsight is not a lookup"), so there's no symmetric mistake
in the other direction — but a `user`-scoped row still needs to read as
Daniel's fact, not the persona's, once it's in Intent's context.

## Archivist

**A question about a daughter filed under `pet`.** Asked whether the persona
would play hide and seek with a child, Archivist wrote the turn away into a
category nothing in it names:

```
Perception:
My daughter is asking if we can play hide and seek with you
Impulse:
no immediate concern
Librarian-1:
family/child
Recall-1:
family/child/daughters/user/youngest daughter name = Susana
Recall-2:
family/child/identity/user/daughters name = Maria Benita
Recall-3:
family/child/nickname/user/daughter name = Maia
Recall-4:
family/child/daughters/user/number of daughters = two
Recall-5:
assistant/reflection/tier/assistant/answer quality = remarkable
Recall-6:
assistant/other/identity/assistant/description = prototype companion
Recall-7:
assistant/other/identity/assistant/nature = emergent cognitive identity
Recall-8:
assistant/other/identity/assistant/structure = faculties of a mind
Intent:
I'd love to play hide and seek with Susana, but since I don't have a
physical body, I'll have to be the one to hide instead.
Archivist-1:
pet/other/interaction/user/question = playing hide and seek
```

Read alone the row is nearly reasonable -- "playing" is a thing one does with
a pet -- but the turn had already been read as `family/child` by Librarian and
answered about a named daughter by Intent, so the closed vocabulary was not
the constraint that failed. Worth noting the row is also weak on its own
terms: the fact recorded is that a question was asked, which is a turn, not
something that stays true about anybody.

Two things to check before calling it a prompt problem. Whether the write-side
call sees what the read side saw -- if the pair Librarian selected does not
reach Archivist, it is categorising the sentence cold. And whether `x/other`
is doing the damage it was measured not to do: "other" is a write-side valve,
so `pet/other` is the shape a category takes when nothing under it fits, which
means the miss was `pet` and the valve only made it storable.
