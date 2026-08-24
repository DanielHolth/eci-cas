"""The one shape Analytics, Personality and Knowledge converge on at
Governance's bundle boundary (Daniel, 2026-08-24).

Before this, Analytics' `Recommendation` (agents/analytics/contract.py)
and the archive-lookup family's `Findings` (agents/archive_lookup/contract.py)
were two hand-copied dataclasses that already agreed on every field except
its name: recommendation/findings text, proceed/relevant boolean, concern,
decided_by, diagnostics. Each agent still keeps its own working dataclass
— the field names there stay task-specific, because "recommendation" and
"findings" mean genuinely different things while an agent is reasoning
about them. But the moment Governance bundles the three parallel answers
for Intent, they were always the same kind of thing wearing three
different labels, and now they say so: one shape, named once, and it is
what actually goes out on the wire (Governance's `EventState.recommendations()`,
agents/governance/buffer.py).

Intent used to read three named blocks out of a `bundle` dict
(`bundle["analytics"]`, `bundle["personality"]`, `bundle["knowledge"]`),
each carrying its full internal shape — tier, decided_by, diagnostics,
all of it. None of that is for Intent's eyes; a persona deciding what to
say does not need to know which tier answered or why a fallback fired.
It needs to know who said what, and whether they thought it should
proceed. So Intent now reads one list, `meta["recommendations"]`, each
entry this shape and nothing else.
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
    proceed   Analytics: should this go forward. Personality/Knowledge:
              was anything relevant found. Same slot, same question at the
              point Intent reads it — "is this worth weighing" — even
              though the two families arrive at it by different reasoning.
    concern   one short reason, present only when proceed is false / there
              was nothing to surface
    """

    sender: str
    keywords: str
    proceed: bool = True
    concern: str = ""

    def to_dict(self) -> Dict[str, Any]:
        out: Dict[str, Any] = {
            "sender": self.sender,
            "keywords": self.keywords,
            "proceed": self.proceed,
        }
        if self.concern:
            out["concern"] = self.concern
        return out


__all__ = ["RecommendationEntry"]
