"""
Shared Intent core — bus wiring, the persona cache, the conversation
window (§5.5, v0.35f/g).

Mirrors agents/analytics/base.py's split deliberately: everything here is
identical for the mock and the substrate-backed tier, because they must
stay interchangeable at the bus boundary (§2.1) the same way Analytics'
two tiers do. Subclasses supply exactly one thing:

  voice(envelope, task, ...) -> contract.Speech
      Turn one hop into `proposed_action` text, in whichever register the
      task calls for (agents/intent/contract.py's Task).

What v0.35f removed from this file, and why it isn't hiding elsewhere
---------------------------------------------------------------------
Phase 0.4's IntentBase also owned consolidation: a batch counter, the
temp-log-to-epoch pass, epoch writing, and the Impulse recalibration
coupling. All of that is now `agents/consolidator/` (v0.35f) — a separate
agent, on its own substrate, with its own batch buffer, running its slow
pass off the live path. Nothing about WHO decides what to write changed
(Option B: the reasoner decides content, Archive stays a dumb executor);
the reasoning simply stopped being a mode of this class.

The node/rotation model went with it. `Awake -> Consolidating ->
ReadyToSwap`, `node_id`, and the N=1 "always Awake" special case are
gone: Intent is always active, one job, like every other role. §7's
lifecycle chapter is superseded, so this file no longer carries its
vocabulary.

What lives here rather than in either tier
-------------------------------------------
  The persona CACHE. Core Anchors are hydrated once at construction and
  held in memory. Phase 0.4 called hydrate() — and therefore
  archive.query("identity") — on every single voicing call; that
  per-event Archive read is gone. Anchors don't change at runtime, and
  Personality (v0.35b) now supplies the per-event, situationally-relevant
  identity context Intent used to fetch for itself.

  (Phase 0.4 through 0.8 also carried an "Evolving Trait Delta" here —
  a digest of Consolidator's batch epochs, re-hydrated on an
  `EpochWritten` control-plane ping. Phase 0.9 turned Consolidator into a
  per-event fact writer with no epochs at all, which left the digest
  permanently empty and the ping permanently unfired — an unintended
  side effect, not a design decision. Removed in Phase 0.9.1 rather than
  left as dead code.)

  The conversation window. The temp log is the only cross-event state in
  the system, and under v0.35c it is also what gives Intent the broader
  context the single-event agents (Analytics, Personality, Knowledge)
  never have. It is bounded by whole EVENTS, not characters or turns —
  one entry is one concluded event — so a window can never be cut
  mid-event. How many events is tier-scaled (`context_events`, set by
  budget/tiers.py: minimal 1, budget 5, default 10, super 15), because
  this rides on every live call and is therefore charged against the
  flat-cost claim (§1).

  Core Anchors seeding. The persona lives in Archive as data, not code or
  manifest YAML — see agents/intent/contract.py's DEFAULT_CORE_ANCHORS —
  so both tiers need the same "seed it once if it isn't there yet" step.
"""
from __future__ import annotations

from collections import deque
from typing import Any, Deque, Dict, List, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.intent import contract
from agents.intent.contract import PersonaState, Speech, Task

#: Default conversation window, in whole concluded events. Overridden per
#: budget tier (budget/tiers.py) — see the module docstring.
DEFAULT_CONTEXT_EVENTS = 10

#: Hard ceiling on the temp log regardless of the configured window, so a
#: long-running process can't grow it without bound between consolidation
#: cycles. Generous relative to any tier's window.
TEMP_LOG_MAXLEN = 200


class IntentBase:
    """Bus-facing half of Intent. Subclass and implement voice()."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, archive, *,
                 context_events: int = DEFAULT_CONTEXT_EVENTS):
        self.bus = bus
        self.archive = archive
        self.context_events = max(0, int(context_events))

        #: The conversation window's backing store (§7.2's ephemeral
        #: provisional ledger). In memory, not Archive: the spec is
        #: explicit that a mid-consolidation crash here is recoverable
        #: state loss, not data corruption.
        self._temp_log: Deque[Dict[str, Any]] = deque(maxlen=TEMP_LOG_MAXLEN)

        self.metrics: Dict[str, int] = {
            "events": 0, "advised": 0,
            "llm_calls": 0, "fallbacks": 0,
        }

        self.ensure_anchors_seeded()
        #: The persona cache. Hydrated once here; Core Anchors don't
        #: change at runtime, so there is nothing to re-hydrate on.
        self._persona: PersonaState = self.hydrate()

        self.bus.subscribe("events.intent", self.on_event)

    # ---- Persona (§5.5, §7.1) ----------------------------------------------

    def ensure_anchors_seeded(self) -> None:
        """Write the starter Core Anchors to Archive if none exist yet.
        Idempotent, and safe to call from every tier — only the first
        caller across the ecosystem's lifetime actually writes."""
        if self.archive is None:
            return
        existing = self.archive.query(
            "identity",
            predicate=lambda r: r.get("epoch_id") == contract.ANCHORS_EPOCH_ID,
            limit=1,
        )
        if existing:
            return
        self.archive.write("identity", {
            "epoch_id": contract.ANCHORS_EPOCH_ID,
            "kind": "anchors",
            "anchors": contract.DEFAULT_CORE_ANCHORS,
        })

    def hydrate(self) -> PersonaState:
        """Core Anchors, read from Archive. Called once at construction —
        nothing invalidates the cache at runtime, since anchors are
        static once seeded."""
        anchors = dict(contract.DEFAULT_CORE_ANCHORS)

        if self.archive is not None:
            records = self.archive.query("identity")
            anchor_records = [r for r in records if r.get("kind") == "anchors"]
            if anchor_records:
                anchors = anchor_records[-1].get("anchors", anchors)

        return PersonaState(anchors=anchors)

    @property
    def persona(self) -> PersonaState:
        """The cached persona. Every live call reads this; none of them
        touch Archive."""
        return self._persona

    # ---- Conversation window (v0.35c) --------------------------------------

    def recent_conversation(self) -> List[Dict[str, Any]]:
        """The last N CONCLUDED EVENTS, oldest first — the broader context
        Analytics/Personality/Knowledge never get (v0.35c).

        Bounded by whole events by construction: one temp-log entry is one
        event, so no window can ever end mid-event. N is tier-scaled; 0
        means the window is disabled entirely (Minimal keeps 1)."""
        if not self.context_events:
            return []
        return list(self._temp_log)[-self.context_events:]

    # ---- Business events ----------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1

        task = Task.from_envelope(envelope)
        if task is None:
            # Not a task Intent handles. Log and drop rather than guess.
            return

        speech = self.voice(envelope, task)
        self.emit(envelope, task, speech)

    def voice(self, envelope: Envelope, task: Task) -> Speech:
        """Return a contract.Speech — text plus attribution. Subclasses
        implement this for their tier."""
        raise NotImplementedError

    # ---- Emission -----------------------------------------------------------

    def emit(self, envelope: Envelope, task: Task, speech: Speech) -> Envelope:
        """Intent always replies to Governance, which owns every hop from
        here. Intent always proceeds — Security/Governance decide blocking."""
        self.metrics["advised"] += 1
        if speech.decided_by in ("fallback", "budget"):
            self.metrics["fallbacks"] += 1

        self._temp_log.append({
            "event_id": envelope.event_id,
            "task": task.value,
            "heard": str(envelope.content)[:400],
            "said": speech.text,
        })

        intent_meta: Dict[str, Any] = {"tier": self.tier,
                                       "decided_by": speech.decided_by,
                                       "task": task.value}
        intent_meta.update(speech.diagnostics)

        meta: Dict[str, Any] = dict(envelope.meta)
        meta.pop("governance", None)
        meta["proposed_action"] = speech.text
        meta["proceed"] = True
        meta["intent"] = intent_meta

        out = envelope.reply(
            source="Intent",
            destination="Governance",
            type="Advise",
            content=speech.text,
            triggered_by=envelope.triggered_by,
            meta=meta,
        )
        self.bus.publish("events.governance", out)
        return out

__all__ = ["IntentBase", "DEFAULT_CONTEXT_EVENTS", "TEMP_LOG_MAXLEN"]
