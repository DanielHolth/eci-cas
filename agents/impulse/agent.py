"""
Impulse — MOCK, deterministic tier (§5.3, §13.1).

Phase 0 mock: real drive-vector bookkeeping (it's cheap, deterministic
code — no LLM call either way), but the reflexive "reaction" text is
templated rather than reasoned. Subscribes to events.impulse (its
inbound), reacts in parallel with Sensory, and publishes its reaction to
events.governance so Governance can merge the two (§3.2 worked example).
"""
from __future__ import annotations

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from agents.archive.store import ArchiveStore

DEFAULT_VECTORS = {
    "curiosity": 0.8,
    "fatigue": 0.1,
    "urgency": 0.0,
    "social_drive": 0.5,
    "temperature": 0.4,      # §15 default seed vectors
}


class ImpulseMock:
    def __init__(self, bus: EmbeddedBus, archive: ArchiveStore):
        self.bus = bus
        self.archive = archive
        self.vectors = dict(DEFAULT_VECTORS)
        self.archive.set_drive_vectors(self.vectors)
        self.bus.subscribe("events.impulse", self.on_event)

    def _reflex(self, content: str) -> str:
        """Templated reflexive reaction — real reasoning arrives when this
        mock is replaced with a live agent per §13.4."""
        if self.vectors["urgency"] > 0.6:
            return "Terse, protective reaction."
        if self.vectors["curiosity"] > 0.6:
            return "Calm, exploratory reaction."
        return "Calm reaction."

    def apply_feedback(self, valence: float, driver: str) -> None:
        """Reward path, §4.1: Impulse shifts drive vectors immediately,
        no Intent pre-approval."""
        if driver in self.vectors:
            self.vectors[driver] = max(0.0, min(1.0, self.vectors[driver] + valence))
            self.archive.set_drive_vectors(self.vectors)

    def on_event(self, envelope: Envelope) -> None:
        reaction = self._reflex(str(envelope.content))
        out = envelope.reply(
            source="Impulse",
            destination="Governance",
            type="Reflex",
            content=reaction,
            triggered_by=envelope.triggered_by,
            meta={"drive_vectors": dict(self.vectors)},
        )
        self.bus.publish("events.governance", out)
