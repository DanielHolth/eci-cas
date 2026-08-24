"""
Intent — LIVE tier (§5.5, §13.4, v0.35c/e/g).

The role with the persona, and — as of v0.35e — the role with the veto.
Mirrors agents/analytics/live.py's shape deliberately: substrate resolved
from the manifest, a validated response contract with a deterministic
fallback per register, budget-mode awareness, the same attribution fields
on every hop.

What changed from Phase 0.4
----------------------------
  * Four registers, not two. ADVISE and REFUSE are unchanged in
    behaviour. REVIEW (Security yellow) and REVISE (Security red) are
    new, they GATE, and they fail closed — see contract.py.

  * Consolidation is gone from this file entirely. `reconcile()`, the
    consolidation prompt, and the consolidation substrate all moved to
    `agents/consolidator/` (v0.35f). Intent voices; Consolidator
    remembers.

  * The persona is CACHED (v0.35g), hydrated once at construction and
    refreshed only on Consolidator's EpochWritten ping. Phase 0.4 called
    `hydrate()` — and therefore `archive.query("identity")` — on every
    single voicing call. That read is gone.

  * The prompt carries the conversation window (v0.35c) and the bundle's
    Personality/Knowledge findings (v0.35b) when they are present, which
    is the grounding that made moving the revision loop here an
    improvement rather than a downgrade: by the time Security reds
    something, Intent already holds every analytical read of the event
    plus the broader conversation none of the single-event agents see.
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
    "You are INTENT, the persona of a multi-agent system, speaking "
    "directly to the human. Security is a hard stop you never argue "
    "with; where it cannot decide, the judgment is yours, and unsure "
    "means no."
)


class IntentAgent(IntentBase):
    """Substrate-backed Intent. Drop-in replacement for IntentMock."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, substrate: Substrate, archive=None, *,
                 context_events: int = DEFAULT_CONTEXT_EVENTS,
                 consolidator=None,
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
        # strict=True re-raises instead of degrading. For calibration runs
        # where a silent fallback would hide a bad prompt; never for
        # production, where a substrate outage must not stop the pipeline.
        self.strict = bool(strict)
        super().__init__(bus, archive, context_events=context_events,
                         consolidator=consolidator)

    # ---- Voicing ------------------------------------------------------------

    def voice(self, envelope: Envelope, task: Task) -> Speech:
        recommendation = str(envelope.meta.get("recommendation") or envelope.content)
        concern = str(envelope.meta.get("concern", "")).strip()
        blocked = str(envelope.meta.get("proposed_action", "")).strip()

        if self.budget is not None and not self.budget.should_call_substrate():
            reason = f"budget mode ({self.budget.state.reason or 'manual'})"
            degraded = contract.fallback(task, reason, recommendation=recommendation,
                                         concern=concern)
            # Budget mode does not get to approve a gated hop. The
            # per-register fallback already declines on REVIEW/REVISE
            # (contract.fallback_gated); this just records why.
            return Speech(
                text=degraded.text, proceed=degraded.proceed,
                concern=degraded.concern, decided_by="budget",
                diagnostics={**degraded.diagnostics, "budget_mode": True,
                             "budget_reason": self.budget.state.reason or "manual",
                             "source_substrate": self.substrate.substrate_class})

        try:
            text, latency_ms, usage = self._ask(
                envelope, task, recommendation=recommendation, concern=concern,
                blocked=blocked)
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            # A contract violation is NOT a substrate failure — the call
            # succeeded and was paid for; the model just answered out of
            # shape. Parsing after recording keeps the two accounted
            # separately, so a run of bad JSON never latches budget mode.
            parsed = contract.parse(text, task, recommendation=recommendation,
                                    concern=concern, blocked=blocked)
            return Speech(
                text=parsed.text, proceed=parsed.proceed, concern=parsed.concern,
                decided_by="llm",
                diagnostics={**self._diagnostics(), "latency_ms": latency_ms,
                             "usage": usage, "est_cost_usd": cost or None,
                             "epoch_count": self.persona.epoch_count})
        except (SubstrateError, ContractViolation, ValueError) as exc:
            # SubstrateError, not CompletionError. CredentialsError is its
            # sibling, not its subclass, and both providers build their
            # client OUTSIDE their own try/except — so a key rotated away
            # after boot, or an SDK that fails to import lazily, raises
            # something this used to let escape. On a gating register that
            # meant skipping the fail-closed fallback entirely and
            # unwinding the whole synchronous pipeline instead. Bootstrap's
            # credential check is offline and one-shot; it cannot cover
            # this.
            if isinstance(exc, CompletionError) and self.budget is not None:
                self.budget.record_failure(
                    getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))
            if self.strict:
                raise
            reason = f"{type(exc).__name__}: {exc}"
            degraded = contract.fallback(task, reason, recommendation=recommendation,
                                         concern=concern)
            return Speech(text=degraded.text, proceed=degraded.proceed,
                          concern=degraded.concern, decided_by="fallback",
                          diagnostics={**degraded.diagnostics, **self._diagnostics()})

    # ---- Substrate call -----------------------------------------------------

    def _ask(self, envelope: Envelope, task: Task, *, recommendation: str,
             concern: str, blocked: str):
        system = (self.system_instruction + "\n"
                  + contract.RESPONSE_CONTRACTS[task]).strip()
        user = contract.build_prompt(
            task, envelope, self.persona,
            recommendation=recommendation,
            concern=concern,
            blocked=blocked,
            proposed=blocked,
            verdict_detail=self._verdict_detail(envelope),
            recommendations=self._recommendations(envelope),
            reflex=envelope.meta.get("reflex"),
            recent=self.recent_conversation(),
            reflex_already_acted=bool(envelope.meta.get("reflex_already_acted")),
            reflex_action=envelope.meta.get("reflex_action"),
        )
        self.metrics["llm_calls"] += 1
        started = time.perf_counter()
        response = self.substrate.complete(
            system=system, user=user, temperature=self.temperature,
            max_tokens=self.max_tokens, prefill="{",
        )
        latency_ms = round((time.perf_counter() - started) * 1000, 1)
        return response.text, latency_ms, dict(response.usage or {})

    @staticmethod
    def _verdict_detail(envelope: Envelope) -> str:
        """What Security actually said, for the two registers that answer
        it. Security's rule engine may or may not attach prose; when it
        doesn't, the verdict itself is the whole of the message, and
        saying so plainly beats inventing a reason it never gave."""
        concern = str(envelope.meta.get("security_concern") or "").strip()
        if concern:
            return concern
        verdict = str(envelope.meta.get("verdict") or "").strip()
        return f"verdict: {verdict}" if verdict else ""

    @staticmethod
    def _recommendations(envelope: Envelope) -> List[Dict[str, Any]]:
        """Analytics', Personality's and Knowledge's answers, as the one
        shared {sender, keywords, proceed, concern} shape Governance
        projects them to (Daniel, 2026-08-24) — see
        agents/governance/buffer.py's EventState.recommendations() and
        agents/shared/recommendation.py. Empty until the fan-out exists,
        or on a route that carried none."""
        return list(envelope.meta.get("recommendations") or [])

    # ---- Diagnostics --------------------------------------------------------

    def _diagnostics(self) -> Dict[str, Any]:
        return {
            "source_substrate": self.substrate.substrate_class,
            "source_model": self.substrate.model,
            "provider": self.substrate.provider_name,
        }


__all__ = ["IntentAgent", "DEFAULT_SYSTEM_INSTRUCTION"]
