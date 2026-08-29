"""Reflection — LIVE tier (dispatch #4, 2026-08-29).

Same discipline as every other live tier: a validated response contract,
a deterministic fallback, budget-mode awareness. Fallback posture:
fail-silent, not fail-open in the writing sense — an unusable answer
means this batch's pattern (if any) is lost, which is the same
recoverable-state-loss trade Consolidator makes on a substrate outage.
Reflection gates nothing and never blocks the live queue; it runs off
Governance's `_conclude()` fork, entirely out of band from Sensory's
fan-out.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from substrates.base import Substrate, SubstrateError
from substrates.parsing import extract_json_object

from agents.reflection.base import ReflectionBase
from agents.reflection.contract import RESPONSE_CONTRACT, ReflectionResult, build_prompt, parse
from agents.shared.substrate_call import record_budget_failure, timed_complete

DEFAULT_SYSTEM_INSTRUCTION = (
    "You are REFLECTION, the meta-cognitive agent of a multi-agent system. "
    "You look back over a batch of concluded incidents and decide whether "
    "there is a real, durable pattern worth remembering — not a summary "
    "of what happened, a lesson about how you tend to react. You never "
    "speak to the human directly and you never gate anything; you may "
    "only write an internal memory or raise one idea for the normal "
    "pipeline to consider."
)


class ReflectionAgent(ReflectionBase):
    """Substrate-backed Reflection. Drop-in replacement for ReflectionMock."""

    tier = "live"

    def __init__(self, bus, substrate: Substrate, *,
                 structured_store=None, sensory=None,
                 batch_size: int = 5,
                 system_instruction: str = "",
                 temperature: float = 0.3,
                 max_tokens: Optional[int] = None,
                 strict: bool = False,
                 budget=None):
        self.substrate = substrate
        self.budget = budget
        self.system_instruction = (system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        self.strict = bool(strict)
        super().__init__(bus, structured_store=structured_store, sensory=sensory,
                         batch_size=batch_size)

    def reflect(self, pending: List[Dict[str, Any]],
                prior_learnings: List[Dict[str, Any]]) -> ReflectionResult:
        if self.budget is not None and not self.budget.should_call_substrate():
            return ReflectionResult(outcome="silent", decided_by="fallback",
                                    diagnostics={"degraded": True, "reason": "budget mode"})

        system = (self.system_instruction + "\n" + RESPONSE_CONTRACT).strip()
        user = build_prompt(pending, prior_learnings)

        try:
            text, latency_ms, usage = timed_complete(
                self.substrate, self.metrics,
                system=system, user=user, temperature=self.temperature,
                max_tokens=self.max_tokens, prefill="{",
            )
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            obj = extract_json_object(text)
            result = parse(obj)
            result.decided_by = "llm"
            result.diagnostics.update({
                "source_substrate": self.substrate.substrate_class,
                "source_model": self.substrate.model,
                "latency_ms": latency_ms, "usage": usage,
                "est_cost_usd": cost or None,
            })
            return result
        except (SubstrateError, ValueError) as exc:
            record_budget_failure(exc, self.budget)
            if self.strict:
                raise
            return ReflectionResult(
                outcome="silent", decided_by="fallback",
                diagnostics={"degraded": True, "reason": f"{type(exc).__name__}: {exc}"[:200]})


__all__ = ["ReflectionAgent", "DEFAULT_SYSTEM_INSTRUCTION"]
