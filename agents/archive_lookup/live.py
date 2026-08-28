"""
The archive-lookup family — LIVE tier.

Substrate-backed relevance judgment over a bounded set of archive
records. Personality and Knowledge are two instances of this one class,
differing only in which store they read and their brief.

The model decides which records bear on the current event (a relevance
judgment that can't be written as a rule). It is NOT asked to recall
anything — the supplied records are the only permissible source.

Fallback: SILENCE. Cost: runs on every event, twice, in parallel.
Empty-archive short-circuits without a substrate call.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from substrates.base import Substrate, SubstrateError

from agents.archive_lookup import contract
from agents.archive_lookup.base import ArchiveLookupBase
from agents.archive_lookup.contract import Findings
from agents.shared.substrate_call import (
    base_diagnostics,
    record_budget_failure,
    timed_complete,
)

DEFAULT_SYSTEM_INSTRUCTION = (
    "You are a memory-lookup agent in a multi-agent system. You are given "
    "one event and a set of records from this system's own archive. You "
    "report which records bear on the event, as keywords. You never "
    "address the human, you never invent a record, and you never answer "
    "from your own knowledge — only from the records you are given."
)


class ArchiveLookupAgent(ArchiveLookupBase):
    """Substrate-backed lookup. Drop-in replacement for ArchiveLookupMock."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, archive, substrate: Substrate, *,
                 role: str,
                 store_kind: Optional[str] = None,
                 topic: Optional[str] = None,
                 brief: str = "",
                 query_limit: int = contract.DEFAULT_QUERY_LIMIT,
                 system_instruction: str = "",
                 temperature: float = 0.2,
                 max_tokens: Optional[int] = None,
                 strict: bool = False,
                 budget=None,
                 structured_store=None):
        self.substrate = substrate
        self.budget = budget
        self.system_instruction = (
            system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        #: strict=True re-raises instead of degrading — calibration runs
        #: only, where a silent fallback hides a bad prompt. Never in
        #: production: a substrate outage must not stop the pipeline.
        self.strict = bool(strict)
        super().__init__(bus, archive, role=role, store_kind=store_kind,
                         topic=topic, brief=brief, query_limit=query_limit,
                         structured_store=structured_store)
        self.metrics.setdefault("skipped_empty", 0)

    # ---- The judgment -----------------------------------------------------

    def look(self, envelope: Envelope, records: List[Any]) -> Findings:
        if not records:
            # Nothing to be relevant TO. Not a degraded answer and not a
            # fallback — this is the correct answer, arrived at without
            # spending anything to hear it.
            self.metrics["skipped_empty"] += 1
            return contract.silent("archive holds no records for this store")

        if self.budget is not None and not self.budget.should_call_substrate():
            # Budget mode degrades to the same silence a failure does.
            # There is no cheaper real answer to give: relevance over
            # free-text records is exactly the part that needed a model.
            self.metrics["fallbacks"] += 1
            return Findings(
                findings="", relevant=False, decided_by="budget",
                diagnostics={"budget_mode": True,
                             "budget_reason": self.budget.state.reason or "manual",
                             "source_substrate": self.substrate.substrate_class})

        try:
            text, latency_ms, usage = self._ask(envelope, records)
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            # Parse AFTER recording: a call that returned unusable text
            # still happened and was still paid for.
            findings = contract.parse(text)
            return Findings(
                findings=findings.findings,
                relevant=findings.relevant,
                decided_by="llm",
                diagnostics=self._diagnostics(latency_ms=latency_ms, usage=usage,
                                              cost_usd=cost,
                                              records_considered=len(records)),
            )
        except (SubstrateError, ValueError) as exc:
            record_budget_failure(exc, self.budget)
            if self.strict:
                raise
            degraded = contract.fallback(f"{type(exc).__name__}: {exc}")
            return Findings(
                findings=degraded.findings,
                relevant=degraded.relevant,
                decided_by="fallback",
                diagnostics={**self._diagnostics(), **degraded.diagnostics},
            )

    # ---- Substrate call ---------------------------------------------------

    def _ask(self, envelope: Envelope, records: List[Any]):
        system = (self.system_instruction + "\n" + contract.RESPONSE_CONTRACT).strip()
        user = contract.build_prompt(str(envelope.content), records,
                                     brief=self.brief)

        return timed_complete(
            self.substrate, self.metrics,
            system=system, user=user,
            temperature=self.temperature, max_tokens=self.max_tokens,
        )

    # ---- Diagnostics ------------------------------------------------------

    def _diagnostics(self, *, latency_ms: Optional[float] = None,
                     usage: Optional[Dict[str, Any]] = None,
                     cost_usd: Optional[float] = None,
                     records_considered: Optional[int] = None) -> Dict[str, Any]:
        """Same source_substrate / source_model split every other live tier
        records (§7.4), so Diagnostic (§12) can trace which substrate
        produced which judgment. `records_considered` is this family's
        own addition: 'relevant: false' means something quite different
        over 8 records than over 1."""
        return base_diagnostics(self.substrate, latency_ms=latency_ms,
                                usage=usage, cost_usd=cost_usd,
                                records_considered=records_considered)


__all__ = ["ArchiveLookupAgent", "DEFAULT_SYSTEM_INSTRUCTION"]
