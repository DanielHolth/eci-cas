"""
Intent — MOCK, cognitive tier, temp 0.7, N-node fleet (§5.5, §7, §13.1).

Phase 0 runs exactly one registered node ("node-a") in the Awake state at
all times — the N-node registry exists from day one (§13.1), but real
rotation (Awake -> Consolidating -> ReadyToSwap, §7.1) is not exercised
until this mock is replaced with a live agent, where N=1 degrades to a
*pause* rather than a swap (§7.3).

This mock still performs the deterministic, cheap parts of consolidation
bookkeeping (batch counting, writing a templated epoch delta to Archive
on schedule) so the replacement only has to swap in real reasoning, not
build the scaffold.

v0.34/Phase 0.2 — Intent now voices REFUSALS.
---------------------------------------------
When Analytics declines something (the yellow lane's `proceed: false`, a
blocked course with no acceptable alternative, or a detected loop), the
refusal reaches the human in the PERSONA'S voice rather than as a
router's template. That is the whole reason the refusal path runs
Analytics -> Intent rather than Analytics -> Governance: §5.5 gives Intent
the escalation-tier judgment and the voice to deliver it, and a persona
that goes quiet when it is uneasy is worse than one that says so.

Intent stays advisory (§5.5). It does not get to overturn the decline —
Governance and Security hold the veto power, and a refusal that Intent
could talk itself out of would not be a safety property. What Intent
supplies is how it sounds.
"""
from __future__ import annotations

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from agents.archive.store import ArchiveStore

DEFAULT_BATCH_SIZE = 25      # §15 default: rotation.batch_size_events


class IntentMock:
    def __init__(self, bus: EmbeddedBus, archive: ArchiveStore,
                 node_id: str = "node-a", batch_size: int = DEFAULT_BATCH_SIZE):
        self.bus = bus
        self.archive = archive
        self.node_id = node_id
        self.state = "Awake"          # N=1: always Awake in Phase 0 (§7.3)
        self.batch_size = batch_size
        self._events_since_consolidation = 0
        self._cycle = 0

        self.bus.subscribe("events.intent", self.on_event)

    def _advise(self, recommendation: str) -> str:
        """Templated advisory judgment — real values-reasoning arrives when
        this mock is replaced with a live agent per §13.4.

        Phase 0.2 note: the mock now ECHOES Analytics' recommendation into
        its advice, so a queue trace shows the reasoning actually being
        consumed rather than vanishing. Before this, a real Analytics
        could produce an excellent read and the trace would look
        identical to one where it produced nothing — which makes the
        whole pipeline impossible to eyeball.

        What it deliberately does NOT do is put Analytics' words into the
        persona's mouth. The proposed action stays a templated line (see
        on_event), because Analytics writes ANALYSIS and Intent writes
        SPEECH, and a mock that parroted the one as the other would
        quietly break the guarantee that Analytics never addresses the
        human. Turning analysis into voice is exactly the job Phase 0.4
        exists to do."""
        if "unethical" in recommendation.lower():
            return "That would be unethical. Recommend declining."
        return (f"Noted Analytics' read: {recommendation} "
                f"Recommend a warm response.")

    def _refusal(self, concern: str) -> str:
        """Phrase a decline in the persona's own voice (v0.34).

        The concern comes from Analytics and is passed through rather than
        reworded — Intent supplies the voice, not a different reason."""
        if concern:
            return f"I'd rather not do that one. {concern}"
        return "I'd rather not do that one."

    def on_event(self, envelope: Envelope) -> None:
        proceed = envelope.meta.get("proceed", True)
        concern = str(envelope.meta.get("concern", "")).strip()

        if not proceed:
            advice = f"Analytics advised against this: {envelope.content} Declining, and saying so."
            proposed_action = self._refusal(concern)
        else:
            advice = self._advise(str(envelope.content))
            # Templated persona voice. Phase 0.4 replaces this with a node
            # that actually reads the recommendation and speaks from it.
            proposed_action = ("Hey there! Awake and pleased to interact."
                               if "warm response" in advice else advice)

        out = envelope.reply(
            source="Intent",
            destination="Governance",
            type="Advise",
            content=advice,
            triggered_by=envelope.triggered_by,
            meta={"proposed_action": proposed_action,
                  "node_id": self.node_id,
                  "declined": not proceed},
        )
        self.bus.publish("events.governance", out)

        self._events_since_consolidation += 1
        if self._events_since_consolidation >= self.batch_size:
            self._consolidate()

    def _consolidate(self) -> None:
        """§7.4 — templated epoch delta. Real reconciliation (temp log +
        Analytics' delta report + prior Evolving Trait Delta) arrives with
        a live Intent node; the mock just proves the write path works."""
        self._cycle += 1
        epoch = {
            "epoch_id": f"phase0-mock_{self.node_id}_cycle-{self._cycle}",
            "source_substrate": "mock",
            "source_model": "none (Phase 0 mock, zero LLM cost)",
            "node_id": self.node_id,
            "consolidation_cycle": self._cycle,
            "deltas": [],
        }
        self.archive.write("identity", epoch)
        self._events_since_consolidation = 0
