"""
The archive-lookup family — LIVE tier (Phase 0.6).

v0.35b built this family mock-first: the mock proved the wiring, the
parallel fan-out and the read-only posture at zero cost, and left the one
thing a model is actually for — deciding what in a pile of records
BEARS on the event in front of you — as the open item. This is that item.

One class, two instances, exactly as the mock tier is. Personality and
Knowledge differ in which store they read and how their brief is worded;
neither difference belongs in code (see base.py's header).

What the model is and is not asked to do
----------------------------------------
It is asked: given this event and these N records, which of them matter,
stated as keywords. That is a relevance judgment over a bounded, supplied
set — the one thing here that cannot be written as a rule, because
relevance is about meaning.

It is NOT asked to recall anything. The records in the prompt are the
only permissible source, and the contract says so explicitly ("report
what the records say, not what you happen to know"). A lookup that
answers from parametric knowledge has quietly become a second Analytics
while still being labelled as memory — the failure mode that would make
Intent's bundle untrustworthy in a way no test would catch.

Fallback posture: SILENCE, never invention (contract.fallback). This
family gates nothing, so a substrate outage costs grounding, not safety.
Guessing at what the archive might have said would put fiction into
Intent's prompt with an authoritative label on it — strictly worse than
saying nothing.

Cost posture: this runs on EVERY event, twice (once per instance), in
parallel with Impulse and Analytics. That is why the empty-archive
short-circuit below exists, and why it is not merely an optimisation:
calling a model to reason over zero records is spending money to be told
there is nothing there.
"""
from __future__ import annotations

import time
from typing import Any, Dict, List, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from substrates.base import (
    CompletionError,
    FailureKind,
    Substrate,
    SubstrateError,
)

from agents.archive_lookup import contract
from agents.archive_lookup.base import ArchiveLookupBase
from agents.archive_lookup.contract import ContractViolation, Findings

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
                 budget=None):
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
                         topic=topic, brief=brief, query_limit=query_limit)
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

            # Parse AFTER recording: a call that returned bad JSON still
            # happened and was still paid for. Conflating the two would
            # let a run of malformed answers latch budget mode as if the
            # substrate were down.
            findings = contract.parse(text)
            return Findings(
                findings=findings.findings,
                relevant=findings.relevant,
                decided_by="llm",
                diagnostics=self._diagnostics(latency_ms=latency_ms, usage=usage,
                                              cost_usd=cost,
                                              records_considered=len(records)),
            )
        except (SubstrateError, ContractViolation, ValueError) as exc:
            if isinstance(exc, CompletionError) and self.budget is not None:
                self.budget.record_failure(
                    getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))
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

        self.metrics["llm_calls"] += 1
        started = time.perf_counter()
        response = self.substrate.complete(
            system=system,
            user=user,
            temperature=self.temperature,
            max_tokens=self.max_tokens,
        )
        latency_ms = round((time.perf_counter() - started) * 1000, 1)
        return response.text, latency_ms, dict(response.usage or {})

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
        diagnostics: Dict[str, Any] = {
            "source_substrate": self.substrate.substrate_class,
            "source_model": self.substrate.model,
            "provider": self.substrate.provider_name,
        }
        if latency_ms is not None:
            diagnostics["latency_ms"] = latency_ms
        if usage:
            diagnostics["usage"] = usage
        if cost_usd:
            diagnostics["est_cost_usd"] = cost_usd
        if records_considered is not None:
            diagnostics["records_considered"] = records_considered
        return diagnostics


__all__ = ["ArchiveLookupAgent", "DEFAULT_SYSTEM_INSTRUCTION"]
