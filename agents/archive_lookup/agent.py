"""
Archive-lookup agents — MOCK tier (v0.35b, §13.1).

Mocked first, per Daniel's 2026-08-24 call and §13.1's standing
discipline: mock every role, replace one per phase. These two are
read-only retrieval with no gating power, so a lighter bring-up was on
the table — but the precedent won, and it buys something real here. The
mock proves the shape of the family (one class, two configurations, the
shared keyword contract, the read-only posture, the bundle slot) with no
credential and no cost, so when the live tier lands the only new thing
being tested is the retrieval judgment itself.

What the mock does: reports whether its store holds anything at all, in
the shared format, and says so honestly. It does not pretend to judge
relevance — it cannot — so `relevant` reflects "there is recorded
material here", not "this material bears on this event". That distinction
is exactly what the live tier is for, and overstating it in the mock
would make the fan-out tests pass for the wrong reason.
"""
from __future__ import annotations

from typing import Any, List

from agents.archive_lookup import contract
from agents.archive_lookup.base import ArchiveLookupBase
from agents.archive_lookup.contract import Findings
from bus.envelope import Envelope


class ArchiveLookupMock(ArchiveLookupBase):
    tier = "mock"

    def look(self, envelope: Envelope, records: List[Any]) -> Findings:
        if not records:
            return contract.silent(f"{self.store_kind} store is empty")
        return Findings(
            findings=f"{len(records)} recorded {self.store_kind} entries "
                     f"(mock tier: not assessed for relevance)",
            relevant=False,
            decided_by="deterministic",
            diagnostics={"source_substrate": "mock",
                         "source_model": "none (mock tier, zero LLM cost)",
                         "records_seen": len(records)},
        )


__all__ = ["ArchiveLookupMock"]
