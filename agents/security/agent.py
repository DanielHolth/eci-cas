"""
Security — MOCK, deterministic, rule-based (§5.6, §13.1).

"Security's mock always answers Green" (§13.1) — real graded response
(~90% silent / ~9% advisory / ~1% hard "No", evaluated against
security_rules.json) arrives when this mock is replaced with the real
rule engine in §13.4's replacement sequence.
"""
from __future__ import annotations

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus


class SecurityMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.bus.subscribe("events.security", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        out = envelope.reply(
            source="Security",
            destination="Governance",
            type="Verdict",
            content="Green",
            meta=envelope.meta,
        )
        self.bus.publish("events.governance", out)
