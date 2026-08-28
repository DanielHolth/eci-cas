"""The shared shape Analytics, Personality and Knowledge converge on at
Governance's bundle boundary: {sender, keywords}. Each agent keeps its
own working dataclass internally; this is what goes on the wire."""
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
