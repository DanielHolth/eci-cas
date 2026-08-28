"""
Shared Analytics core — bus wiring, working memory, loop detection,
emission (§5.4).

Everything here is identical for the mock and the substrate-backed tier,
and deliberately so: they must be interchangeable at the bus boundary, or
`roles.analytics.mock` would be swapping in a different component rather
than a different tier of the same one (§2.1).

Three things live here rather than in either tier, because none of them
should ever involve a model:

  The rolling working window (§5.4, §15's 10-event default). Counting.

  Loop detection (§15's 3-repeat default). Also counting — and when it
  fires, the answer is fixed, so the substrate is never consulted at all.
  This is Phase 0.1's lesson carried forward: an agent shouldn't pay for
  inference to notice it has seen the same thing three times.

  The control plane. SystemCheck is answered by native code so Recovery
  can health-check the ecosystem with every model endpoint offline (§9,
  §11 Level 2).

Subclasses supply exactly one thing: think(), which turns one envelope
and its task into a Recommendation.
"""
from __future__ import annotations

from collections import deque
from typing import Any, Deque, Dict, List

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.analytics import contract
from agents.analytics.contract import Recommendation, Task

ROLLING_WINDOW = 10          # §15 default
LOOP_THRESHOLD = 3           # §15 default: 3 repeats without state change = loop


class AnalyticsBase:
    """Bus-facing half of Analytics. Subclass and implement think()."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, archive=None):
        self.bus = bus
        self.archive = archive
        self._history: Deque[str] = deque(maxlen=ROLLING_WINDOW)
        self.metrics: Dict[str, int] = {
            "events": 0, "recommended": 0, "declined": 0,
            "loops_detected": 0, "llm_calls": 0, "fallbacks": 0,
        }
        self.bus.subscribe("events.analytics", self.on_event)
        self.bus.subscribe("system.diagnostic", self.on_diagnostic)

    # ---- Control plane (§11 Level 2) --------------------------------------

    def on_diagnostic(self, envelope: Envelope) -> None:
        """Watchdog's SystemCheck, forwarded here by Governance. Reply
        directly to Recovery — Action is bypassed. Never calls a model."""
        if envelope.destination != "Analytics" or envelope.type != "SystemCheck":
            return
        out = envelope.reply(source="Analytics", destination="Recovery",
                             type="SystemCheckAck", content="alive")
        self.bus.publish("system.diagnostic", out)

    # ---- Working memory & loop detection ----------------------------------

    def _repeats(self, content: str) -> int:
        return list(self._history).count(content)

    def recent_events(self) -> List[str]:
        """The rolling window, oldest first — bounded context for the
        prompt. Excludes the event currently being handled."""
        return list(self._history)[:-1]

    def prior_knowledge(self, limit: int = 5) -> List[Any]:
        """One bounded Archive read (§5.4's "queries Archive for prior
        context").

        Deliberately ONE query, not the spec's "iterating on the query a
        few times": each round trip is a prompt the flat-cost claim has to
        carry, and Phase 0.2 has no evidence that more than one helps.
        Returns empty until consolidation starts writing knowledge in
        Phase 1 — the wiring is proven now and lights up then."""
        if self.archive is None:
            return []
        try:
            return self.archive.query("knowledge", limit=limit)
        except Exception:
            # Archive is the only door to memory (§5.8), but a reasoning
            # hop should not die because a read failed.
            return []

    # ---- Business events --------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        # v0.35a: this arrives straight from Sensory's fan-out now, in
        # parallel with Impulse, Personality and Knowledge — not relayed
        # by Governance. Nothing about the handling changes; only who
        # published it.
        self.metrics["events"] += 1

        task = Task.from_envelope(envelope)
        if task is None:
            # Not a task Analytics handles (e.g. a legacy LoopCheck, or a
            # type from a future revision). Log and drop rather than guess.
            return

        content = str(envelope.content)
        self._history.append(content)

        repeats = self._repeats(content)
        if repeats >= LOOP_THRESHOLD:
            self.metrics["loops_detected"] += 1
            self.emit(envelope, contract.loop_detected(envelope, repeats))
            return

        recommendation = self.think(envelope, task)
        self.emit(envelope, recommendation)

    def think(self, envelope: Envelope, task: Task) -> Recommendation:
        raise NotImplementedError

    # ---- Emission ---------------------------------------------------------

    def emit(self, envelope: Envelope, recommendation: Recommendation) -> Envelope:
        """Publish Analytics' answer to Governance for bundling."""
        if recommendation.decided_by == "fallback":
            self.metrics["fallbacks"] += 1

        meta: Dict[str, Any] = dict(envelope.meta)
        meta.pop("governance", None)      # not ours to forward
        meta["analytics"] = {"tier": self.tier,
                             "recommendation": recommendation.recommendation,
                             **recommendation.to_meta()}

        out = envelope.reply(
            source="Analytics",
            destination="Governance",
            type="Recommend",
            content=recommendation.recommendation,
            triggered_by=envelope.triggered_by,
            meta=meta,
            # Severity deliberately omitted — inherited untouched (§3).
            # Whether Analytics should be able to RAISE severity is a real
            # question the spec permits but no phase has exercised.
        )
        self.bus.publish("events.governance", out)
        self.metrics["recommended"] += 1
        return out
