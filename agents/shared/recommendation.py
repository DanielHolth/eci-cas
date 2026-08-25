"""The one shape Analytics, Personality and Knowledge converge on at
Governance's bundle boundary (Daniel, 2026-08-24; cut down further to
sender+keywords only, Daniel, 2026-08-25).

Before this, Analytics' `Recommendation` (agents/analytics/contract.py)
and the archive-lookup family's `Findings` (agents/archive_lookup/contract.py)
were two hand-copied dataclasses that already agreed on every field except
its name. Each agent still keeps its own working dataclass — the field
names there stay task-specific, because "recommendation" and "findings"
mean genuinely different things while an agent is reasoning about them.
But the moment Governance bundles the three parallel answers for Intent,
they were always the same kind of thing wearing three different labels,
and now they say so: one shape, named once, and it is what actually goes
out on the wire (Governance's `EventState.recommendations()`,
agents/governance/buffer.py).

2026-08-25: `proceed`/`concern` are gone from this shape entirely.
Analytics never had real gating power after v0.35e moved the veto to
Security/Intent — "proceed" surviving as a field was dead weight that
Governance's own routing code was still reading (a real remnant, not a
hypothetical one), and it fed Intent a stale ADVISE/REFUSE fork nobody
actually wanted. The only real gate in this system is Security's red
verdict. Intent's bundle is now exactly `{sender, keywords}` — pure
signal, nothing to weigh, nothing to mislead it into paranoia over a
non-decision dressed up as one.

Diagnostic metadata (source_model, latency_ms, usage, est_cost_usd,
records_considered) stays out of this shape on purpose — it lives in
Governance's own EventState/trace/logs, never in what reaches Intent.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict


@dataclass(frozen=True)
class RecommendationEntry:
    """One agent's contribution to Intent's recommendations array.

    sender    which agent said this: "Analytics" | "Personality" | "Knowledge"
    keywords  the terse keyword/phrase content — Analytics' recommendation
              text, or a lookup's findings text
    """

    sender: str
    keywords: str

    def to_dict(self) -> Dict[str, Any]:
        return {"sender": self.sender, "keywords": self.keywords}


__all__ = ["RecommendationEntry"]
