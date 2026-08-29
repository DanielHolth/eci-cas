"""Shared Reflection core — bus wiring, the rolling incident window, and
applying whatever one reflection pass decided (dispatch #4, 2026-08-29).

Meta-cognition over CONCLUDED episodes, off Governance's `_conclude()`
fork (`events.reflection`) — one event later than Consolidator's BUNDLE
fork, because Reflection needs the finished arc (what was actually said),
not just what was proposed. It never gates anything and never replies to
Governance, same posture as Consolidator.

Batched, not per-event, deliberately: a pattern across N incidents is the
whole point (contract.py's rule 1) — a per-event reflection would just be
Consolidator's job again. `batch_size` incidents accumulate in memory
between passes; a mid-batch crash loses at most one partial batch, which
is recoverable state loss, not data corruption — the same trade
Consolidator's fail-open posture already makes.

Subclasses supply exactly one thing: reflect(pending, prior_learnings) ->
contract.ReflectionResult.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.reflection.contract import ReflectionResult

DEFAULT_BATCH_SIZE = 5


class ReflectionBase:
    """Bus-facing half of Reflection. Subclass and implement reflect()."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, *, structured_store=None,
                 sensory=None, batch_size: int = DEFAULT_BATCH_SIZE):
        self.bus = bus
        self.structured_store = structured_store
        #: Read-only use: fires an Idea ping through the normal front door
        #: (§5.2 — a resurfaced pattern genuinely IS a perception, the same
        #: reasoning docs/ideas/consolidation-doodle.md makes for a click).
        #: None is tolerated (e.g. in isolated tests) — the ping is simply
        #: dropped and counted rather than raising.
        self.sensory = sensory
        self.batch_size = max(1, int(batch_size))

        #: Optional display-layer hook, called with the ReflectionResult
        #: of every completed pass — same pattern as Consolidator's
        #: on_write, for an observer like tools/console.py.
        self.on_reflect = None

        self._pending: List[Dict[str, Any]] = []

        self.metrics: Dict[str, int] = {
            "events": 0, "passes": 0, "llm_calls": 0, "fallbacks": 0,
            "writes": 0, "ideas": 0, "silent": 0, "dropped": 0,
        }
        self.bus.subscribe("events.reflection", self.on_event)

    # ---- Business events ----------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1
        self._pending.append({
            "sensory": envelope.content,
            "final_proposal": envelope.meta.get("final_proposal", ""),
            "reflex_action": envelope.meta.get("reflex_action", ""),
            "verdict": envelope.meta.get("verdict", "green"),
            "severity": envelope.severity,
        })
        if len(self._pending) >= self.batch_size:
            self._run_pass()

    def _run_pass(self) -> None:
        pending, self._pending = self._pending, []
        prior_learnings = (
            self.structured_store.query("knowledge", domain="internal", limit=50)
            if self.structured_store is not None else [])

        result = self.reflect(pending, prior_learnings)
        self.metrics["passes"] += 1
        if result.decided_by == "fallback":
            self.metrics["fallbacks"] += 1

        if result.outcome == "write" and result.write is not None:
            self._apply_write(result.write)
        elif result.outcome == "idea" and result.idea:
            self._apply_idea(result.idea)
        else:
            self.metrics["silent"] += 1

        if result.diagnostics.get("dropped_reason"):
            self.metrics["dropped"] += 1

        if self.on_reflect is not None:
            self.on_reflect(result)

    def _apply_write(self, write: Dict[str, Any]) -> None:
        # Mutates the same dict result.write points at (rather than a copy)
        # so on_reflect — called after this, with the same ReflectionResult —
        # sees domain too, instead of a display layer having to assume it.
        write["domain"] = "internal"
        write["source"] = "reflection"
        if self.structured_store is None:
            return
        self.structured_store.upsert("knowledge", [write])
        self.metrics["writes"] += 1

    def _apply_idea(self, idea: str) -> None:
        self.metrics["ideas"] += 1
        if self.sensory is None:
            return
        self.sensory.ingest(idea, source_type="idea", triggered_by="Reflection")

    # ---- Tier hook ------------------------------------------------------------

    def reflect(self, pending: List[Dict[str, Any]],
                prior_learnings: List[Dict[str, Any]]) -> ReflectionResult:
        raise NotImplementedError


__all__ = ["ReflectionBase", "DEFAULT_BATCH_SIZE"]
