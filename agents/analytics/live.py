"""
Analytics — LIVE tier, Phase 0.2 (§5.4, §13.4).

The second cycle of the replacement sequence, and the first role that
genuinely needs a model. Phase 0.1 ended by proving Governance didn't;
this one is the opposite case, and the contrast is the point. Governance
routes, which is a lookup. Analytics reasons, which isn't.

What that changes about the guardrails
--------------------------------------
Governance got a routing whitelist: a closed set of legal answers,
checkable exactly. There is no equivalent here — enumerating the useful
recommendations in advance would mean not needing the model. So this
agent constrains the SHAPE of the answer and the CONSEQUENCE of a bad
one, rather than its content:

  * The response schema is fixed and validated (contract.parse).
  * Every task has a deterministic fallback, and two of the three fail
    toward not acting (contract.fallback).
  * Analytics never speaks as the persona. It advises Intent, which owns
    the wording and the values (§5.5). A model that starts writing the
    reply is still producing a valid recommendation — Intent simply
    treats it as advice, because that is the only thing this hop is wired
    to be.
  * Loop detection and the control plane never reach this class at all;
    AnalyticsBase answers both mechanically.

Substrate handling
------------------
The vendor is resolved from the manifest's substrate class table (§10.2)
and never named here. Model, provider, endpoint and credentials are all
one manifest edit away from being something else, which is the property
the whole layer exists for.

Every call is stateless and single-turn: one task, one event, and a
bounded working window. History does not accumulate in the prompt, which
is what keeps cost per event flat as the ecosystem's memory grows (§1).
"""
from __future__ import annotations

import time
from typing import Any, Dict, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from substrates.base import CompletionError, FailureKind, Substrate

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
                 budget=None):
        self.substrate = substrate
        #: BudgetManager, or None. When present it decides whether this
        #: agent may call its substrate at all, and it learns the outcome
        #: of every call it does allow (budget/state.py).
        self.budget = budget
        self.system_instruction = (system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        # strict=True re-raises instead of degrading. For calibration runs
        # where a silent fallback would hide a bad prompt; never for
        # production, where a substrate outage must not stop the pipeline.
        self.strict = bool(strict)
        super().__init__(bus, archive)

    def think(self, envelope: Envelope, task: Task) -> Recommendation:
        if self.budget is not None and not self.budget.should_call_substrate():
            # Budget mode. Reuse the SAME per-task fallbacks a substrate
            # failure would produce — budget mode has no degraded behaviour
            # of its own, so there is nothing extra to get wrong. Evaluate
            # still proceeds; Review and Revise still decline.
            degraded = contract.fallback(
                envelope, task,
                f"budget mode ({self.budget.state.reason or 'manual'})")
            self.metrics["fallbacks"] += 1
            return Recommendation(
                recommendation=degraded.recommendation,
                proceed=degraded.proceed,
                concern=degraded.concern,
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

            # A contract violation is NOT a substrate failure — the call
            # succeeded and was paid for; the model just answered out of
            # shape. Parsing after recording keeps the two accounted
            # separately, so a run of bad JSON never latches budget mode.
            recommendation = contract.parse(text, task)
            return Recommendation(
                recommendation=recommendation.recommendation,
                proceed=recommendation.proceed,
                concern=recommendation.concern,
                decided_by="llm",
                diagnostics=self._diagnostics(latency_ms=latency_ms, usage=usage,
                                              cost_usd=cost),
            )
        except (CompletionError, ContractViolation, ValueError) as exc:
            if isinstance(exc, CompletionError) and self.budget is not None:
                self.budget.record_failure(
                    getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))
            if self.strict:
                raise
            degraded = contract.fallback(envelope, task, f"{type(exc).__name__}: {exc}")
            return Recommendation(
                recommendation=degraded.recommendation,
                proceed=degraded.proceed,
                concern=degraded.concern,
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
        )

        self.metrics["llm_calls"] += 1
        started = time.perf_counter()
        response = self.substrate.complete(
            system=system,
            user=user,
            temperature=self.temperature,
            max_tokens=self.max_tokens,
            prefill="{",
        )
        latency_ms = round((time.perf_counter() - started) * 1000, 1)
        return response.text, latency_ms, dict(response.usage or {})

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
