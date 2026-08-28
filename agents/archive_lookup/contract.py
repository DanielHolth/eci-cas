"""
The archive-lookup family's output contract.

One shape shared by Personality and Knowledge: terse keyword findings,
matching Analytics' style so Intent pattern-matches one format across all
analytical slots. Fallback posture: fail toward SILENCE, never invention.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

from agents.shared.decision import build_meta

#: No longer enforced (2026-08-29, Daniel) — the 80-char cap this used to
#: apply was cutting findings off mid-word, which read as broken rather
#: than terse. Kept as a name only for anything still importing it;
#: nothing slices against it any more.
MAX_FINDINGS_CHARS = 80

#: How many Archive records one lookup reads. Bounded for the same reason
#: Analytics' working window is: this runs on every event.
DEFAULT_QUERY_LIMIT = 8


@dataclass(frozen=True)
class Findings:
    """One archive-grounded agent's contribution to a single event."""

    findings: str = ""
    relevant: bool = False
    decided_by: str = "deterministic"     # llm | fallback | deterministic
    diagnostics: Dict[str, Any] = field(default_factory=dict)

    def to_meta(self) -> Dict[str, Any]:
        return build_meta(self.decided_by, self.diagnostics,
                          findings=self.findings, relevant=self.relevant)


#: Code-fixed backstop, mirroring the other roles' contracts: this
#: survives an operator blanking `system_instruction`.
RESPONSE_CONTRACT = """
Reply with comma-separated keywords relevant to this event, drawn only
from the records you were given. Nothing else — no JSON, no sentences,
no reasoning. If nothing in the records bears on this event, reply with
exactly: NONE
"""


def build_prompt(content: str, records: Optional[List[Any]], *,
                 brief: str = "") -> str:
    """One event, one bounded slice of one Archive store.

    No conversation history, no persona, no cross-event state: this
    family sees the single current event and nothing else (v0.35b). That
    narrowness is the point — it is what makes these agents cheap enough
    to run four-way in parallel on every event."""
    lines = []
    if brief:
        lines += [brief, ""]
    lines += [f"THE EVENT: {content}", "", "WHAT THE ARCHIVE HOLDS:"]
    if records:
        for record in records:
            lines.append(f"  - {str(record)[:240]}")
    else:
        lines.append("  (nothing recorded yet)")
    return "\n".join(lines)


def parse(text: str) -> Findings:
    """Parse plain-text findings. 'NONE' means nothing relevant."""
    cleaned = text.strip()
    if not cleaned or cleaned.upper() == "NONE":
        return Findings(findings="", relevant=False, decided_by="llm")
    return Findings(findings=cleaned, relevant=True, decided_by="llm")


def silent(reason: str = "", decided_by: str = "deterministic") -> Findings:
    """Nothing to add. A complete answer, not a degraded one."""
    diagnostics = {"reason": reason[:200]} if reason else {}
    return Findings(findings="", relevant=False, decided_by=decided_by,
                    diagnostics=diagnostics)


def fallback(reason: str) -> Findings:
    """What a lookup says when its substrate answer can't be used: the
    same thing it says when the archive holds nothing — nothing. See the
    module docstring on why this fails toward silence."""
    return Findings(findings="", relevant=False, decided_by="fallback",
                    diagnostics={"degraded": True, "reason": reason[:200]})


__all__ = ["Findings", "RESPONSE_CONTRACT",
           "MAX_FINDINGS_CHARS", "DEFAULT_QUERY_LIMIT",
           "build_prompt", "parse", "silent", "fallback"]
