"""
Consolidator — MOCK tier (v0.35f/g, §13.1).

Templated reconciliation, zero LLM cost. Keeps every deterministic part
of the role real — batching, the threshold trigger, epoch assembly, the
Archive write path, the recalibration coupling, the EpochWritten ping —
because all of that is cheap native code either way and lives in
ConsolidatorBase. What the mock stands in for is the reasoning.

Direct port of Phase 0.4's `IntentMock.reconcile()`, which had exactly
this job before v0.35f carved Consolidator out of Intent: prove the write
path works, at zero cost, and leave the actual reconciliation to the live
tier.

`decided_by="deterministic"`, never "fallback" — same distinction
AnalyticsMock and IntentMock draw. A tier that never had a substrate to
fail is not degraded; reporting it as a fallback would misread as an
outage in the queue log.
"""
from __future__ import annotations

from typing import Any, Dict, List

from agents.consolidator.base import ConsolidationResult, ConsolidatorBase


class ConsolidatorMock(ConsolidatorBase):
    tier = "mock"

    def reconcile(self, batch: List[Dict[str, Any]],
                  recent_queue: List[Dict[str, Any]],
                  prior_epochs: List[Dict[str, Any]]) -> ConsolidationResult:
        """§7.4 — templated empty epoch. No deltas, no recalibration, no
        knowledge writes: the mock cannot judge what a batch means, and
        inventing deltas would put fiction into the identity store, which
        is the one place in this system that is supposed to be an
        auditable record of what actually happened."""
        return ConsolidationResult(
            deltas=[],
            decided_by="deterministic",
            diagnostics={"source_substrate": "mock",
                         "source_model": "none (mock tier, zero LLM cost)",
                         "batch_size": len(batch)},
        )


__all__ = ["ConsolidatorMock"]
