"""Reflection's output contract (dispatch #4, 2026-08-29).

One reflection pass looks back over N concluded events and produces
exactly one of three outcomes — a new domain="internal" archive write, an
Idea ping back into Sensory, or silence. Never more than one: a pass that
both wrote and pinged would be reasoning about its own reasoning in the
same breath it hadn't finished having, which is a worse trade than
picking the single strongest thread.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

OUTCOMES = ("write", "idea", "silent")

RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"outcome": "write" | "idea" | "silent",
   "category": "<broad domain, only for outcome=write>",
   "topic": "<grouping within domain, only for outcome=write>",
   "subtopic": "<relation, role, or type, only for outcome=write>",
   "subject": "<entity name, or a short description, only for outcome=write>",
   "key": "<attribute name, only for outcome=write>",
   "value": "<the insight itself, only for outcome=write>",
   "idea": "<one sentence worth raising, only for outcome=idea>"}

RULES:
1. You are looking for a PATTERN across the incidents below, not a
   restatement of any one of them — "the user asked about X" is not an
   insight, "I tend to escalate low-impact events when Y is present" is.
2. outcome="write" only when you found something durable enough to guide
   FUTURE behavior and it is not already sitting in PRIOR LEARNINGS below
   (near-duplicate of an existing entry: skip it, that is outcome="silent").
3. outcome="idea" only when the pattern is worth surfacing to the human
   directly — something to ask or offer, not just remember. This becomes
   a real event fed back through the normal pipeline, so use it sparingly:
   one genuinely interesting thread beats a comment on every batch.
4. outcome="silent" is the default. Most batches of ordinary conversation
   contain no new pattern — that is normal, not a failure to try harder.
5. value/idea is the insight in your own words — never a copy of one
   incident's raw content.
"""


@dataclass
class ReflectionResult:
    """One reflection pass's outcome, after structural validation."""

    outcome: str = "silent"
    write: Optional[Dict[str, Any]] = None
    idea: str = ""
    decided_by: str = "deterministic"
    diagnostics: Dict[str, Any] = field(default_factory=dict)


def build_prompt(pending: List[Dict[str, Any]], prior_learnings: List[Dict[str, Any]]) -> str:
    """The incident summary (what happened) plus prior internal learnings
    (what Reflection already concluded), so it isn't re-deriving the same
    pattern every batch."""
    lines = ["INCIDENTS, oldest first:"]
    for i, rec in enumerate(pending, 1):
        lines.append(f"  {i}. input: {str(rec.get('sensory', ''))[:200]}")
        said = rec.get("final_proposal") or rec.get("reflex_action") or ""
        if said:
            lines.append(f"     said: {str(said)[:200]}")
        lines.append(f"     verdict: {rec.get('verdict', 'green')}, "
                     f"severity: {rec.get('severity', 'Neutral')}")

    lines.append("")
    lines.append("PRIOR LEARNINGS (your own past reflections):")
    if prior_learnings:
        for r in prior_learnings:
            path = "/".join(p for p in (r.get("category", ""), r.get("topic", ""),
                                        r.get("subtopic", ""), r.get("subject", "")) if p)
            lines.append(f"  {path}: {r.get('key', '')} = {r.get('value', '')}")
    else:
        lines.append("  (none yet)")

    return "\n".join(lines)


def parse(text_obj: Any) -> ReflectionResult:
    """Structural validation only — clamp-at-the-boundary, same posture as
    every other response contract in this codebase. `text_obj` is already
    the extracted JSON object (see substrates.parsing.extract_json_object);
    this function owns turning it into a well-formed ReflectionResult."""
    if not isinstance(text_obj, dict):
        return ReflectionResult(outcome="silent",
                                diagnostics={"dropped_reason": "not a JSON object"})

    outcome = str(text_obj.get("outcome") or "").strip().lower()
    if outcome not in OUTCOMES:
        return ReflectionResult(outcome="silent",
                                diagnostics={"dropped_reason": f"unknown outcome {outcome!r}"})

    if outcome == "write":
        category = str(text_obj.get("category") or "").strip()
        topic = str(text_obj.get("topic") or "").strip()
        key = str(text_obj.get("key") or "").strip()
        value = str(text_obj.get("value") or "").strip()
        if not category or not topic or not key or not value:
            return ReflectionResult(outcome="silent",
                                    diagnostics={"dropped_reason": "incomplete write"})
        return ReflectionResult(outcome="write", write={
            "category": category[:80],
            "topic": topic[:80],
            "subtopic": str(text_obj.get("subtopic") or "general")[:80],
            "subject": str(text_obj.get("subject") or "").strip()[:80] or "this",
            "key": key[:120],
            "value": value[:1000],
        })

    if outcome == "idea":
        idea = str(text_obj.get("idea") or "").strip()
        if not idea:
            return ReflectionResult(outcome="silent",
                                    diagnostics={"dropped_reason": "empty idea"})
        return ReflectionResult(outcome="idea", idea=idea[:500])

    return ReflectionResult(outcome="silent")


__all__ = ["OUTCOMES", "RESPONSE_CONTRACT", "ReflectionResult", "build_prompt", "parse"]
