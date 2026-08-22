"""
Governance — REAL, deterministic tier (§5.1, spec v0.34).

The non-thinking backbone, taken at its word. No persona, no opinions,
never explains itself, and as of v0.34 no substrate: every hop it handles
is settled by the envelope alone, so there is nothing for a model to
decide and nothing for one to write.

That is a tier change, not a capability cut. v0.33 and earlier listed
Governance as Cognitive; Phase 0.1 built the LLM-backed version, measured
what it actually contributed, and found the answer was routing decisions
that were already determined plus wording nobody downstream could use.
The one genuinely open case — a safety verdict that couldn't be read
mechanically — has a better home than a model in the router seat: it goes
to Analytics, which is the agent that reasons. See the v0.34 revision
note.

There is consequently one implementation, not a mock and a real one.
Governance joins Sensory as always-real (§13.1's reasoning applied to a
second role: there is nothing meaningful to mock about a lookup table).
`roles.governance.mock` in the manifest is ignored, with a warning.

Routes (v0.31 strict relay, §3.2 worked example):

    Impulse relay   -> Analytics  Evaluate
    Intent advice   -> Security   Clear
    Security green  -> Action     Speech      release
    Security yellow -> Analytics  Review      rules don't cover it
    Security red    -> Analytics  Revise      blocked
    Action failure  -> Action     Prompt      v0.33 fallback, no retries
    anything else   -> log and drop

Two properties are enforced here rather than trusted to callers:

  Severity is never touched. Outbound envelopes are built with
  Envelope.reply() and no severity argument, so the tag computed upstream
  propagates unchanged (§3's OR-upscale-only rule).

  The control plane is identical to the business path in cost and
  mechanism. BootCheck / SystemCheck are answered by the same native
  code, which is what lets Recovery bootstrap and health-check the
  ecosystem with every model endpoint on earth offline (§9).
"""
from __future__ import annotations

from typing import Any, Dict, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.governance import routing
from agents.governance.routing import RoutingDecision


class Governance:
    """The dispatcher. One tier, no substrate, no state across events."""

    tier = "deterministic"

    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        # Observability counters ONLY. Never read by decide(); Governance's
        # per-event statutory context reset (§5.1) means no decision may
        # depend on anything that happened in a previous event.
        self.metrics: Dict[str, int] = {
            "events": 0, "routed": 0, "dropped": 0, "verdicts_inferred": 0,
        }
        self.bus.subscribe("events.governance", self.on_event)
        self.bus.subscribe("system.diagnostic", self.on_diagnostic)

    # ---- Control plane: Recovery's synthetic pings (§9, §11 Level 2) -----

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

    # ---- Business events --------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1
        decision = routing.decide(envelope)
        if decision is None:
            # Unroutable source (a Recovery ping that leaked onto events.*,
            # or an unexpected sender). Log-and-drop.
            self.metrics["dropped"] += 1
            return
        if decision.diagnostics.get("verdict_inferred"):
            self.metrics["verdicts_inferred"] += 1
        self.emit(envelope, decision)

    # ---- Emission ---------------------------------------------------------

    def emit(self, envelope: Envelope, decision: RoutingDecision) -> Envelope:
        """Publish the decision.

        Reading meta.governance in a trace: it describes the hop it sits
        on ONLY where source == "Governance". Routes that carry meta
        forward (Intent -> Security) hand the whole meta dict to the next
        agent, and Security echoes meta back on its verdict — so a stale
        block can ride along on a hop Governance didn't produce. Filter on
        source."""
        route = decision.route

        meta: Dict[str, Any] = dict(envelope.meta) if route.carry_meta else {}
        governance_meta: Dict[str, Any] = {"tier": self.tier}
        governance_meta.update(decision.diagnostics)
        if decision.rationale:
            governance_meta["rationale"] = decision.rationale
        meta["governance"] = governance_meta

        out = envelope.reply(
            source="Governance",
            destination=route.destination,
            type=route.type,
            content=decision.content,
            triggered_by=envelope.triggered_by,
            meta=meta,
            # NOTE: severity deliberately omitted — reply() inherits the
            # upstream tag and Governance never revises it (§3).
        )
        self.bus.publish(route.topic, out)
        self.metrics["routed"] += 1
        return out


#: Retired alias. Phase 0 had a mock/real split for this role; v0.34
#: collapsed it to one deterministic implementation. Kept so an older
#: import doesn't break silently.
GovernanceMock = Governance
