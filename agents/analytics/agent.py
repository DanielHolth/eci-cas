"""
Analytics — MOCK tier (§5.4, §13.1).

Templated reasoning, zero LLM cost. Keeps the deterministic half of the
role for real — rolling working window, loop detection, Archive access —
because all of that is cheap native code either way and lives in
AnalyticsBase.

What the mock stands in for is the thinking. It answers every task with
the Phase 0 template, which is also exactly what the live tier degrades
to on an Evaluate (see contract.templated_recommendation), so a substrate
outage changes the quality of the answer rather than the shape of the
trace.

2026-08-25: Analytics gates nothing. `proceed`/`concern` are gone from
its contract entirely — the only real gate left in the system is
Security's red verdict.
"""
from __future__ import annotations

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.analytics import contract
from agents.analytics.base import AnalyticsBase
from agents.analytics.contract import Recommendation, Task


class AnalyticsMock(AnalyticsBase):
    tier = "mock"

    def __init__(self, bus: EmbeddedBus, archive=None):
        super().__init__(bus, archive)

    def think(self, envelope: Envelope, task: Task) -> Recommendation:
        return Recommendation(
            recommendation=contract.templated_recommendation(envelope),
            decided_by="deterministic",
        )
