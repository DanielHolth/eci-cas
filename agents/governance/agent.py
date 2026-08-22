"""
Governance — MOCK, cognitive tier substrate "fast-reflex", temp 0.0 (§5.1, §13.1).

No persona, never explains itself, per-event statutory context reset —
holds no memory ACROSS events. Within a single event's lifecycle it does
need short-lived correlation state (the Sensory/Impulse merge buffer,
and knowing where a given event_id currently sits in the pipeline);
that state is discarded the moment the event completes, so it does not
violate the "no memory across events" rule.

Routes: Sensory + Impulse -> Analytics -> Intent -> Governance -> Security
-> Governance -> Action, matching the §3.2 worked example exactly.
On a Security "Red", loops back to Analytics for revision (§4, §5.1).
"""
from __future__ import annotations

from typing import Dict

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus


class GovernanceMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self._merge_buffer: Dict[str, Dict[str, Envelope]] = {}

        self.bus.subscribe("events.governance", self.on_event)
        self.bus.subscribe("system.diagnostic", self.on_diagnostic)

    # ---- Recovery's synthetic diagnostic pings (§9, §11 Level 2) ----------
    # These bypass Action entirely and are structurally distinct from real
    # events (§9) — they live on system.diagnostic, never events.*.

    def on_diagnostic(self, envelope: Envelope) -> None:
        if envelope.destination != "Governance":
            return  # e.g. Analytics' reply back to Recovery — not ours

        if envelope.type == "BootCheck":
            # §9.1 step 6: verify full pass-through to Governance and back.
            out = envelope.reply(source="Governance", destination="Recovery",
                                  type="BootCheckAck", content="alive")
            self.bus.publish("system.diagnostic", out)
        elif envelope.type == "SystemCheck":
            # §11 Level 2: routed to Analytics, which replies directly to
            # Recovery — Action is bypassed.
            out = envelope.reply(source="Governance", destination="Analytics",
                                  type="SystemCheck", content="liveness check")
            self.bus.publish("system.diagnostic", out)

    def on_event(self, envelope: Envelope) -> None:
        src = envelope.source

        if src in ("Sensory", "Impulse"):
            self._merge_and_route_to_analytics(envelope)
        elif src == "Intent":
            self._route_to_security(envelope)
        elif src == "Security":
            self._route_on_security_verdict(envelope)
        else:
            # Recovery synthetic pings / unexpected sources: log-only mock behavior.
            pass

    # ---- Step 1: wait for + merge Sensory & Impulse (§3.2) ----------------

    def _merge_and_route_to_analytics(self, envelope: Envelope) -> None:
        bucket = self._merge_buffer.setdefault(envelope.event_id, {})
        bucket[envelope.source] = envelope

        if "Sensory" not in bucket or "Impulse" not in bucket:
            return  # still waiting on the other parallel input

        sensory_env = bucket["Sensory"]
        impulse_env = bucket["Impulse"]
        del self._merge_buffer[envelope.event_id]  # per-event state, discard now

        merged = f"Evaluate intent based on the prompt ('{sensory_env.content}') and the reaction ('{impulse_env.content}')."
        out = sensory_env.reply(
            source="Governance",
            destination="Analytics",
            type="Evaluate",
            content=merged,
            triggered_by=sensory_env.triggered_by,
        )
        self.bus.publish("events.analytics", out)

    # ---- Step 3: Intent's advice comes back to Governance -> Security -----

    def _route_to_security(self, envelope: Envelope) -> None:
        out = envelope.reply(
            source="Governance",
            destination="Security",
            type="Clear",
            content=envelope.content,
        )
        self.bus.publish("events.security", out)

    # ---- Step 5: Security's verdict comes back to Governance --------------

    def _route_on_security_verdict(self, envelope: Envelope) -> None:
        verdict = str(envelope.content)
        if verdict.strip().lower().startswith("red"):
            # Hard "No" -> loop back to Analytics for a revised course (§4, §5.1)
            out = envelope.reply(
                source="Governance",
                destination="Analytics",
                type="Revise",
                content="Security blocked the prior course. Propose a revised response.",
            )
            self.bus.publish("events.analytics", out)
            return

        out = envelope.reply(
            source="Governance",
            destination="Action",
            type="Speech",
            content=envelope.meta.get("proposed_action", envelope.content),
        )
        self.bus.publish("events.action", out)
