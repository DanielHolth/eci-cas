"""
Analytics — LIVE tier (§5.4, §13.4).

Substrate-backed reasoning. Constrains the SHAPE of the answer (validated
by contract.parse) and the CONSEQUENCE of a bad one (fallback to
deterministic template). Analytics never speaks as the persona — it
advises Intent, which owns the wording. Every call is stateless and
single-turn.
"""
from __future__ import annotations

import time
from typing import Any, Dict, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from substrates.base import (
    CompletionError,
    FailureKind,
    Substrate,
    SubstrateError,
)

from agents.analytics import contract
from agents.analytics.base import AnalyticsBase
from agents.analytics.contract import ContractViolation, Recommendation, Task

DEFAULT_SYSTEM_INSTRUCTION = (
    "You are ANALYTICS, the reasoning and working-memory agent in a "
    "multi-agent system. You think about events and advise other agents. "
    "You have no persona and you never address the human directly."
)


class AnalyticsAgent(AnalyticsBase):
    """Substrate-backed Analytics. Drop-in replacement for AnalyticsMock."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, substrate: Substrate, archive=None, *,
                 system_instruction: str = "",
                 temperature: float = 0.2,
                 max_tokens: Optional[int] = None,
                 strict: bool = False,
                 budget=None,
                 structured_store=None):
        self.substrate = substrate
        self.budget = budget
        self.system_instruction = (system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        self.strict = bool(strict)
        self._structured_store = structured_store
        self._schema_index_cache: Optional[list] = None
        super().__init__(bus, archive)

    def think(self, envelope: Envelope, task: Task) -> Recommendation:
        if self.budget is not None and not self.budget.should_call_substrate():
            # Budget mode. Reuse the SAME fallback a substrate failure
            # would produce — budget mode has no degraded behaviour of its
            # own, so there is nothing extra to get wrong.
            degraded = contract.fallback(
                envelope, task,
                f"budget mode ({self.budget.state.reason or 'manual'})")
            self.metrics["fallbacks"] += 1
            return Recommendation(
                recommendation=degraded.recommendation,
                decided_by="budget",
                diagnostics={"budget_mode": True,
                             "budget_reason": self.budget.state.reason or "manual",
                             "source_substrate": self.substrate.substrate_class},
            )

        try:
            text, latency_ms, usage = self._ask(envelope, task)
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            # Parse AFTER recording: a call that returned unusable text
            # still happened and was still paid for.
            recommendation = contract.parse(text, task)
            return Recommendation(
                recommendation=recommendation.recommendation,
                knowledge_paths=recommendation.knowledge_paths,
                decided_by="llm",
                diagnostics=self._diagnostics(latency_ms=latency_ms, usage=usage,
                                              cost_usd=cost),
            )
        except (SubstrateError, ContractViolation, ValueError) as exc:
            if isinstance(exc, CompletionError) and self.budget is not None:
                self.budget.record_failure(
                    getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))
            if self.strict:
                raise
            degraded = contract.fallback(envelope, task, f"{type(exc).__name__}: {exc}")
            return Recommendation(
                recommendation=degraded.recommendation,
                decided_by="fallback",
                diagnostics={**self._diagnostics(), **degraded.diagnostics},
            )

    # ---- Substrate call ---------------------------------------------------

    def _ask(self, envelope: Envelope, task: Task):
        system = (self.system_instruction + "\n" + contract.RESPONSE_CONTRACT).strip()
        user = contract.build_prompt(
            envelope, task,
            recent_events=self.recent_events(),
            prior_knowledge=self.prior_knowledge(),
            schema_index=self._get_schema_index(),
        )

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

    def _get_schema_index(self) -> Optional[list]:
        if self._structured_store is None:
            return None
        if self._schema_index_cache is None:
            self._schema_index_cache = self._structured_store.schema_index("knowledge")
        return self._schema_index_cache

    def refresh_schema_index(self):
        """Called on ArchiveWritten events to invalidate the cache."""
        self._schema_index_cache = None

    # ---- Diagnostics ------------------------------------------------------

    def _diagnostics(self, *, latency_ms: Optional[float] = None,
                     usage: Optional[Dict[str, Any]] = None,
                     cost_usd: Optional[float] = None) -> Dict[str, Any]:
        """Recorded into meta.analytics on every hop, and therefore into
        the Archive queue log. Mirrors §7.4's source_substrate (stable,
        analytical) / source_model (forensic, resolved at write time)
        split, so Diagnostic (§12) can later trace which substrate
        produced which judgment."""
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
        return diagnostics


__all__ = ["AnalyticsAgent", "DEFAULT_SYSTEM_INSTRUCTION"]
