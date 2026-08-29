# Phase 0.8 — ideas for next iteration (handoff, not a spec)
 
Date: 2026-08-25. Captured verbatim from Daniel at the end of the
0.7-cleanup session (proceed/concern removal, truly-async fan-out — see
`claude/phase-0.7-cleanup-as-built.md`). **Nothing below is designed or
scoped yet.** This is the raw brief for whoever opens the next session,
written down before it's lost, not a spec to start implementing from.
 
---
 
## 0. Where the observation came from
 
After the cleanup, Daniel re-ran a live test. No bugs. But Intent still
reads "jumpy" — the persona doesn't feel settled, even with a clean
bundle and a tightened keyword contract. That observation is what
produced the three ideas below. Worth remembering when scoping this:
the underlying complaint is about Intent's *character*, not its
plumbing — the plumbing is now clean (Phase 0.6 wired the substrate,
0.7 removed the dead gate and fixed the arrival-order leak). "Jumpy"
is a persona/grounding problem, not a bug.
 
---
 
## 1. Swappable personas — "which tamagotchi do I want to play with today?"
 
Daniel's framing, verbatim: the user may swap persona at any time.
Knowledge stays shared across personas (it's "what actually happened" —
facts, people, places, this system's own history). Personality does
not: each persona has its own trait bank, and it only develops while
it's the active one.
 
What this implies, unscoped:
 
- **Consolidation writes to the active persona's identity store, not a
  single shared one.** Right now `agents/consolidator/` writes identity
  epochs to one `data/archive/identity/intent_epochs.json`
  (`agents/intent/contract.py`'s `ANCHORS_EPOCH_ID` etc.) and one
  knowledge store. This would need N identity stores (one per persona)
  and one shared knowledge store — a real change to Archive's `kind`
  namespacing (`agents/archive/store.py`), not just a manifest tweak.
- **Personality (the archive-lookup agent) needs to know which persona
  is active** when it queries the identity store, so its per-event
  findings are grounded in the right trait bank. Today
  `agents/archive_lookup/live.py` queries a fixed `kind="identity"` —
  this would need a persona-scoped kind or a runtime parameter.
- **Intent's cached persona (`PersonaState`, v0.35g) is currently
  hydrated once at bootstrap and refreshed only on `EpochWritten`.**
  Swapping personas mid-session means re-hydrating from a *different*
  store on demand, not just refreshing the same one. Worth deciding
  whether a swap is a Sensory-triggered event (goes through the normal
  pipeline) or an out-of-band admin action.
- **"Only develops while active"** — a persona not currently active
  should not accumulate trait deltas. Consolidator's batch/threshold
  logic (`agents/consolidator/base.py`) would need to gate on "was this
  persona active when the batch's events happened," not just batch
  size.
- Genuinely open: does switching personas create a NEW Intent instance,
  or does the same Intent instance re-hydrate from a different store?
  The latter is cheaper; the former might be cleaner given Intent's
  substrate-resolution-at-construction pattern (`agents/intent/live.py`).
This is the biggest of the three ideas — probably its own phase, not a
quick add.
 
---
 
## 2. Knowledge and Personality should match input to output, not just retrieve
 
Daniel's example: the keywords "hello, name" (from a greeting) don't
add anything to Intent's bundle. But knowing *this is very likely
Daniel* — inferred from the conversation, not stated outright — is
genuinely useful grounding. Same principle applies to Personality: it
should try to match what's actually being said to what it surfaces,
not just report "here's a record that technically matched."
 
This reframes what these two agents are for. Today
(`agents/archive_lookup/contract.py`) they answer "what does the
archive say that's relevant to this event" — a retrieval question. The
ask is closer to an inference question: "given this event, what do I
already know that changes how I should read it" — which is a higher
bar than keyword relevance matching. Concretely, this probably means:
 
- The RESPONSE_CONTRACT's "relevant: true/false" framing may need to
  become something closer to "does this event let me infer something
  useful," not just "did I find a matching record."
  - Note the tension with the module's own explicit design principle
    (`archive_lookup/contract.py`'s docstring): "report what the
    records say, not what you happen to know... never invent a
    record." Inferring "this is likely Daniel" from conversational
    cues is closer to reasoning *about* the records than reporting
    *from* them. Worth a real design conversation before writing code
    — this could quietly turn Knowledge into a second Analytics
    (parametric reasoning) if not bounded carefully, which is exactly
    the failure mode v0.35b's original design was written to prevent.
  - Best evaluated once decision (3) below — a real seed dataset —
    exists, since "does this match well" isn't answerable against an
    empty or thin archive.
## 3. A real mockup dataset for the archive
 
Explicitly flagged by Daniel as "for the handover, not now." The 8
records seeded this session (4 knowledge, 4 personality — see the
earlier conversation turn) were a quick smoke-test, not a real corpus.
Idea (1) and (2) both need substantially more archive content to
actually evaluate against: idea (2)'s "match input to output" quality
can't be judged against a handful of hand-written records, and idea
(1)'s per-persona identity stores will look thin with only the current
starter trait set. Next session should plan to flood the archive with
a much larger, more varied mockup dataset before trying to tune either
agent's matching behavior.
 
---
 
## 4. Test suite bloat — reduce to a bare minimum
 
Separate from the three ideas above, but flagged in the same
conversation: Daniel wants the test suite reviewed and cut down.
Verbatim: "too many tests get dated fast." This session's own cleanup
is a live example — the proceed/concern removal alone required editing
assertions across `test_phase02_analytics.py`, `test_phase04_intent.py`,
and `test_phase05_fanout.py`, and the async fan-out change broke fixed
hop-order assertions in four more files
(`test_phase0_e2e.py`, `test_phase01_governance.py`,
`test_phase04_intent.py`, `test_phase05_fanout.py`) that were pinning
an implementation detail (strict sequential dispatch order) rather than
a real invariant. That pattern — tests asserting exact sequences that
happen to be true today rather than properties that have to stay
true — is probably the main source of the dated-fast complaint. Worth a
pass that specifically asks, per test: is this asserting a contract, or
an accident of the current implementation? The current suite sits
around 585 tests (607 baseline through Phase 0.6, plus subsequent
additions, minus a few consolidated in Phase 0.7's fixes) plus the two
live-only files gated behind `ECI_LIVE_TESTS`. No target number given —
just the direction ("bare minimum") and the reason (churn cost).
 
---
 
## 5. Suggested order, if picking this up
 
Not requested explicitly, but worth naming so the next session doesn't
have to re-derive it: (3) the mockup dataset is the prerequisite for
meaningfully evaluating (2), and probably makes (1)'s per-persona
stores easier to test too. (4) the test-suite pass is independent of
all three and could happen any time, including in parallel. (1)
swappable personas is the largest architectural piece and the one most
worth a dedicated design pass (persona storage model, Intent
re-hydration, Consolidator's active-persona gating) before any code —
the kind of thing that wants its own ECI-spec-revisions doc the way
v0.35 got one, given how much it touches (Archive's `kind` namespacing,
Intent's persona caching, Consolidator's batching).