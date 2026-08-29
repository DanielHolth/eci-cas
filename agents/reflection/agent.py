"""ReflectionMock — deterministic tier, zero cost (dispatch #4, 2026-08-29).

Always outcome="silent". A mock that invented patterns out of templated
text would be worse than useless here — Reflection's entire job is
finding a REAL pattern (contract.py's rule 1), and there is no
deterministic way to fake one. Silence is the honest zero-cost answer,
the same way ArchiveLookupMock reports nothing rather than a guess.
"""
from __future__ import annotations

from typing import Any, Dict, List

from agents.reflection.base import ReflectionBase
from agents.reflection.contract import ReflectionResult


class ReflectionMock(ReflectionBase):
    tier = "mock"

    def reflect(self, pending: List[Dict[str, Any]],
                prior_learnings: List[Dict[str, Any]]) -> ReflectionResult:
        return ReflectionResult(outcome="silent", decided_by="deterministic")


__all__ = ["ReflectionMock"]
