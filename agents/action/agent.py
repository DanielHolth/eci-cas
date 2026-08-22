"""
Action — MOCK, deterministic (§5.7, §13.1).

No persona, no judgment — executes exactly what Governance hands it
after Security clearance.

v0.32 — no proprioception loop through Sensory (revision notes at the
top of the spec). On success, Action is silent: no envelope goes
anywhere. It's the only door to the outside world; once it does the
deed, there's nothing further to say in-band — the real-world effect
IS the confirmation, the way a person doesn't narrate "I have finished
speaking" to themselves after speaking.

On failure, Action reports to GOVERNANCE (never Sensory) — Governance
commanded the action, so Governance owns deciding what happens next.
Action includes its own failure count and whether the loop threshold
(§15 default: 3) has been reached, so Governance can retry directly for
early failures but must hand off to Analytics once the threshold hits —
preserving Analytics' ownership of loop detection per §5.4/§5.7, rather
than Governance silently retrying forever.
"""
from __future__ import annotations

from typing import List

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

FAILURE_LOOP_THRESHOLD = 3     # §5.7 / §15: three failures trigger a loop check via Analytics


class ActionMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.executed: List[Envelope] = []
        self._consecutive_failures = 0
        # Testing knob only: forces the next N executions to fail, so the
        # failure/retry/graceful-degradation path is actually exercisable
        # without needing a real, flaky world to fail against.
        self.force_next_failures = 0
        self.bus.subscribe("events.action", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        self.executed.append(envelope)

        if self.force_next_failures > 0:
            self.force_next_failures -= 1
            success = False
        else:
            success = True

        if success:
            self._consecutive_failures = 0
            return  # silent on success — no envelope, no re-entry (v0.32)

        self._consecutive_failures += 1
        out = envelope.reply(
            source="Action",
            destination="Governance",
            type="Failure",
            content=envelope.content,
            meta={
                "consecutive_failures": self._consecutive_failures,
                "loop_threshold_reached": self._consecutive_failures >= FAILURE_LOOP_THRESHOLD,
            },
        )
        self.bus.publish("events.governance", out)
