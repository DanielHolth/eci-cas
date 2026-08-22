"""
Security — MOCK, deterministic, rule-based (§5.6, §13.1).

"Security's mock always answers Green" (§13.1) — real graded response
(~90% silent / ~9% advisory / ~1% hard "No", evaluated against
security_rules.json) arrives when this mock is replaced with the real
rule engine in §13.4's replacement sequence.

v0.34 — the verdict is now stated as DATA in `meta.verdict`, drawn from
the closed enum in bus/envelope.py (green | yellow | red), not inferred
from the prose in `content`. The prose stays for human readability and
back-compatibility; nothing downstream parses it any more.

This matters for the role boundary, not just the wire format. Security is
"is this against the rules" — deterministic, auditable, every decision
justifiable from security_rules.json and that single event (§5.6). It is
NOT "is this against our values"; that is Intent's, per §5.5, and the
persona is deliberately given room to push at that line. Security exists
to be the hard stop, which only works if it stays mechanical: a model in
this seat would trade the audit trail for judgment the ecosystem already
has somewhere better.

So the real Security keeps no LLM. What it gains instead is the yellow
lane: where the rules do not cover a case, it says so, and the agent that
reasons picks it up.
"""
from __future__ import annotations

from bus.envelope import VERDICT_GREEN, Envelope
from bus.pubsub import EmbeddedBus


class SecurityMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.bus.subscribe("events.security", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        meta = dict(envelope.meta)
        meta["verdict"] = VERDICT_GREEN     # §13.1: the mock always clears

        out = envelope.reply(
            source="Security",
            destination="Governance",
            type="Verdict",
            content="Green",
            meta=meta,
        )
        self.bus.publish("events.governance", out)
