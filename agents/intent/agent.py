"""
Intent — MOCK, cognitive tier, temp 0.7, N-node fleet (§5.5, §7, §13.1).

Phase 0 runs exactly one registered node ("node-a") in the Awake state
at all times — the N-node registry exists from day one (per §13.1: "the
N-node registry exists from day one, it just has one entry"), but real
rotation behavior (Awake -> Consolidating -> ReadyToSwap, §7.1) is not
exercised until this mock is replaced with a live agent in Phase 1,
where N=1 degrades to a *pause* rather than a swap (§7.3).

This mock still performs the deterministic, cheap parts of consolidation
bookkeeping (batch counting, writing a templated epoch delta to Archive
on schedule) so the Phase 1 replacement only has to swap in real
reasoning, not build the counting/writing scaffold from scratch.
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

    def _advise(self, content: str) -> str:
        """Templated advisory judgment — real values-reasoning arrives when
        this mock is replaced with a live agent per §13.4."""
        if "unethical" in content.lower():
            return "That would be unethical. Recommend declining."
        return "Awake and pleased to interact. Let's give a warm response."

    def on_event(self, envelope: Envelope) -> None:
        advice = self._advise(str(envelope.content))

        out = envelope.reply(
            source="Intent",
            destination="Governance",
            type="Advise",
            content=advice,
            triggered_by=envelope.triggered_by,
            meta={"proposed_action": f"Hey there! {advice}" if "warm" in advice else advice,
                  "node_id": self.node_id},
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
