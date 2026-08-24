"""
Governance's per-event working set (v0.35a/c/g).

Governance gained one job in v0.35 that it never had before: it has to
HOLD something. The Sensory fan-out sends four agents their own copy of
the same event in parallel, and Governance buffers all four answers
before it can bundle them for Intent. Later in the same event it also
needs to remember what Security said and what Intent tried, so it can
hand Consolidator one complete record once Action has run.

Is that a violation of §5.1's "per-event statutory context reset"?
No — and the distinction is worth stating precisely rather than waving
at, because it is the kind of thing that quietly rots. §5.1's rule is
that no routing decision may depend on a PREVIOUS EVENT. What lives here
is scoped to one `event_id`, created when that event's first worker
reports, and destroyed when that event concludes. Governance still holds
no cross-event state, still keeps no memory between events, and still
decides nothing about content. It is a mailbox for one conversation, not
a memory.

Why no timeout
--------------
A partial bundle cannot stall the pipeline on the embedded bus: publish
dispatches synchronously to every subscriber in turn, so by the time
`Sensory.ingest()` returns, all four workers have run to completion. A
slot that never arrives therefore means a worker that isn't subscribed at
all — a misconfiguration, not a race — which is why the expected worker
set is declared up front (see EventState.expected) rather than inferred,
and why an incomplete event is a counted diagnostic rather than
something to wait out. If the bus ever becomes asynchronous, THIS is the
file that needs a timeout, and this paragraph is the note saying so.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Set

from bus.envelope import severity_max
from agents.shared.recommendation import RecommendationEntry

#: The four agents Sensory fans out to (v0.35a). Declared, not inferred:
#: a bundle that waited for "however many turn up" could never tell a
#: missing worker from a slow one.
DEFAULT_WORKERS: Set[str] = {"Impulse", "Analytics", "Personality", "Knowledge"}

#: Which meta key each worker's contribution rides in, on its way into
#: the bundle Intent receives.
WORKER_SLOTS: Dict[str, str] = {
    "Impulse": "impulse",
    "Analytics": "analytics",
    "Personality": "personality",
    "Knowledge": "knowledge",
}

#: The three workers whose answers become Intent's recommendations array
#: (Daniel, 2026-08-24). Impulse is not one of them — its contribution is
#: a felt reaction, not a recommendation, and it keeps riding in its own
#: `meta.reflex` slot exactly as before.
RECOMMENDATION_WORKERS: tuple = ("Analytics", "Personality", "Knowledge")


@dataclass
class EventState:
    """Everything Governance is holding about ONE in-flight event."""

    event_id: str
    expected: Set[str] = field(default_factory=lambda: set(DEFAULT_WORKERS))

    #: Worker name -> the slot payload it reported.
    slots: Dict[str, Dict[str, Any]] = field(default_factory=dict)
    #: The Sensory content, verbatim, as first seen by any worker.
    sensory: str = ""
    #: The highest severity any of the four parallel answers carried.
    #:
    #: This exists because bundling could otherwise LOSE an escalation.
    #: Each worker replies to its own copy of the Sensory event, so the
    #: bundle envelope is built from whichever answer happened to arrive
    #: last — and if that one inherited "Neutral" while Impulse raised
    #: "Elevated" on its own copy, the raised tag would silently vanish.
    #: §3's OR-upscale-only rule says a tag set upstream may be raised by
    #: anyone and lowered by no one, so the bundle carries the maximum.
    severity: str = "Neutral"
    #: Set once the bundle has gone to Intent, so a late or duplicate
    #: report can be dropped rather than bundling the event twice.
    bundled: bool = False
    #: Set when Impulse reads Critical and the reflex path fires (v0.35d).
    #:
    #: This does NOT end the event. The reflex is the FIRST of two actions:
    #: the body moves before the mind catches up, exactly as it does in an
    #: animal. The fan-out still completes, Governance still bundles all
    #: four answers, and Intent still voices — but it voices KNOWING a
    #: reflex already reached the human, so it can account for the double
    #: action rather than talking over its own hand ("Excuse my reflex — I
    #: thought that knife was going to land on someone. Are you okay?").
    #:
    #: That is the whole point of leaving the fan-out unsuppressed on a
    #: Critical: the reflex buys the milliseconds, and the considered
    #: reply arrives behind it with the reflex in its context.
    reflex_fired: bool = False
    #: What the reflex actually did, so Intent can refer to it rather than
    #: guess. Captured at the moment it clears Security.
    reflex_action: str = ""

    #: What Intent proposed, most recent last. On a clean event that's one
    #: entry; on a revised one it's the whole arc (v0.35g).
    proposals: List[str] = field(default_factory=list)
    #: How many REVISION attempts have been spent (not counting the
    #: original proposal). Bounded by contract.MAX_REVISION_PASSES.
    revision_passes: int = 0
    #: The worst verdict this event ever drew, and what Security said
    #: about it.
    verdict: str = ""
    security_concern: str = ""
    blocked: bool = False

    def raise_severity(self, severity: str) -> str:
        """OR-upscale-only (§3): raise, never lower."""
        self.severity = severity_max(self.severity, severity)
        return self.severity

    def ready(self) -> bool:
        return not self.bundled and self.expected.issubset(self.slots)

    def concludes_on_action(self) -> bool:
        """Whether an action reaching Action ends this event.

        Normally yes. On a Critical the reflex reaches Action FIRST, and
        the event is not over — Intent's considered reply is still coming
        behind it. Only the second action (or a block) concludes it."""
        return self.bundled or self.blocked or not self.reflex_fired

    def missing(self) -> Set[str]:
        return self.expected - set(self.slots)

    def bundle(self) -> Dict[str, Any]:
        """The four slots, EXACTLY as each worker reported them — tier,
        decided_by, diagnostics, all of it.

        Kept for internal/diagnostic use (Governance's own bookkeeping,
        the consolidation record) — this is NOT what reaches Intent any
        more. See recommendations() for that (Daniel, 2026-08-24: Intent
        doesn't need to know which tier answered or why a fallback fired,
        it needs to know who said what)."""
        return {WORKER_SLOTS.get(name, name.lower()): payload
                for name, payload in self.slots.items()}

    def recommendations(self) -> List[Dict[str, Any]]:
        """Analytics', Personality's and Knowledge's answers, projected to
        the one shared shape Intent actually reads (Daniel, 2026-08-24):
        {sender, keywords, proceed, concern} — sender identifies who said
        it, everything else about HOW they arrived at it (tier, decided_by,
        diagnostics) stays here in Governance's own bookkeeping.

        A worker that hasn't reported yet, or reported nothing worth
        surfacing (empty keywords — a silent lookup, e.g.), is simply
        omitted rather than included as a noisy empty entry."""
        entries: List[Dict[str, Any]] = []
        for name in RECOMMENDATION_WORKERS:
            slot = self.slots.get(name)
            if not slot:
                continue
            keywords = str(slot.get("recommendation") or slot.get("findings") or "")
            if not keywords:
                continue
            proceed = slot.get("proceed")
            if proceed is None:
                proceed = slot.get("relevant", True)
            concern = str(slot.get("concern") or "")
            entries.append(RecommendationEntry(
                sender=name, keywords=keywords, proceed=bool(proceed),
                concern=concern).to_dict())
        return entries

    def final_proposal(self) -> str:
        return self.proposals[-1] if self.proposals else ""

    def consolidation_record(self) -> Dict[str, Any]:
        """The one bundle per event Consolidator receives once Action has
        run (v0.35g's settled hand-off).

        Deliberately NOT included: Impulse's reflex, Analytics'
        recommendation text, Personality's and Knowledge's findings. Those
        agents only ever surface what Archive already holds or stay
        neutral and never touch it, so repeating them to the agent that
        writes Archive is redundant."""
        security: Dict[str, Any] = {"verdict": self.verdict or "green"}
        if self.security_concern:
            security["concern"] = self.security_concern
        if self.revision_passes:
            security["revisions"] = list(self.proposals[:-1]) or []
        if self.blocked:
            security["blocked"] = True
        record: Dict[str, Any] = {
            "event_id": self.event_id,
            "sensory": self.sensory,
            "security": security,
            "intent_final": self.final_proposal(),
        }
        if self.reflex_fired:
            # The one piece of Impulse's contribution Consolidator DOES
            # need. Normally its reflex reading is excluded as redundant
            # (§v0.35g), but a reflex that actually acted on the world is
            # not a reading — it is something the persona did, and the
            # human lived through it.
            record["reflex_action"] = self.reflex_action
        return record


class BundleBuffer:
    """Governance's mailbox. One entry per in-flight event; entries are
    created on first sight and dropped when the event concludes."""

    def __init__(self, expected: Optional[Set[str]] = None):
        self.expected: Set[str] = set(expected or DEFAULT_WORKERS)
        self._events: Dict[str, EventState] = {}

    def get(self, event_id: str) -> EventState:
        state = self._events.get(event_id)
        if state is None:
            state = EventState(event_id=event_id, expected=set(self.expected))
            self._events[event_id] = state
        return state

    def peek(self, event_id: str) -> Optional[EventState]:
        return self._events.get(event_id)

    def release(self, event_id: str) -> Optional[EventState]:
        """The event is over. Forget it — §5.1's reset, enforced by
        deletion rather than by discipline."""
        return self._events.pop(event_id, None)

    def __len__(self) -> int:
        return len(self._events)

    @property
    def in_flight(self) -> List[str]:
        return list(self._events)


__all__ = ["BundleBuffer", "EventState", "DEFAULT_WORKERS", "WORKER_SLOTS"]
