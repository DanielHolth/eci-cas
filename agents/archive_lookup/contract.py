"""
The archive-lookup family's output contract (v0.35b).

One shape, shared by every agent in this family — today Personality and
Knowledge, tomorrow whatever else turns out to be "look something up in
Archive and say what's relevant." Daniel flagged early that these two are
unlikely to be the only ones of this character, so the contract is
defined once, here, rather than twice in two hand-copied agents.

The format matches Analytics' terse keyword style, and that is
load-bearing rather than cosmetic. Two reasons:

  1. Intent pattern-matches ONE shape across all three analytical slots
     in Governance's bundle (Analytics, Personality, Knowledge). Three
     different response shapes would mean three parsers and three ways
     for a slot to be misread.
  2. The (deferred, product-layer) avatar UI shows three coloured
     "thought bubble" streams, one per agent. For that to read as three
     voices of one mind rather than three unrelated widgets, the things
     being streamed have to be the same KIND of thing.

Fallback posture: fail toward SILENCE, never toward invention. These
agents gate nothing — they contribute grounding, and a lookup with
nothing to say should say nothing. `relevant: false` with empty findings
is a complete, honest answer, and it is also what an outage degrades to;
the alternative (guessing at what the archive might have said) would put
fiction into Intent's prompt with an authoritative label on it.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

from substrates.parsing import coerce_bool, extract_json_object

#: Findings are keywords, not prose. Tightened from 300 (Daniel,
#: 2026-08-25) — a live trace showed Personality answering in full
#: sentences ("active listener; reflect back what actually heard before
#: offering a take; ask rather than assume...") well within the old cap,
#: which flooded Intent's RECOMMENDATIONS block and, on at least one run,
#: visibly misled Intent (it read the essay as loaded context and reacted
#: to it rather than to the human). Matches Analytics'
#: MAX_RECOMMENDATION_CHARS (agents/analytics/contract.py) — the two
#: families share one keyword-terse shape at Governance's bundle boundary
#: (agents/shared/recommendation.py) and now share one length ceiling too.
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
        meta: Dict[str, Any] = {"findings": self.findings,
                                "relevant": self.relevant,
                                "decided_by": self.decided_by}
        if self.diagnostics:
            meta.update(self.diagnostics)
        return meta


class ContractViolation(ValueError):
    """The substrate's answer cannot be used. Always recoverable — the
    caller degrades to silence (see `fallback`)."""


#: Code-fixed backstop, mirroring the other roles' contracts: this
#: survives an operator blanking `system_instruction`.
RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"findings": "<2-6 comma-separated keywords, no sentences>",
   "relevant": true | false}

Report only what the records you were given actually say, compressed to
keywords — no sentences, no reasoning, no punctuation beyond commas. If
nothing in the records bears on this event, answer with relevant: false
and empty findings — that is a useful answer, not a failure. Never
invent a record, and never address the human; you are informing another
agent.
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
    obj = extract_json_object(text)
    if obj is None:
        raise ContractViolation(f"no JSON object in response: {text[:200]!r}")

    findings = obj.get("findings")
    if not isinstance(findings, str):
        findings = "" if findings is None else str(findings)
    findings = findings.strip()[:MAX_FINDINGS_CHARS]

    # `relevant` defaults FALSE when unreadable — silence is the safe
    # value for this family, the same way `proceed: false` is for a
    # gating one.
    relevant = coerce_bool(obj.get("relevant"), default=bool(findings))
    if not findings:
        relevant = False

    return Findings(findings=findings, relevant=relevant, decided_by="llm")


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


__all__ = ["Findings", "ContractViolation", "RESPONSE_CONTRACT",
           "MAX_FINDINGS_CHARS", "DEFAULT_QUERY_LIMIT",
           "build_prompt", "parse", "silent", "fallback"]
