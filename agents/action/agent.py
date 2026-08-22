"""
Action — MOCK, deterministic (§5.7, §13.1).

No persona, no judgment — executes exactly what Governance hands it
after Security clearance.

v0.33 — Action executes and reports only failures back to Governance.
On success: silent (no envelope goes anywhere). On failure: report to
Governance with the original content.

Governance's fallback rule: Action failed? → Issue a Prompt action
instead, letting the persona explain the failure to the human. This
is the only failure path; no retry loops, no loop detection, no
Analytics escalation. Governance has the answer built-in.
"""
from __future__ import annotations

from typing import List

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus


class ActionMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.executed: List[Envelope] = []
        # Testing knob only: forces the next N executions to fail, so the
        # failure/fallback path is actually exercisable without needing a
        # real, flaky world to fail against.
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
            return  # silent on success — no envelope, no re-entry (v0.33)

        # Action failed. Report to Governance, which owns deciding the
        # fallback response (always: issue a Prompt action explaining
        # the failure, §5.7 v0.33).
        out = envelope.reply(
            source="Action",
            destination="Governance",
            type="Failure",
            content=envelope.content,
        )
        self.bus.publish("events.governance", out)
