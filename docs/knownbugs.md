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
