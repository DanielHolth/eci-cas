"""Shared `to_meta()` shape for the per-event result dataclasses
(Analytics' Recommendation, the archive-lookup family's Findings): a
`decided_by` field, plus any role-specific extras, with `diagnostics`
merged in last."""
from __future__ import annotations

from typing import Any, Dict


def build_meta(decided_by: str, diagnostics: Dict[str, Any],
                **extra: Any) -> Dict[str, Any]:
    meta: Dict[str, Any] = {"decided_by": decided_by, **extra}
    if diagnostics:
        meta.update(diagnostics)
    return meta


__all__ = ["build_meta"]
