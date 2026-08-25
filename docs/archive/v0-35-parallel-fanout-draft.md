# v0.35 draft — parallel fan-out, two new archive-grounded agents, Intent owns the revision loop

**Status:** Design capture from a voice-dictated brainstorm (Daniel, via a
separate chat, relayed here 2026-08-24). Not yet implemented, not yet
folded into ECI-spec-revisions. This document exists to pin down exactly
what was described before any code changes, and to be corrected before it
becomes a real revision doc.

**This breaks Phase 0.4.** Three of the eight roles change shape
(Analytics' output becomes an input to a bundle rather than the sole
gating verdict; Security's red path no longer touches Analytics at all;
Intent gains a real veto). Two new roles are added. Confirmed explicitly
by Daniel as an intentional, known-large change — not something to talk
him out of, just something to get exactly right before touching code.

---

## 1. The reactive pipeline, end to end

```
Sensory
  │
  ├──→ Impulse       (existing, deterministic — reflex + severity, unchanged)
  ├──→ Analytics     (existing, cognitive — reasons about the event, sets
  │                    proceed/concern; unchanged in what it does, changed
  │                    in who receives its answer — see §3)
  ├──→ Personality   (NEW — archive-grounded, see §2)
  └──→ Knowledge     (NEW — archive-grounded, see §2)

  All four receive their own copy of the same Sensory event, in parallel,
  with NO Governance hop on this fan-out (explicitly: "I don't want
  governance" here — the one place in the whole pipeline Governance is
  deliberately absent).

Governance buffers/waits for all four to answer, then sends ONE bundled
message to Intent. (If Impulse's severity read is Critical, see §4 —
different path.)

Intent (existing, cognitive, persona) synthesizes the bundle into speech.
Same Advise/Refuse voicing job as Phase 0.4, richer input — see §3.

Governance → Security (clearance check on Intent's proposed speech/action)

  Security clears (green/yellow, non-blocking) → Governance → Action
  Security reds                                → Governance → Intent
                                                    (Intent revises — see §5)
                                                    → Governance → Security
                                                    (loop until cleared)
                                                    → Governance → Action
```

Every hop except the Sensory→{Impulse,Analytics,Personality,Knowledge}
fan-out passes through Governance. That fan-out is the one deliberate
exception — confirmed explicitly, not an oversight.

## 2. Personality and Knowledge — one new agent *class*, not two one-offs

Confirmed: these two are architecturally **identical** — same shape,
same output contract, same "never write" posture — differing only in
(a) which Archive folder they're pointed at and (b) their system
instruction's wording. Daniel flagged that this may become a *family* of
agents later ("should we ever need more agents like this... they could
all go under some kind of knowledge agents... don't think Personality and
Knowledge are the only ones of this character") — so this should be
built as one reusable shape, not two hand-copied implementations.

**Shared contract, both agents:**

- Read-only. Query Archive, never write. (Writing stays where it already
  lives — Intent writes identity epochs at consolidation; nothing about
  that changes.)
- See only the single current event — no cross-event memory of their
  own, no persona, no values of their own. Purely "what does the archive
  say that's relevant to *this*."
- Output in the **same terse keyword format Analytics already
  produces** — this is explicit and load-bearing: the human-facing
  "thought bubble" UI (§7) shows three colors (Analytics / Personality /
  Knowledge), and the format has to be recognizable as the same *kind*
  of thing across all three for that UI to make sense. Intent is
  described as pattern-matching on this shared keyword format, not
  parsing three different shapes.

**Personality** — points at Archive's **identity** store (Core Anchors,
Evolving Trait Delta, epochs — the same store `agents/intent/base.py`'s
`hydrate()` currently reads). Contributes identity/values-relevant
findings about the current event: is this in character, does it touch a
prior stated boundary, etc. — the situational counterpart to what Intent
currently self-hydrates for its own persona rendering.

**Knowledge** — points at Archive's **knowledge** store (currently
declared in the Memory Model table, `data/archive/knowledge/`, unused by
any agent today). Contributes retrieval: facts, people, places, stories —
Daniel's framing: *"local knowledge — I know these people, I know that
story"* — explicitly **not** worldly/parametric knowledge. Retrieval
only; writing new knowledge stays Intent's consolidation-time triage job,
per the existing Memory Model table (unchanged from v0.32/§6).

**Analytics stays unique, deliberately.** It does not touch either
Archive folder these two read. It keeps its existing rolling
working-queue window (loop/trend detection, unchanged from today) but
leans on its own parametric/pretrained ("worldly") knowledge rather than
Archive-grounded lookup, and stays neutral rather than character- or
memory-colored. This is the actual dividing line between Analytics and
the new pair: *worldly reasoning* vs. *local/archive-grounded retrieval*.

**Open implementation question (not blocking, just noting for later):**
a shared base class (`agents/archive_lookup/base.py`?) that Personality
and Knowledge both subclass, parameterized by which Archive `kind` they
query and their system instruction — mirroring how `agents/intent/`
already splits `base.py` (shared) from `live.py`/mock (tier-specific).
This is the natural shape for the "family of similar agents" Daniel
flagged as likely to grow.

## 3. What Intent's bundle looks like, and what changes for Analytics

Analytics keeps reasoning and keeps setting `proceed`/`concern` exactly
as it does in Phase 0.4 — **but its answer no longer goes to Governance's
dispatcher for Security clearance directly** (today: Analytics'
`proceed: false` is what tells Governance to route toward a decline).
Instead Analytics' answer becomes one of four inputs Governance bundles
for Intent. Intent still receives `proceed`/`concern` from Analytics'
slot in the bundle (that part of Phase 0.4's contract is unchanged) plus
Personality's and Knowledge's keyword findings plus Impulse's reflex —
richer grounding for the same ADVISE/REFUSE choice Intent already makes
in Phase 0.4.

**Intent also gets broader context the other three don't have**: Daniel
was explicit that Analytics/Personality/Knowledge only ever see the
single current event, while Intent has visibility into the ongoing
conversation (this already exists in shape — Intent's temp log and
persona are the only cross-event state in the system — but Intent's
per-event `voice()` call doesn't currently read the temp log; extending
it to do so, at least for recent turns, is implied by "intent... has a
broader scope of the ongoing conversations" and is worth confirming
explicitly when this gets specced properly rather than assumed).

## 4. Critical path

Confirmed: the Critical-severity reflex path also goes through Governance
(not a bypass) — `Impulse → Governance → Security → Action`, skipping the
bundle/Analytics/Personality/Knowledge/Intent on the way in, exactly
matching the Critical-reflex design already named-but-deferred in
`docs/phase-0.3-impulse.md`. What's new: if Security reds a Critical-path
action, it now also loops back through Governance to Intent for revision
(§5) rather than having no revision path at all — so Intent is in the
loop for every red, Critical or not.

## 5. Security red → Intent owns the revision, not Analytics. Confirmed, explicit, load-bearing.

This is the one change that reverses a hard invariant Phase 0.4 was built
around. Worth stating plainly rather than softening it:

**Before (v0.34, current code):** Security red → Governance → Analytics
revises → Governance → Security (loop). Intent is "advisory only... holds
no veto" (§5.5) — nothing Intent says can change whether an action
happens. `agents/intent/contract.py`'s entire fallback design leans on
this: there's no fail-closed/fail-open asymmetry in Intent's contract
*because* Intent has nothing to gate.

**After (v0.35, as confirmed):** Security red → Governance → **Intent**
revises. Analytics is severed from Security entirely — "we have to sever
the connection between analytics and security... the whole thing goes
over to intent now." Intent's revision is grounded in more than a fresh
Analytics call would be: it already has Analytics' + Personality's +
Knowledge's original answers (from the bundle) *and* broader conversation
context the single-event agents don't have. Daniel's stated rationale:
this gives the revision decision better grounding, not worse — an
explicit, reasoned trade, not an oversight.

**What this actually requires, precisely, so nothing is glossed over:**

- Intent needs a **third task**, not just ADVISE/REFUSE — call it
  `Revise` (mirroring what Analytics' `Revise` task used to be). This is
  new contract surface in `agents/intent/contract.py`: a prompt, a
  response shape, and — this is the part that used to not exist because
  Intent never decided `proceed` — **a real fail-closed fallback**. If
  Intent's substrate is unusable or its answer is unparsable while
  revising after a Security red, the deterministic fallback has to fail
  *toward not acting*, the same posture Analytics' `Revise` fallback has
  today. Intent's contract currently has no such asymmetry anywhere
  (documented explicitly in `contract.py`'s module docstring as "no
  fail-closed asymmetry... Intent never decides proceed") — that
  docstring becomes false the moment this ships, and needs rewriting
  alongside the code, not after.
- `recovery/bootstrap.py`'s wiring changes: Security's red output no
  longer targets Analytics; it targets Governance, which targets Intent.
- Everywhere Phase 0.4's code and docs currently assert "Intent holds no
  veto" as a *safety* property (README, `docs/phase-0.4-intent.md`,
  `contract.py` module docstring, the as-built doc mirrored to the
  Project) needs a v0.35 correction pass — not just new code sitting
  next to stale claims.

## 6. The UI concept (context for why the keyword-format contract matters)

Described as a companion app idea, not blocking the backend work but
explaining *why* Personality/Knowledge's output format is specified so
precisely: an avatar with facial expressions driven by Impulse's live
reflex, a "thought bubble" that types out in three colors (one per
Analytics/Personality/Knowledge) so the reasoning feels visible before
the avatar speaks, a security-fail icon with its own bubble when a
red-flag revision is happening, and the final speech bubble is Intent's
output specifically, with the thought bubble persisting (faded, not
gone) rather than disappearing. Noted here for completeness; the backend
contract (§2's shared keyword format) is what actually needs to exist for
this to be buildable later — nothing in this section is a near-term
build item.

## 6a. Intent splits into two agents: Intent (live) and Consolidator

Superseding §7.1's original fleet/rotation model (`Awake → Consolidating →
ReadyToSwap` inside one class) entirely. Confirmed reasoning, in order:

- **The context-window question exposed the real issue.** Every substrate
  call is stateless (no provider-side memory across calls — see the
  chat-log discussion this design came out of); anything Intent "remembers"
  has to be resent, in full, on every call, by ECI's own code. Prompt
  caching (not yet implemented anywhere in `substrates/`) can make
  resending a stable, append-only prefix cheap, but it never removes the
  need to resend it. Given that, running Intent's live-voicing path and
  its slow, occasional reconciliation pass as one object with an internal
  mode switch buys nothing — both are just "assemble a prompt and call a
  substrate," and the mode switch was only there to decide *which* prompt.
- **So: two single-purpose agents**, matching every other role's shape
  (one job each) instead of one role with two jobs:
  - **Intent** — always active (no more Awake/Consolidating/ReadyToSwap
    state machine, no N=1 "pause" special case). Voices the bundle,
    same ADVISE/REFUSE/REVISE contract as before. Persona (Core Anchors +
    Evolving Trait Delta) is hydrated once and cached in memory — refreshed
    only when Consolidator produces a new epoch — never read from Archive
    per-event (§6b resolves the mechanism).
  - **Consolidator** (NEW) — the entire former "Consolidating" job, on its
    own. Owns `reconcile()`, the batch-size trigger, and the write to
    Archive. Nothing about Option B (§1, Archive stays a dumb executor,
    the reasoner decides content) changes — it just now belongs to
    Consolidator instead of to a mode of Intent.
- **What Consolidator actually receives, and from where — corrected
  THREE times now; this is the settled version, for token-cost reasons
  that aren't going to change:** NOT a direct subscription to the Sensory
  fan-out (superseded — §6a's first draft). NOT three separate
  incremental hand-offs streamed as each piece becomes known (superseded
  — the immediately-preceding draft of this section; it optimized for
  "Consolidator doesn't wait," but Consolidator was never meant to be
  fast, and three separate hand-offs means either three separate
  reasoning passes — paying the fixed prompt overhead three times — or
  awkward held state between them. Neither is worth it for a component
  whose entire design point is running rarely, off the live path).

  **Settled: one bundle per event, sent once Action completes**, from
  Governance. Contents:
  - `event_id` (correlation/dedup key)
  - The **Sensory input**, verbatim
  - The **Security outcome** — whether it ever went yellow/red, and if
    so what triggered it (the concern text) and what Intent tried at
    each pass (the full revision arc, however long it ran)
  - **Intent's final concluded output** — the version that actually
    cleared and reached Action

  Explicitly excluded (unchanged from earlier drafts): Impulse's reflex
  reading, Analytics' own recommendation text, Personality's/Knowledge's
  per-event findings — all redundant for Consolidator's purposes (§2,
  §3). Consolidator only ever hears from Governance, never directly from
  the other cognitive agents, and never mid-event — only once the whole
  thing has resolved.

  This also closes out the "does Governance need an early direct Sensory
  subscription" question from the streamed-version draft — moot now,
  since nothing is forwarded early. Governance already has everything it
  needs by the time the event concludes at Action.

  - **Source determines destination, as a default, not an absolute
    rule**: Sensory-sourced content → Knowledge, Intent-sourced content
    → Personality. Consolidator can still override for an obvious misfit
    (e.g. Sensory content that's really identity-relevant feedback about
    Intent's own behavior, not a fact about the world) — the rule
    removes the *common* judgment call, it doesn't remove judgment
    entirely.
  - Consolidator batches these per-event bundles itself and triggers
    `reconcile()` at its own batch-size threshold, same mechanism as
    today's `_events_since_consolidation` counter — reasoning over the
    whole accumulated batch in **one call**, which can emit **multiple
    write instructions**, not just one. Each instruction fully specifies
    its own destination (store + kind/tag — e.g. `knowledge:general`,
    `knowledge:security`, `identity:epoch`) and its content, so Archive
    has nothing left to decide, only to execute — Option B (§1) taken to
    its natural conclusion: one reasoning pass, N mechanical writes.
- **No shared mutable state between Intent and Consolidator** — they
  never touch each other's memory. Intent's cached persona and
  Consolidator's batch buffer are each private to their own process.
  The only shared, durable thing is Archive, written only by
  Consolidator, exactly as Option B already established.
- **Not yet resolved: whether the heavy `reconcile()` call needs to run
  off the main synchronous dispatch path.** The embedded bus dispatches a
  publish to every subscriber's handler in turn before returning — so on
  the one event where Consolidator's batch threshold trips, that event's
  response to the human still waits on Consolidator's slow call to finish,
  same problem as before, unless that call is explicitly deferred (e.g.
  to a background thread). Splitting the agents makes this a clean,
  isolated fix (only Consolidator's handler would need it) but it's a
  separate decision, not yet made.

## 6a-2. Surfacing what Consolidator learns

Not gated speech through Security/Action for the routine case — that
idea (an earlier draft of this section) is superseded. Instead:

- **Every Consolidator write** — regardless of destination store or
  importance — pings a passive icon in the UI ("learned something,"
  no interruption, nothing spoken). Clicking it opens a timestamped log
  of everything learned. Purely informational; doesn't touch the
  reactive pipeline at all.
- **A smaller "important" subset gets proactively surfaced** to the
  user — Consolidator (or something) does an importance evaluation, not
  every entry qualifies. Eligible sources: **Knowledge-derived entries
  only** — both plain Sensory-sourced facts and security events. **Never
  Personality-derived entries** (Intent's own concluded actions) — the
  user already lived through those live, so re-announcing them is pure
  redundancy, logging-only, no exceptions.
  - **Settled — no, it does NOT go through Security/Action.** Explicitly
    confirmed: "that's harmless." An important learned item surfaces as a
    small notification on the avatar itself ("I learned the skill" / "I
    learned that the mother's name was Maria") — not spoken through
    Intent's voice, not gated, not routed through the reactive pipeline
    at all. This is a genuinely different channel from everything else
    in this document: every other piece of content reaching the human
    goes through Governance → Security → Action with no exceptions (§1);
    this is the one deliberate exception, justified by it being a passive
    notification *about* a memory write rather than a new utterance
    addressed to the human — the earlier "treat it like idle musing"
    instinct was wrong, and worth naming as wrong rather than quietly
    dropped: idle musing is Intent speaking unprompted, still gated;
    this is Consolidator's activity becoming visible, never spoken at
    all, so there's nothing for Security to clear.
  - **The carve-out is narrower than it first looks, and stays that way
    on purpose.** It only ever covers the bare notification glyph. The
    moment the user *clicks* it, that click is ordinary Sensory input —
    "the user reviewed [this learned item]" is exactly as much a Sensory
    event as anything else that enters the system — and it re-enters
    through the normal front door: fans out to the four working agents
    same as any event, and whatever Intent says back about it goes
    through the full Governance → Security → Action circuit, gated like
    any other reply. So the "everything reaching the human is
    Security-cleared" guarantee is never actually broken — only a
    content-free icon is exempt; any words generated in response to a
    human interacting with it are fully in-pipeline, fully gated.

## 6b. Persona caching (Intent side)

Intent's persona (Core Anchors + Evolving Trait Delta) is hydrated once
— at bootstrap, and again only right after Consolidator writes a new
epoch — and held in memory. Every live voicing call uses the cached copy;
Intent never queries Archive itself while active. This is a real change
from Phase 0.4's `IntentAgent.voice()`, which currently calls
`self.hydrate()` (and therefore `archive.query("identity")`) on *every*
call — that per-event Archive read goes away entirely under v0.35, since
Personality now supplies the per-event identity-grounded context Intent
used to fetch itself.

## 7. Still open / not yet asked

- Whether Personality and Knowledge are mocked-first the way every other
  role has been (§13.1's "mock every role, replace one per phase"
  discipline) or whether, being read-only retrieval with no gating power,
  they get a lighter-weight bring-up. Following precedent says mock
  first; worth confirming rather than assuming given how much else in
  this revision is already a deliberate precedent break.
- Exact shape of Intent's "broader conversation context" input (§3) —
  how many recent temp-log turns, bounded how, rendered how in the
  revision prompt.
- Whether the shared Personality/Knowledge base class (§2) is worth
  building now or whether two direct implementations are fine until a
  third archive-grounded agent actually shows up.
