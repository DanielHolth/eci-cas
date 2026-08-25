# ECI-spec-v0-35.md

**Version:** 0.35
**Status:** IMPLEMENTED, 2026-08-24. Every section below is built and
tested — see [`docs/phase-0.5-v0-35.md`](phase-0.5-v0-35.md) for the
as-built record, including four decisions Daniel took during
implementation that this document left open (and one place where he
widened v0.35e beyond what is written here: Analytics is isolated from
Security in EVERY way, so both non-green lanes route to Intent, not just
red). The "Still open" list at the end is resolved — see the
Resolutions section appended below it.
**Supersedes:** v0.34 (Phase 0.4 complete: Governance, Analytics, Impulse,
Intent all real; Security and Action still mocked)
**Last Updated:** 2026-08-24

---

## Executive summary

v0.35 is a deliberate, large break from the topology Phase 0.4 finished.
Confirmed explicitly by Daniel, more than once, as intentional — not
something to talk him out of, just something to get exactly right on
paper before any code changes. Four things change:

1. **Sensory fans out to four parallel agents** (Impulse, Analytics, and
   two new ones — Personality, Knowledge) with no Governance hop on that
   one fan-out. Governance buffers all four and bundles them for Intent.
2. **Two new agents join the roster** — Personality and Knowledge — a
   single reusable *class* of read-only, archive-grounded, single-event
   lookup agent, differing only in which Archive store they query.
3. **Governance becomes the universal router.** Every hop *except* the
   Sensory fan-out passes through it, including the Critical-severity
   reflex path (no longer a bypass) and Security's red-verdict loop.
4. **Security's red verdict now routes to Intent, not Analytics** — and
   Intent gains a real veto. This is the one change that reverses a hard
   safety invariant v0.34/Phase 0.4 was built around ("Intent holds no
   veto," §5.5). It is confirmed, explicit, and reasoned, not an
   oversight: Intent's revision is grounded in the full bundle plus
   broader conversation context none of the single-event agents have.

A fifth, related change that fell out of a side discussion about token
cost and LLM statelessness: **Intent's fleet/rotation model
(`Awake → Consolidating → ReadyToSwap`) is replaced entirely** by two
single-purpose agents — Intent (always active, voices the bundle) and a
new Consolidator (periodic, writes to Archive). Nothing about who decides
what to write changes (the reasoner decides content, Archive stays a
dumb executor — a discipline this document calls **Option B** throughout,
carried over unmodified from Phase 0.4's actual `_consolidate()`
behavior); the reasoning that used to run as a mode of Intent now runs
in its own agent.

---

## v0.35a: Sensory fans out to four parallel agents, no Governance gate

**Change.** Sensory input goes simultaneously to four agents — Impulse,
Analytics, Personality, Knowledge — each getting its own copy, in
parallel. **This is the one hop in the whole pipeline with no Governance
in between**, confirmed explicitly and repeatedly as deliberate, not an
oversight in an otherwise-universal-Governance design (see v0.35c).

**Why.** Daniel's stated goal is genuine parallelism for latency: four
short, cheap, independent calls racing in parallel beat one long call (or
a serial chain) doing all four jobs. This is also consistent with how
every cognitive call in the system already works — stateless per call,
no cross-call memory unless the calling code reconstructs it — so nothing
is lost by fanning these four out independently; none of them need to
see each other's answers to do their own job.

**Governance's role at this hop.** It buffers/waits for all four to
answer, then sends **one bundled message to Intent**. See v0.35c for how
this composes with Governance's role everywhere else, and v0.35d for the
one path that skips this fan-out entirely (Critical severity).

---

## v0.35b: Two new agents — Personality and Knowledge

**Change.** Two new roles, built as **one reusable agent class**, not two
hand-copied implementations — confirmed explicitly ("don't think
Personality and Knowledge are the only ones of this character... they
could all go under some kind of knowledge agents") as likely to grow into
a family later.

**Shared contract, both agents:**

- **Read-only.** Query Archive, never write. Writing new knowledge or
  identity content stays a Consolidator job (v0.35f) — unchanged in
  principle from Phase 0.4/v0.32's Memory Model table, which already
  named Intent (now Consolidator) as the sole writer.
- **Single-event scope.** No cross-event memory of their own, no
  persona, no values. Purely "what does the archive say that's relevant
  to *this* event."
- **Output format matches Analytics' existing terse keyword style** —
  explicit and load-bearing. The (deferred, product-layer) avatar UI
  concept shows three colored "thought bubble" streams — Analytics,
  Personality, Knowledge — and Intent is described as pattern-matching on
  a shared keyword format across all three, not parsing three different
  shapes.

**Personality** — queries Archive's **identity** store (Core Anchors,
Evolving Trait Delta, epochs — the same store `agents/intent/base.py`'s
`hydrate()` reads today). Contributes identity/values-relevant findings
about the current event: the situational counterpart to what Intent used
to self-hydrate for its own persona rendering. Under v0.35, **Intent no
longer reads Archive itself at all while active** (v0.35g/§persona
caching) — Personality's per-event output is what replaces that direct
read.

**Knowledge** — queries Archive's **knowledge** store (declared in the
Memory Model table since v0.32, unused by any agent until now).
Retrieval only: facts, people, places, stories. Daniel's framing: *"local
knowledge — I know these people, I know that story"* — explicitly **not**
worldly/parametric knowledge.

**Analytics stays unique, deliberately.** It touches neither Archive
folder these two read. It keeps its existing rolling working-queue
window (loop/trend detection, unchanged) but leans on its own
parametric/pretrained ("worldly") knowledge and stays neutral rather than
character- or memory-colored. This is the real dividing line between
Analytics and the new pair: *worldly reasoning* vs. *local/archive-
grounded retrieval*.

**Implementation note, not yet decided:** a shared base class
(`agents/archive_lookup/base.py`?) parameterized by which Archive `kind`
each instance queries and its system instruction, mirroring how
`agents/intent/` already splits `base.py` (shared) from tier-specific
files — the natural shape for the family Daniel expects to grow. Whether
to build this now or wait for a third archive-grounded agent to actually
show up is open (see "Still open," below).

---

## v0.35c: Governance becomes the universal router

**Change.** Every hop in the pipeline passes through Governance —
**except** the one Sensory→four-agents fan-out (v0.35a). This was
Daniel's original ask from the start of this design conversation
("governance is the place to reroute things... just in case we need to
alter something in the future") and it's confirmed to survive everything
else that changed: Governance sits between Intent and Security, between
Security and the revision loop, between the revision loop and Action, and
(v0.35d) on the Critical fast path too.

**Governance's job at the one fan-out point it doesn't gate:** buffer
all four parallel answers (Impulse, Analytics, Personality, Knowledge),
then send one bundled message to Intent once all four have reported.

**What's in Intent's bundle, and what changes for Analytics:** Analytics
keeps reasoning and keeps setting `proceed`/`concern` exactly as it does
today — **but its answer no longer goes to Governance's dispatcher for
Security clearance directly** (today, Analytics' `proceed: false` is what
tells Governance to route toward a decline). Instead Analytics' answer
becomes one of four inputs Governance bundles for Intent, alongside
Personality's and Knowledge's keyword findings and Impulse's reflex.
Intent still reads `proceed`/`concern` from Analytics' slot in the
bundle — that part of the ADVISE/REFUSE contract is unchanged, just
better-grounded.

**Intent also gets broader context the other three don't have.**
Analytics/Personality/Knowledge only ever see the single current event.
Intent has visibility into the ongoing conversation — this already
exists in shape (Intent's temp log is the only cross-event state in the
system) but Intent's per-event voicing call doesn't currently read it.
Extending it to do so — at least for recent turns — is implied but **not
yet specced precisely**: exact window size, bounding, and rendering into
the prompt are open (see "Still open").

---

## v0.35d: Critical severity routes through Governance too — not a bypass

**Change.** The Critical-reflex fast path named-but-deferred since v0.34
(`Impulse → Governance → Security → Action`, skipping Analytics/Intent
for a genuine emergency) is confirmed as still routing through Governance
— explicitly, after an initial dictation ambiguity about whether it
should skip Governance entirely. It does not skip the bundle/Analytics/
Personality/Knowledge/Intent fan-out and voicing step; it skips *straight
to Security* on the way in, exactly as v0.34 designed.

**What's new under v0.35:** if Security reds a Critical-path action, it
now loops back through Governance to **Intent** for revision (v0.35e) —
where previously there was no revision path for the Critical reflex at
all (v0.34 never specified one, since Analytics/Intent were skipped on
this path by design). Intent is now in the revision loop for every red
verdict, Critical-origin or not.

---

## v0.35e: Security red → Intent revises, not Analytics. Intent gains a real veto.

**This is the one change that reverses a hard safety invariant Phase 0.4
was built around.** Stated plainly, not softened:

**Before (v0.34/Phase 0.4, current shipped code):** Security red →
Governance → **Analytics** revises → Governance → Security (loop). Intent
is "advisory only... holds no veto" (§5.5) — nothing Intent says can
change whether an action happens. `agents/intent/contract.py`'s entire
fallback design leans on this: there is no fail-closed/fail-open
asymmetry in Intent's contract *because* Intent has nothing to gate. This
is documented, in the shipped code's own module docstring, as a
deliberate safety property.

**After (v0.35, confirmed explicitly, multiple times, unambiguous):**
Security red → Governance → **Intent** revises. "We have to sever the
connection between analytics and security... the whole thing goes over
to intent now." Analytics is severed from Security entirely.

**Daniel's stated rationale, in full, because it matters for anyone
reviewing this later:** Intent's revision is better grounded than a fresh
Analytics call would be, not worse. By the time Security reds something,
Intent already has Analytics' + Personality's + Knowledge's original
answers (from the bundle it voiced from) *and* broader conversation
context the single-event agents never see. "This gives intent... more
grounded decision-making when the security flags red. This is an
improvement at least in my eyes." An explicit, reasoned trade — not an
oversight, and not something the next implementer should try to talk him
out of or quietly walk back.

**What this actually requires to build, precisely, so nothing gets
glossed over in implementation:**

- Intent needs a **third task**, not just ADVISE/REFUSE — call it
  `Revise` (mirroring what Analytics' `Revise` task used to be). New
  contract surface in `agents/intent/contract.py`: a prompt, a response
  shape, and — the part that genuinely didn't exist before, because
  Intent never decided `proceed` — **a real fail-closed fallback**. If
  Intent's substrate is unusable or its answer is unparsable while
  revising after a red, the deterministic fallback must fail *toward not
  acting*, the same posture Analytics' `Revise` fallback has today.
- `recovery/bootstrap.py`'s wiring changes: Security's red output no
  longer targets Analytics; it targets Governance, which targets Intent.
- **Every place Phase 0.4's shipped code and docs currently assert
  "Intent holds no veto" as a safety property needs a v0.35 correction
  pass, not new code sitting next to a stale claim.** Specifically:
  `agents/intent/contract.py`'s module docstring ("no fail-closed
  asymmetry... Intent never decides proceed" — becomes false), `README.md`,
  `docs/phase-0.4-intent.md`, and the as-built doc mirrored to the
  Project (`claude/phase-0.4-intent-as-built.md`).

---

## v0.35f: Intent splits into two agents — Intent (live) and Consolidator

**Change.** The `Awake → Consolidating → ReadyToSwap` fleet/rotation
model inside one Intent class (§7.1, unchanged since v0.32, never
exercised past N=1) is replaced entirely by **two single-purpose
agents**, matching the shape of every other role in the system:

- **Intent** — always active. No more state machine, no N=1 "pause"
  special case. Voices the bundle (ADVISE/REFUSE/REVISE). Persona is
  cached in memory, not read from Archive per-event (see v0.35g's
  "persona caching" subsection).
- **Consolidator (NEW)** — the entire former "Consolidating" job, on its
  own. Owns the equivalent of `reconcile()`, the batch-size trigger, and
  the write to Archive. **Nothing about who decides what to write
  changes** — the reasoner decides content, Archive stays a dumb
  executor (**Option B**, carried over unmodified from Phase 0.4's actual
  `_consolidate()`/`reconcile()` behavior, and explicitly re-confirmed
  during this design pass after being weighed against the alternative of
  making Archive itself a reasoner — rejected because it would mean
  re-deriving context Intent/Consolidator already has, twice, at real
  token cost, for no benefit).

**Why this is correct, not just tidy — the actual argument, worth
keeping for whoever reviews this later:** every substrate call in this
system is stateless. There is no provider-side memory between calls;
anything "remembered" has to be resent, in full, by ECI's own code, on
every single call. Prompt caching (not yet implemented anywhere in
`substrates/`) can make resending a stable, append-only prefix cheap, but
it never removes the need to resend it. Given that, running Intent's
live-voicing path and its slow, occasional reconciliation pass as one
object with an internal mode switch bought nothing — both were just
"assemble a prompt, call a substrate," and the mode switch only decided
*which* prompt. Splitting them costs nothing extra and gets Consolidator
out of Intent's way entirely.

**No shared mutable state between Intent and Consolidator.** They never
touch each other's memory. Intent's cached persona and Consolidator's
batch buffer are each private. The only shared, durable thing between
them is Archive, written only by Consolidator.

**Not yet resolved: whether Consolidator's heavy reconciliation call
needs to run off the main synchronous dispatch path.** The embedded bus
dispatches a publish to every subscriber's handler in turn before
returning — so on the one event where Consolidator's batch threshold
trips, that event's response to the human would still wait on
Consolidator's slow call to finish, unless that call is explicitly
deferred (e.g. to a background thread). Splitting the agents makes this
an isolated, clean fix later (only Consolidator's handler would need it)
but it's a separate decision, genuinely not yet made — see "Still open."

---

## v0.35g: Consolidator's data flow

**What Consolidator receives, and from where.** This went through three
drafts during the design conversation before settling, purely on
token-cost grounds — worth recording why the earlier two were rejected,
so nobody re-proposes them without re-deriving the same conclusion:

1. *Rejected:* Consolidator subscribes directly to the raw Sensory
   fan-out (v0.35a), same as the four working agents. Doesn't work —
   Consolidator needs pipeline-*final* information (what Intent
   ultimately said, how many revision passes it took), which doesn't
   exist yet at the moment Sensory fans out.
2. *Rejected:* Governance sends Consolidator three separate, incremental
   hand-offs per event, each fired the instant that piece of information
   exists (Sensory input immediately; Security verdict when known;
   Intent's final output when settled). Optimized for "Consolidator
   doesn't wait" — but Consolidator was never meant to be fast, and three
   separate hand-offs means either three separate reasoning passes
   (paying fixed prompt overhead three times) or awkward held state
   between them. Not worth it for a component whose entire design point
   is running rarely, off the live path.

**Settled: one bundle per event, sent once Action completes, from
Governance.** Contents:

- `event_id` (correlation/dedup key)
- The **Sensory input**, verbatim
- The **Security outcome** — whether it ever went yellow/red, and if so
  what triggered it (the concern text) and what Intent tried at each pass
  (the full revision arc from v0.35e, however long it ran)
- **Intent's final concluded output** — the version that actually cleared
  and reached Action

Explicitly excluded: Impulse's reflex reading, Analytics' own
recommendation text, Personality's/Knowledge's per-event findings — all
redundant for Consolidator's purposes (v0.35b, v0.35c: those agents only
ever surface things Archive already has, or stay deliberately neutral and
never touch Archive at all). Consolidator only ever hears from
Governance, never directly from the other cognitive agents, and never
mid-event.

**Source determines destination — a default, not an absolute rule:**
Sensory-sourced content → **Knowledge** store; Intent-sourced content
(its concluded output, including the reasoning behind any revision) →
**Personality**/identity store — "the action is more personal... it
didn't come from the outside world, it came from the agent based on its
reflections." Security events specifically are tagged and filed under
**Knowledge**, sub-tagged `security` — confirmed explicitly after Daniel
initially hesitated on this one point. Consolidator may still override
the default for an obvious misfit (e.g. Sensory content that's really
identity-relevant feedback about Intent's own behavior) — the rule
removes the *common* judgment call, not judgment entirely.

**Batching and output shape.** Consolidator batches these per-event
bundles itself and triggers its reconciliation pass at its own
batch-size threshold (same mechanism as today's
`_events_since_consolidation` counter) — reasoning over the whole
accumulated batch in **one call**, which can emit **multiple write
instructions**, not just one. Each instruction fully specifies its own
destination (store + kind/tag — e.g. `knowledge:general`,
`knowledge:security`, `identity:epoch`) and its content, so Archive has
nothing left to decide, only to execute. This is Option B taken to its
natural conclusion: one reasoning pass, N mechanical writes.

**Persona caching (Intent side).** Intent's persona (Core Anchors +
Evolving Trait Delta) is hydrated once — at bootstrap, and again only
right after Consolidator writes a new epoch — and held in memory. Every
live voicing call uses the cached copy; **Intent never queries Archive
itself while active.** This is a real, deliberate change from Phase
0.4's shipped `IntentAgent.voice()`, which currently calls
`self.hydrate()` (and therefore `archive.query("identity")`) on *every*
call. That per-event Archive read is removed entirely under v0.35 —
Personality now supplies the per-event identity-grounded context Intent
used to fetch itself, and Core Anchors don't change between
consolidation cycles, so re-reading them every event was always wasted
work once Personality existed to cover the situational half of the job.

---

## v0.35h: Surfacing what Consolidator learns — a narrow, deliberate exception to universal gating

**The routine case: no gating at all, because it isn't speech.** Every
Consolidator write pings a passive icon in the (deferred, product-layer)
UI — no interruption, nothing spoken, purely informational. Clicking it
opens a timestamped log of what was learned.

**The "important" case:** a smaller subset of writes gets proactively
surfaced to the user rather than just logged — an importance evaluation
decides which. Eligible sources: **Knowledge-derived entries only**
(plain Sensory-sourced facts, and security events) — **never
Personality-derived entries** (Intent's own concluded actions), since the
user already lived through those live and re-announcing them is pure
redundancy.

**Settled, after an explicit back-and-forth worth recording precisely:
even the "important" case does NOT go through Governance → Security →
Action.** "That's harmless." An important learned item surfaces as a
notification directly on the avatar — "I learned the skill" / "I learned
that the mother's name was Maria" — never spoken through Intent's voice,
never gated, never entering the reactive pipeline. This is the **one
deliberate exception** to the otherwise-universal rule that everything
reaching the human is Security-cleared (v0.35c). An earlier instinct
during this design pass to treat it like Impulse's deferred "idle
musing" concept (self-originated content, still gated) was wrong, and is
worth naming as wrong rather than quietly dropped: idle musing is Intent
speaking unprompted, and stays gated; this is Consolidator's activity
becoming visible on the UI, never spoken at all, so there's nothing for
Security to clear.

**Why the exception doesn't actually weaken the safety guarantee.** It's
narrower than it first looks. It only ever covers the bare notification
glyph. The moment the user *clicks* it, that click is ordinary Sensory
input — "the user reviewed [this learned item]" is exactly as much a
Sensory event as anything else entering the system — and it re-enters
through the normal front door: fans out to the four working agents
(v0.35a) same as any event, and whatever Intent says back about it goes
through the full Governance → Security → Action circuit, gated like any
other reply. So the "everything reaching the human is Security-cleared"
invariant is never actually broken — only a content-free icon is exempt;
any words generated in response to a human interacting with it are fully
in-pipeline, fully gated.

---

## What the ecosystem looks like now (design, not yet built)

| Role | Tier | Notes |
|---|---|---|
| Sensory | Deterministic | Fans out to 4 agents, no Governance gate (v0.35a) |
| Impulse | Deterministic | Unchanged; also feeds the Critical fast path (v0.35d) |
| Analytics | Cognitive | Answer now bundled by Governance, not routed directly (v0.35c) |
| **Personality** (NEW) | Cognitive, archive-grounded | Reads identity store only, never writes (v0.35b) |
| **Knowledge** (NEW) | Cognitive, archive-grounded | Reads knowledge store only, never writes (v0.35b) |
| Governance | Deterministic | Universal router — every hop but the fan-out (v0.35c) |
| Intent | Cognitive | Always active, no fleet/rotation; gains `Revise` task and a real veto (v0.35e, v0.35f) |
| **Consolidator** (NEW) | Cognitive | Absorbs Intent's former reconciliation job; sole Archive writer (v0.35f, v0.35g) |
| Security | Deterministic | Red now routes to Intent, not Analytics (v0.35e) — otherwise unchanged |
| Action | Deterministic | Unchanged |
| Archive | Deterministic | Unchanged in posture — dumb executor, multi-instruction writes now possible per call (v0.35g) |

Ten roles (up from eight), three cognitive (up from two: Analytics,
Personality, Knowledge, Intent, Consolidator — five, actually, once
Personality/Knowledge/Consolidator are counted, up from two).

---

## Sections superseded

| Section | Change |
|---|---|
| §3.2 (routing) | Sensory fans out to 4 parallel agents; Governance is no longer "sole trigger from Impulse" — it aggregates 4 inputs |
| §5.1 Governance | Gains a buffering/bundling job at the one fan-out hop; otherwise its "universal router" scope is now explicit and total |
| §5.4 Analytics | Output no longer routes directly to Security; becomes one bundled input to Intent |
| §5.5 Intent | Loses "advisory only... holds no veto" (v0.35e) — gains a real veto via the new `Revise` task on Security red. Loses the N-node fleet/rotation model (v0.35f) — always active, no states |
| §5.6 Security | Red verdict routes to Governance→Intent, not Governance→Analytics |
| §6 Memory Model | Identity tier write-ownership moves from Intent to Consolidator; Knowledge tier gains an active writer (previously declared, unused) |
| §7 (all) | The entire Intent Lifecycle/Rotation/Consolidation chapter is superseded by v0.35f/g — Consolidator replaces node states entirely |
| New roles | Personality, Knowledge, Consolidator have no existing section — net-new |

## What didn't change

- The bus architecture and embedded pub-sub mechanics
- Security's closed-enum verdict (`green | yellow | red`) and its rule-based, stateless-per-event design
- Action's role as the only door to the world
- Archive's "dumb executor" posture (Option B) — reinforced, not weakened, by this revision
- The substrate layer and substrate-class indirection (§10.2)
- The validated-contract-with-deterministic-fallback discipline for every cognitive call

---

## Still open — not yet asked, or asked and not yet answered

1. **Whether Personality and Knowledge are mocked-first**, following
   §13.1's "mock every role, replace one per phase" discipline, or
   whether — being read-only retrieval with no gating power — they get a
   lighter-weight bring-up. Precedent says mock first; worth confirming
   given how much of this revision is already a deliberate precedent
   break.
2. **Exact shape of Intent's "broader conversation context" input**
   (v0.35c) — how many recent turns, bounded how, rendered how in the
   revision prompt.
3. **Whether the shared Personality/Knowledge base class** (v0.35b) is
   worth building now, or whether two direct implementations are fine
   until a third archive-grounded agent actually shows up.
4. **Whether Consolidator's heavy reconciliation call runs off the main
   synchronous dispatch path** (v0.35f) — i.e. whether it needs real
   threading to avoid pausing the live response on the event that trips
   its batch threshold, or whether an occasional brief pause there is
   acceptable for now.
5. **Whether Security's concern text rides along with each rejected
   revision iteration**, or whether Consolidator infers purely from the
   iteration count and Intent's own reasoning — raised early in the
   design conversation, never explicitly closed (superseded in practice
   by v0.35g's settled bundle, which does include the concern text, but
   worth a final explicit confirmation rather than treating it as
   implied).

---

## Handover — for whoever implements this, in a fresh session

**This document is design-complete, not implementation-started.** Every
decision above was reached through a long, iterative, voice-dictated
conversation with Daniel, with several genuine reversals along the way
(the Consolidator hand-off mechanism alone went through three different
designs before settling — see v0.35g). Where a decision is marked
"settled" or "confirmed" above, it survived at least one round of
Daniel correcting or re-deriving it — treat those as load-bearing, not as
a first draft to second-guess. Where something is marked "Still open"
(the five items directly above), it is genuinely unresolved — ask Daniel
before assuming an answer, the same way earlier phases of this project
consistently did.

**Where things stand in the actual codebase right now (as of v0.34/Phase
0.4, unaffected by anything in this document):** 8 real roles, Phase 0.4
complete — Sensory, Governance, Analytics, Impulse, Intent all real;
Security and Action still mocked. Full offline suite: 288 passed, 13
skipped, one pre-existing unrelated failure
(`test_budget_mode.py::TestManifestConfig::test_it_reads_the_shipped_manifest`,
a spend-cap value mismatch that's Daniel's own live-testing config choice,
not a bug — leave it alone). This is the actual starting point for
implementation; **nothing in v0.35 has been written into code, only into
this document and the working draft it was built from**
(`docs/ideas/v0-35-parallel-fanout-draft.md` — same content, rougher
form, kept as the paper trail of how each decision was reached; this
document is the clean version to build from).

**This breaks Phase 0.4 substantially.** Three existing roles change
shape (Analytics — output redirected, not re-reasoned; Security — red
path retargeted; Intent — gains a task, loses its rotation model and its
"no veto" invariant). Two roles are net-new (Personality, Knowledge). One
role is net-new by way of splitting an existing one in half (Consolidator,
carved out of Intent). Expect this to touch: `recovery/bootstrap.py`
(wiring for the new fan-out, the new agents, and Security's retargeted
red path), `manifests/ecosystem-manifest.yaml` (new roles, new
`roles.governance` bundling config, `roles.intent` losing its
`nodes`/rotation shape), `agents/intent/*` (a real `Revise` task with a
fail-closed fallback — new — and losing `base.py`'s node-state
machinery), a new `agents/personality/` and `agents/knowledge/` (possibly
sharing a base class per item 3 above), a new `agents/consolidator/`, and
every test fixture across the suite that currently boots the shipped
manifest and pins `roles.*.mock` flags (the same pattern Phase 0.2 and
0.4 each established when a role went live — grep for
`roles.intent.mock` and `roles.analytics.mock` across `tests/` for the
precedent to follow).

**Suggested build order**, following this project's established
per-phase discipline (mock everything new first, replace one piece at a
time, test after every change, `python -m pytest tests/ -q` before
declaring anything done): start with the Consolidator split (v0.35f/g) —
it's the most self-contained piece, has the clearest existing precedent
to mirror (Phase 0.4's actual `_consolidate()`/`reconcile()` code is
almost the exact logic Consolidator needs, just moved into its own
class), and doesn't require the new fan-out or the Security-retargeting
to be useful in isolation (Intent can keep calling a Consolidator
directly, in-process, before the full four-way fan-out exists). Then the
Personality/Knowledge pair (v0.35b) — mock-first per item 1 above, worth
confirming with Daniel before writing real retrieval logic. Then the
fan-out and Governance bundling (v0.35a/c) once Personality/Knowledge
exist to fill two of its four slots. Last, and most carefully: the
Security-red retargeting to Intent (v0.35e) — this is the safety-critical
change, wants its own focused pass and its own test file, the same way
Phase 0.1's Governance-determinism change and Phase 0.3's Impulse
severity-ceiling change each got dedicated scrutiny before shipping.

**Working agreements that carried this project through Phases 0.1–0.4,
worth continuing:**

- Read the living spec (`docs/`) before writing code — it evolves, keep
  it in sync with implementation, per this Project's own core working
  principles.
- Write a dedicated test file per phase (`tests/test_phase0X_*.py`),
  mirror the as-built doc to both `docs/` and the Project
  (`claude/phase-0.X-*-as-built.md` via `Projects.project_write`) once
  something is actually built and tested — not before.
- Ask Daniel before resolving anything in "Still open" above. He answers
  in detail, changes his mind mid-thought fairly often (see v0.35g's
  three drafts), and consistently corrects course when something's
  actually wrong rather than just different from what was proposed —
  that pattern held throughout this entire design conversation and is
  worth trusting rather than second-guessing.
- Test before declaring done: `python -m pytest tests/ -q` after every
  change. The 288/13/1 baseline above is what "no regressions" means
  until this phase's own test file adds to it.
- Device bridge (Claude desktop app, remote-devices tools) can disconnect
  mid-session — if a commit back to `D:\Dev\Claude\eci-cas` fails, say so
  plainly, deliver via `SendUserFile` only, and check with
  `device_list_dir` before retrying rather than assuming reconnection.

---

## Resolutions — how the five open items were answered

Answered by Daniel on 2026-08-24, during implementation. Recorded here
so this document stops reading as unresolved; the reasoning and the code
they became are in [`docs/phase-0.5-v0-35.md`](phase-0.5-v0-35.md).

1. **Personality/Knowledge mocked first?** YES — precedent won (§13.1).
   Built as one class with two configurations, mock tier only; the live
   retrieval tier is the next phase's work.

2. **Intent's broader conversation context.** The last N CONCLUDED
   EVENTS, tier-scaled: minimal 1, budget 5, default 10, super 15
   (`roles.intent.context_events`). Whole events by construction — one
   temp-log entry is one event — so a window can never be cut mid-event,
   which was Daniel's specific requirement. Each side truncated to 160
   chars so the window always spans exactly N events.

3. **Shared base class now, or two direct implementations?** NOW.
   `agents/archive_lookup/` — one class, parameterized by store kind,
   topic and brief. A third family member is one more instantiation.

4. **Does Consolidator's reconcile run off the main dispatch path?**
   YES, threaded from day one. A single worker thread serializes
   reconciles; `synchronous: true` runs inline for deterministic tests
   and debugging.

5. **Does Security's concern text ride along?** YES where it exists. The
   rule engine has none yet (the mock only sets `verdict`), so the
   Consolidator bundle carries the verdict, the concern when present, and
   the full revision arc. Intent's prompt says `verdict: red` plainly
   rather than inventing a reason Security never gave.

**One item this document did not anticipate**, added during
implementation: a red verdict buys exactly ONE revision. The model is
told so explicitly, and a second red is an outcome rather than another
loop — a deterministic blocked notice carrying an expression from
Impulse's live appraisal state, a security alert, and a frustration nudge
back into the drive vectors. v0.35 never bounded the revision loop; on a
Security that refuses everything, it would have run forever.

---

*End of revision note v0.35. Design complete; implementation complete
2026-08-24 (Phase 0.5).*
