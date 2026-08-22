"""
Action — MOCK, deterministic (§5.7, §13.1).

No persona, no judgment — executes exactly what Governance hands it
after Security clearance. Phase 0 mock "executes" by printing/recording
the output. Reports outcomes back exclusively via Sensory — the
proprioception model (§4): Action's outcome re-enters the system as a
new Sensory input, never a separate feedback channel.
"""
from __future__ import annotations

from typing import List

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

FAILURE_LOOP_THRESHOLD = 3     # §5.7: three failures trigger a loop check


class ActionMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.executed: List[Envelope] = []
        self._consecutive_failures = 0
        self.bus.subscribe("events.action", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        self.executed.append(envelope)
        success = True  # Phase 0 mock: speech "execution" always succeeds

        if success:
            self._consecutive_failures = 0
            outcome_content = f"[executed] {envelope.content}"
        else:
            self._consecutive_failures += 1
            outcome_content = f"[failed] {envelope.content}"
            if self._consecutive_failures >= FAILURE_LOOP_THRESHOLD:
                outcome_content += " — three failures, fall back to display/text action"

        outcome = envelope.reply(
            source="Action",
            destination="Sensory",
            type="Outcome",
            content=outcome_content,
            triggered_by="self",
        )
        self.bus.publish("events.sensory", outcome)
