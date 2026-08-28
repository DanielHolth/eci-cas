"""
Intent — LIVE tier.

Intent does one thing: speak to the human. Plain text output, no JSON.
Security and Governance own all safety routing — Intent never judges
whether something should proceed, it just speaks.
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

from agents.intent import contract
from agents.intent.base import DEFAULT_CONTEXT_EVENTS, IntentBase
from agents.intent.contract import ContractViolation, Speech, Task

DEFAULT_SYSTEM_INSTRUCTION = (
    "You are INTENT: the voice of a multi-agent system. "
    "Be concise and natural — short, direct replies. "
    "If Security is yellow or red, revise or your message is blocked."
)


class IntentAgent(IntentBase):
    """Substrate-backed Intent. Drop-in replacement for IntentMock."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, substrate: Substrate, archive=None, *,
                 context_events: int = DEFAULT_CONTEXT_EVENTS,
                 system_instruction: str = "",
                 temperature: float = 0.7,
                 max_tokens: Optional[int] = None,
                 strict: bool = False,
                 budget=None):
        self.substrate = substrate
        self.budget = budget
        self.system_instruction = (system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        self.strict = bool(strict)
        super().__init__(bus, archive, context_events=context_events)

    # ---- Voicing ------------------------------------------------------------

    def voice(self, envelope: Envelope, task: Task) -> Speech:
        recommendation = str(envelope.meta.get("recommendation") or envelope.content)

        if self.budget is not None and not self.budget.should_call_substrate():
            reason = f"budget mode ({self.budget.state.reason or 'manual'})"
            degraded = contract.fallback(task, reason, recommendation=recommendation)
            return Speech(
                text=degraded.text, decided_by="budget",
                diagnostics={**degraded.diagnostics, "budget_mode": True,
                             "budget_reason": self.budget.state.reason or "manual",
                             "source_substrate": self.substrate.substrate_class})

        try:
            text, latency_ms, usage = self._ask(envelope, recommendation=recommendation)
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            parsed = contract.parse(text, task, recommendation=recommendation)
            return Speech(
                text=parsed.text, decided_by="llm",
                diagnostics={**self._diagnostics(), "latency_ms": latency_ms,
                             "usage": usage, "est_cost_usd": cost or None})
        except (SubstrateError, ContractViolation, ValueError) as exc:
            if isinstance(exc, CompletionError) and self.budget is not None:
                self.budget.record_failure(
                    getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))
            if self.strict:
                raise
            reason = f"{type(exc).__name__}: {exc}"
            degraded = contract.fallback(task, reason, recommendation=recommendation)
            return Speech(text=degraded.text, decided_by="fallback",
                          diagnostics={**degraded.diagnostics, **self._diagnostics()})

    # ---- Substrate call -----------------------------------------------------

    def _ask(self, envelope: Envelope, *, recommendation: str):
        system = (self.system_instruction + "\n"
                  + contract.RESPONSE_CONTRACT).strip()
        user = contract.build_prompt(
            Task.ADVISE, envelope, self.persona,
            recommendation=recommendation,
            recommendations=self._recommendations(envelope),
            reflex=envelope.meta.get("reflex"),
            recent=self.recent_conversation(),
            reflex_already_acted=bool(envelope.meta.get("reflex_already_acted")),
            reflex_action=envelope.meta.get("reflex_action"),
            security_concern=self._security_concern(envelope),
        )
        self.metrics["llm_calls"] += 1
        started = time.perf_counter()
        response = self.substrate.complete(
            system=system, user=user, temperature=self.temperature,
            max_tokens=self.max_tokens,
        )
        latency_ms = round((time.perf_counter() - started) * 1000, 1)
        return response.text, latency_ms, dict(response.usage or {})

    @staticmethod
    def _security_concern(envelope: Envelope) -> Optional[str]:
        """Security concern from a yellow/red verdict, if present."""
        concern = str(envelope.meta.get("security_concern") or "").strip()
        if concern:
            return concern
        verdict = str(envelope.meta.get("verdict") or "").strip()
        if verdict and verdict not in ("green", ""):
            return f"verdict: {verdict}"
        return None

    @staticmethod
    def _recommendations(envelope: Envelope) -> List[Dict[str, Any]]:
        return list(envelope.meta.get("recommendations") or [])

    # ---- Diagnostics --------------------------------------------------------

    def _diagnostics(self) -> Dict[str, Any]:
        return {
            "source_substrate": self.substrate.substrate_class,
            "source_model": self.substrate.model,
            "provider": self.substrate.provider_name,
        }


__all__ = ["IntentAgent", "DEFAULT_SYSTEM_INSTRUCTION"]
