"""
Analytics — MOCK, cognitive tier substrate "deep-reasoning", temp 0.2 (§5.4, §13.1).

Two real jobs per spec: reasoning (produces a recommendation, hands to
Intent for a final read) and working/trend memory (rolling 10-event
window, loop detection). Phase 0 mock keeps the rolling window (cheap,
deterministic bookkeeping) but templates the "reasoning" text itself —
real reasoning arrives when this mock is replaced with a live LLM-backed
agent per §13.4.

Per the §3.2 worked example, Analytics replies directly to Intent
(events.intent), not back to Governance — Governance re-enters the loop
only once Intent has spoken.
"""
from __future__ import annotations

from collections import deque
from typing import Deque

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

ROLLING_WINDOW = 10          # §15 default
LOOP_THRESHOLD = 3           # §15 default: 3 repeats without state change = loop


class AnalyticsMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self._history: Deque[str] = deque(maxlen=ROLLING_WINDOW)
        self.bus.subscribe("events.analytics", self.on_event)
        self.bus.subscribe("system.diagnostic", self.on_diagnostic)

    def on_diagnostic(self, envelope: Envelope) -> None:
        """§11 Level 2: Watchdog's SystemCheck, forwarded here by
        Governance. Reply directly to Recovery — Action is bypassed."""
        if envelope.destination != "Analytics" or envelope.type != "SystemCheck":
            return
        out = envelope.reply(source="Analytics", destination="Recovery",
                              type="SystemCheckAck", content="alive")
        self.bus.publish("system.diagnostic", out)

    def _repeated_action_count(self, content: str) -> int:
        return list(self._history).count(content)

    def on_event(self, envelope: Envelope) -> None:
        if envelope.type == "LoopCheck":
            self._handle_loop_check(envelope)
            return

        content = str(envelope.content)
        self._history.append(content)

        if self._repeated_action_count(content) >= LOOP_THRESHOLD:
            advice = "Loop detected — recommend graceful degradation, not repetition."
        else:
            advice = f"All agents awake. {content}"

        out = envelope.reply(
            source="Analytics",
            destination="Intent",
            type="Recommend",
            content=advice,
            triggered_by=envelope.triggered_by,
        )
        self.bus.publish("events.intent", out)

    def _handle_loop_check(self, envelope: Envelope) -> None:
        """v0.32 — Governance defers here once Action's failure count hits
        the loop threshold (§5.4/§5.7). Deliberately terminal for the
        Phase 0 mock: acknowledges and stops, no further publish anywhere.
        Real graceful-degradation logic (e.g. switch to a text/display
        fallback action) arrives when this mock is replaced with a live
        agent per §13.4 — for now this just proves the handoff exists and
        that a repeated failure doesn't retry forever."""
        pass
