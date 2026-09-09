# Morrow — the product

What Morrow is, who it is for, what it promises and what it deliberately
does not. [`roadmap.md`](roadmap.md) owns how it gets built; this file owns
what is being sold and why anyone would want it. Expect it to be rewritten
as the market answers back — the architecture note is executed against, this
one is learned against.

---

## What it is

**A companion that remembers you, and keeps remembering for decades.**

Not an assistant you query. One instance per person, developing against
that person over years, with a memory that is theirs — on their device, in
a format that outlives the app.

The nearest honest comparison is a diary that reads itself back to you and
has opinions.

## Why it is different

Three claims, and each one is architecture rather than copy.

**Your memory is a file you own.** Parquet and JSONL, on the device,
readable in thirty years by anyone with a parquet reader and none of our
code. Every companion app on the store is a proprietary blob on someone
else's server. This one is not, and that is checkable.

**It runs on your phone.** The agents, the bus, the archive, the retrieval
— all of it. What leaves the device is a prompt to a model provider, and
that is stated plainly rather than dressed up. There is no account holding
your conversations because there is no place to hold them.

**It can be inherited.** The archive is exportable and openable by someone
who is not you. A child can read a parent's. This is the strongest thing
here and the one nobody else is offering.

## Who it is for

**Launch aims at one person: someone who prefers solitude and is
occasionally lonely.** Somewhere to put worries that does not require
another person's time, and that remembers the last time you were worried
about the same thing. That audience is real, underserved, and describes the
product as built rather than as extended.

Everything else is expansion, and each is the same engine with a different
instruction set and a different seeded vocabulary:

- **Student** — a fellow student, tutor and consultant that remembers the
  whole course, not the last message.
- **Professional** — secretary, coworker, interactive memory bank; who said
  what in which meeting, eighteen months ago.
- **Player** — remembers your builds, your losses and what the counter was
  last time.
- **Longevist / legacy** — the archive as the point rather than as the
  mechanism. A life, written down, passable on.

**Launch copy names one.** Four reads as not knowing what the product is.

## What it costs

Three plans. The split is by *what it can do*, not by how fast it types.

**Free — sponsored.** The whole companion: memory, retrieval, conversation.
Cheapest viable model, and a hard monthly ceiling that is stated up front
rather than discovered. Assume this is most people, permanently, and that
it is a cost of doing business rather than a funnel.

**Standard — around $10/month.** Best value per token, and **reflection**:
Morrow thinks about you while you are not there and tells you what it
noticed. This is the hook, and it is the thing that makes it a companion
rather than a chatbot with a database.

**Pro — around $100/month.** Strongest and fastest models, **the toolkit** —
Morrow can analyse its own memory of you rather than only recall it
("what was my uncle interested in") — plus support and first access.

Standard carries free. That has to be true at the ratios we expect, and the
deterministic read and write path is what makes it true.

## What we do not promise

Stated here so it is never accidentally implied.

**Not "your data never leaves your device."** A prompt to a model provider
carries what it needs to carry. What is true is that we keep no archive, no
conversation and no profile; the only thing our server holds is a plan and
a token count.

**Not continuous thought.** Morrow does not run all day. It reflects on a
schedule, when charging, and tells you afterwards. Android would break any
stronger promise and we would rather not make it.

**Not therapy, and not a person.** Somewhere to put a worry is not
treatment, and the copy must never drift toward implying it is.

**Not a service that must survive for your memory to.** If this project
ends, the archive is still a file, still readable, still yours. That is the
point of the format choice and it should be said out loud.

## What has to be true before launch

Product-side, not engineering:

- One persona chosen and its instructions written.
- The export and inheritance path actually working, because it is the
  headline claim.
- The free ceiling picked from measured usage rather than guessed.
- Pro priced at support a solo developer can actually staff.
