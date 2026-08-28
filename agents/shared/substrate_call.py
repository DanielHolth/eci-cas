"""Shared low-level pieces of the live-tier substrate call: the timed
completion, the source_substrate/source_model/provider diagnostics base
every tier records (§7.4), and recording a CompletionError against
budget. Each live tier keeps its own prompt-building, contract parsing,
and fallback/degrade shape — only the mechanical parts converge here."""
from __future__ import annotations

import time
from typing import Any, Dict, Optional, Tuple

from substrates.base import CompletionError, FailureKind, Substrate


def timed_complete(substrate: Substrate, metrics: Dict[str, int], **kwargs: Any
                    ) -> Tuple[str, float, Dict[str, Any]]:
    """Call substrate.complete(**kwargs), counted and timed. Returns
    (text, latency_ms, usage)."""
    metrics["llm_calls"] += 1
    started = time.perf_counter()
    response = substrate.complete(**kwargs)
    latency_ms = round((time.perf_counter() - started) * 1000, 1)
    return response.text, latency_ms, dict(response.usage or {})


def base_diagnostics(substrate: Substrate, *, latency_ms: Optional[float] = None,
                      usage: Optional[Dict[str, Any]] = None,
                      cost_usd: Optional[float] = None,
                      **extra: Any) -> Dict[str, Any]:
    diagnostics: Dict[str, Any] = {
        "source_substrate": substrate.substrate_class,
        "source_model": substrate.model,
        "provider": substrate.provider_name,
    }
    if latency_ms is not None:
        diagnostics["latency_ms"] = latency_ms
    if usage:
        diagnostics["usage"] = usage
    if cost_usd:
        diagnostics["est_cost_usd"] = cost_usd
    diagnostics.update({k: v for k, v in extra.items() if v is not None})
    return diagnostics


def record_budget_failure(exc: Exception, budget) -> None:
    if isinstance(exc, CompletionError) and budget is not None:
        budget.record_failure(getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))


__all__ = ["timed_complete", "base_diagnostics", "record_budget_failure"]
