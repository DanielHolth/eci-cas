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
  The persona CACHE (v0.35g). Core Anchors + Evolving Trait Delta are
  hydrated once at construction and held in memory. Phase 0.4 called
  hydrate() — and therefore archive.query("identity") — on every single
  voicing call; that per-event Archive read is gone. Anchors don't change
  between consolidation cycles, and Personality (v0.35b) now supplies the
  per-event, situationally-relevant identity context Intent used to fetch
  for itself. The cache is refreshed on exactly one signal: Consolidator's
  `EpochWritten` ping on system.control, published right after it writes
  a new epoch. That ping is the ONLY coupling between the two agents —
  no shared mutable state, no direct references; the one shared durable
  thing is Archive, written only by Consolidator.

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

#: How many prior epochs feed the Evolving Trait Delta digest. Small and
#: bounded (§1's flat-cost claim) — though as of v0.35g this is read once
#: per consolidation cycle rather than once per event.
RECENT_EPOCHS_FOR_HYDRATION = 3

#: Default conversation window, in whole concluded events. Overridden per
#: budget tier (budget/tiers.py) — see the module docstring.
DEFAULT_CONTEXT_EVENTS = 10

#: Hard ceiling on the temp log regardless of the configured window, so a
#: long-running process can't grow it without bound between consolidation
#: cycles. Generous relative to any tier's window.
TEMP_LOG_MAXLEN = 200


def is_epoch_record(record: Dict[str, Any]) -> bool:
    """True for a consolidation epoch, false for the anchors record and
    for Consolidator's identity NOTES (v0.35g's knowledge-style writes
    into the identity store).

    Phase 0.4 epochs carry no `kind` field at all, so absence means
    epoch — this stays readable against archives written before v0.35."""
    kind = record.get("kind")
    return kind is None or kind == "epoch"


class IntentBase:
    """Bus-facing half of Intent. Subclass and implement voice()."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, archive, *,
                 context_events: int = DEFAULT_CONTEXT_EVENTS,
                 consolidator=None):
        self.bus = bus
        self.archive = archive
        self.context_events = max(0, int(context_events))
        #: Deliberately unused, and kept as a named no-op rather than
        #: removed silently.
        #:
        #: v0.35f's first cut had Intent hand each concluded event to
        #: Consolidator directly, in-process, because the fan-out didn't
        #: exist yet. Under the full v0.35a/c/g topology that is
        #: GOVERNANCE's job — one bundle per event, sent once Action
        #: completes, carrying the Security outcome an Intent-side
        #: hand-off could never know. Intent hands over nothing, and the
        #: two agents share no reference at all (v0.35f: "no shared
        #: mutable state").
        self.consolidator = consolidator

        #: The conversation window's backing store (§7.2's ephemeral
        #: provisional ledger). In memory, not Archive: the spec is
        #: explicit that a mid-consolidation crash here is recoverable
        #: state loss, not data corruption.
        self._temp_log: Deque[Dict[str, Any]] = deque(maxlen=TEMP_LOG_MAXLEN)

        self.metrics: Dict[str, int] = {
            "events": 0, "advised": 0,
            "llm_calls": 0, "fallbacks": 0, "rehydrations": 0,
        }

        self.ensure_anchors_seeded()
        #: The persona cache (v0.35g). Hydrated once here; refreshed only
        #: on Consolidator's EpochWritten ping.
        self._persona: PersonaState = self.hydrate()

        self.bus.subscribe("events.intent", self.on_event)
        self.bus.subscribe("system.control", self.on_control)

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
        """Core Anchors (fixed) + a recency-weighted digest of the most
        recent consolidation deltas (§7.1).

        Called ONCE at construction and again only on EpochWritten — never
        per event (v0.35g). See the module docstring."""
        anchors = dict(contract.DEFAULT_CORE_ANCHORS)
        evolving_delta = ""
        epoch_count = 0

        if self.archive is not None:
            records = self.archive.query("identity")
            anchor_records = [r for r in records if r.get("kind") == "anchors"]
            if anchor_records:
                anchors = anchor_records[-1].get("anchors", anchors)

            epochs = [r for r in records if is_epoch_record(r)]
            epoch_count = len(epochs)
            fragments: List[str] = []
            for epoch in epochs[-RECENT_EPOCHS_FOR_HYDRATION:]:
                for delta in epoch.get("deltas", []):
                    rationale = delta.get("rationale")
                    if rationale:
                        fragments.append(str(rationale))
            evolving_delta = " ".join(fragments)[:800]

        return PersonaState(anchors=anchors, evolving_delta=evolving_delta,
                            epoch_count=epoch_count)

    @property
    def persona(self) -> PersonaState:
        """The cached persona. Every live call reads this; none of them
        touch Archive (v0.35g)."""
        return self._persona

    def on_control(self, envelope: Envelope) -> None:
        """Control plane. Consolidator says it wrote an epoch; the cached
        persona is now one cycle stale, so re-read it — once, here, rather
        than on every voicing call."""
        if envelope.type != "EpochWritten" or envelope.destination != "Intent":
            return
        self._persona = self.hydrate()
        self.metrics["rehydrations"] += 1

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

__all__ = ["IntentBase", "DEFAULT_CONTEXT_EVENTS", "RECENT_EPOCHS_FOR_HYDRATION",
           "TEMP_LOG_MAXLEN", "is_epoch_record"]
