"""
Consolidator — MOCK tier (v0.9).

Templated fact extraction, zero LLM cost. Keeps every deterministic part
of the role real — the bus subscription, the Archive/StructuredStore
write path — because that's cheap native code either way and lives in
ConsolidatorBase. What the mock stands in for is the reasoning.

`decided_by="deterministic"`, never "fallback" — same distinction
AnalyticsMock and IntentMock draw. A tier that never had a substrate to
fail is not degraded; reporting it as a fallback would misread as an
outage in the queue log.
"""
from __future__ import annotations

from bus.envelope import Envelope

from agents.consolidator.base import ConsolidationResult, ConsolidatorBase


class ConsolidatorMock(ConsolidatorBase):
    tier = "mock"

    def write(self, envelope: Envelope) -> ConsolidationResult:
        """Templated empty result. No writes: the mock cannot judge what
        an event means, and inventing facts would put fiction into
        long-term memory, which is supposed to be an auditable record of
        what actually happened."""
        return ConsolidationResult(
            writes=[],
            decided_by="deterministic",
            diagnostics={"source_substrate": "mock",
                         "source_model": "none (mock tier, zero LLM cost)"},
        )


__all__ = ["ConsolidatorMock"]
