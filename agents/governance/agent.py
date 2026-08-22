"""
Governance — MOCK, cognitive tier substrate "fast-reflex", temp 0.0 (§5.1, §13.1).

No persona, never explains itself, per-event statutory context reset —
holds no memory ACROSS events. v0.31: no per-event merge state either —
Impulse is now the sole trigger into Governance (see revision notes),
so each hop is handled independently as it arrives; the only remaining
correlation is event_id, carried transparently by Envelope.reply().

Routes: Sensory -> Impulse -> Governance -> Analytics -> Intent ->
Governance -> Security -> Governance -> Action (v0.31 strict relay;
see §3.2 worked example).
On a Security "Red", loops back to Analytics for revision (§4, §5.1).

v0.33 — Action failures are reported to Governance. Governance's response
is deterministic: issue a Prompt action explaining the failure to the
human. No retry loops, no Analytics escalation, no threshold counting.
This is the built-in fallback protocol: when execution fails, explain
why via Prompt (§5.7 v0.33).
"""
from __future__ import annotations

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus


class GovernanceMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
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

        if src == "Impulse":
            self._route_to_analytics(envelope)
        elif src == "Intent":
            self._route_to_security(envelope)
        elif src == "Security":
            self._route_on_security_verdict(envelope)
        elif src == "Action":
            self._handle_action_failure(envelope)
        else:
            # Recovery synthetic pings / unexpected sources: log-only mock behavior.
            pass

    # ---- Step 1: Impulse's relay is the sole trigger (v0.31) --------------

    def _route_to_analytics(self, envelope: Envelope) -> None:
        reflex = envelope.meta.get("reflex", "")
        merged = (f"Evaluate intent based on the prompt ('{envelope.content}') "
                  f"and the reaction ('{reflex}').")
        out = envelope.reply(
            source="Governance",
            destination="Analytics",
            type="Evaluate",
            content=merged,
            triggered_by=envelope.triggered_by,
        )
        self.bus.publish("events.analytics", out)

    # ---- Step 3: Intent's advice comes back to Governance -> Security -----

    def _route_to_security(self, envelope: Envelope) -> None:
        out = envelope.reply(
            source="Governance",
            destination="Security",
            type="Clear",
            content=envelope.content,
            meta=envelope.meta,   # carry Intent's proposed_action through to Security's verdict
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

    # ---- v0.33: Action reports a failure directly to Governance -----------

    def _handle_action_failure(self, envelope: Envelope) -> None:
        """
        Action failed. Governance's fallback protocol: issue a Prompt action
        instead, letting the persona explain the failure to the human.
        
        No retry logic, no Analytics escalation, no threshold counting.
        This is the built-in answer: when execution fails, ask the persona
        to tell the human what happened (§5.7 v0.33).
        """
        out = envelope.reply(
            source="Governance",
            destination="Action",
            type="Prompt",
            content=(f"The previous action failed. Explain to the human what "
                     f"was attempted and why it didn't work. "
                     f"Original request: '{envelope.content}'"),
        )
        self.bus.publish("events.action", out)
