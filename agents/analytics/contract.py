"""
Analytics' output contract — what it is asked, and what happens when the
answer can't be used (§5.4, spec v0.35).

Why this looks different from Governance's contract
---------------------------------------------------
Phase 0.1 gave Governance a routing whitelist: a closed set of legal
answers, so a model could be checked against it exactly. Analytics has no
such set. It is the reasoner, and the whole point of putting a model
there is that the useful answers are not enumerable in advance.

So the contract here constrains the SHAPE of an answer, not which answer
— and what happens when the shape is wrong.

One task, and why it is now only one
------------------------------------
Analytics used to answer three message types, one per lane of the v0.34
verdict dispatch: Evaluate (an ordinary event), Review (Security's yellow
lane) and Revise (Security's red lane). Two of those were safety
judgments, and they are gone.

v0.35e moved Security's non-green lanes to Intent. Daniel's 2026-08-24
confirmation went further than the spec draft had: **Analytics is
isolated from Security in every way**, and the role is "only there to
serve unbiased analytical keywords to intent" — cut back to its bare
minimum for now. So:

  Evaluate   the only task. Reason about the event and say what you make
             of it, in keywords. Nothing is gated on the answer.

Since v0.35a it is also fanned out to DIRECTLY by Sensory rather than
relayed by Governance, so the envelope type it sees is the modality
("prompt", "feedback", "vision", ...). All of them mean Evaluate.

What Analytics contributes, and what it no longer does
--------------------------------------------------------
2026-08-25 (Daniel): `proceed`/`concern` are gone. They read as an
analytical judgment, but Governance's own routing code was still using
them to fork Intent between ADVISE and REFUSE — a real, live gate riding
on Analytics' opinion, not the harmless vestige it looked like. The only
gate in this system now is Security's red verdict. Analytics is "only
there to serve unbiased analytical keywords to intent" (Daniel,
2026-08-24) — as dumb as Personality and Knowledge, full stop. It
contributes a keyword recommendation and nothing else. Loop detection, a
mechanical count rather than an opinion, is the other place its answer
comes from.

Fallback posture
----------------
One task, one posture: DEGRADE. Fall back to the templated
recommendation the Phase 0 mock produced. The pipeline keeps moving with
a duller answer, which is the right trade for a hop where nothing is
gated — and it is byte-identical to the mock's output, so a substrate
outage changes quality, not behaviour.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, Optional

from bus.envelope import Envelope
from substrates.parsing import extract_json_object

#: A recommendation longer than this is the model writing an essay rather
#: than a keyword read. Truncated rather than rejected — the content is
#: probably fine, there is just too much of it, and Intent reads it next.
#: 2026-08-25 (Daniel): tightened from 2000 — a "keyword or short phrase"
#: has no business running anywhere near the old ceiling, and Intent was
#: getting misled by essay-length "recommendations" reading as loaded
#: context rather than a terse read.
MAX_RECOMMENDATION_CHARS = 80


class Task(str, Enum):
    EVALUATE = "Evaluate"

    @classmethod
    def from_envelope(cls, envelope: Envelope) -> Optional["Task"]:
        """Since v0.35a Sensory fans out to Analytics directly, so the
        type on the envelope is the modality rather than a task name.
        Every modality means the same thing here: look at this event."""
        raw = str(envelope.type).strip()
        if raw in SENSORY_TYPES:
            return cls.EVALUATE
        try:
            return cls(raw)
        except ValueError:
            return None


#: See Task.from_envelope. Kept module-level rather than on the enum
#: because Enum class bodies turn plain attributes into members.
SENSORY_TYPES = frozenset({"prompt", "feedback", "vision", "audio", "https"})


#: What the model is asked to do. One entry, because there is one task.
TASK_BRIEFS: Dict[Task, str] = {
    Task.EVALUATE: (
        "Reason about this event: produce 2-6 keywords AND pick which "
        "knowledge paths to query. Both fields are mandatory."
    ),
}


@dataclass(frozen=True)
class Recommendation:
    """A validated Analytics answer, ready to hand to Intent."""

    recommendation: str
    knowledge_paths: list = field(default_factory=list)
    decided_by: str = "llm"          # llm | fallback | deterministic
    diagnostics: Dict[str, Any] = field(default_factory=dict)

    def to_meta(self) -> Dict[str, Any]:
        meta: Dict[str, Any] = {"decided_by": self.decided_by}
        if self.knowledge_paths:
            meta["knowledge_paths"] = self.knowledge_paths
        if self.diagnostics:
            meta.update(self.diagnostics)
        return meta


class ContractViolation(ValueError):
    """The substrate's answer cannot be used. Always recoverable: the
    caller degrades per the task's fallback posture."""


# ---------------------------------------------------------------------------
# Prompting
# ---------------------------------------------------------------------------

#: Deliberately terse, and deliberately code-fixed rather than left to the
#: manifest's system_instruction: this is the one place two things are
#: guaranteed to survive an operator blanking out or rewriting
#: system_instruction entirely (AnalyticsAgent falls back to
#: DEFAULT_SYSTEM_INSTRUCTION, but a custom one could say anything).
#: Everything else that used to live here (a persona/wording explainer,
#: a "be concise" reminder, a length rule) was pure restatement of what
#: system_instruction or TASK_BRIEFS already say once — cut, not moved.
RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"recommendation": "<2-6 keywords>",
   "knowledge_paths": [{"category": "person", "topic": "family"}, ...]}

MANDATORY FIELDS (both required in every response):
- recommendation: 2-6 comma-separated keywords describing the event.
- knowledge_paths: pick 1-5 paths from AVAILABLE PATHS. Each entry must
  have EXACTLY the "category" and "topic" values shown in the list — do
  NOT combine them or rephrase them. Copy them verbatim.
"""


def build_prompt(envelope: Envelope, task: Task, *,
                 recent_events: Optional[list] = None,
                 prior_knowledge: Optional[list] = None,
                 schema_index: Optional[list] = None) -> str:
    """One task, one event, and whatever bounded context we have.

    Kept deliberately small. §5.4 gives Analytics a rolling 10-event
    working window and Archive access; both are included here as short,
    bounded lists rather than as a growing transcript, because the flat
    cost claim (§1) depends on the live prompt not growing with history."""
    lines = [
        f"TASK: {task.value}",
        TASK_BRIEFS[task],
        "",
        "EVENT:",
        f"  from:     {envelope.source}",
        f"  severity: {envelope.severity}",
        f"  content:  {envelope.content}",
    ]

    reflex = envelope.meta.get("reflex")
    if reflex:
        lines.append(f"  gut reaction (Impulse): {reflex}")
    proposed = envelope.meta.get("proposed_action")
    if proposed:
        lines.append(f"  action under consideration: {proposed}")
    vectors = envelope.meta.get("drive_vectors")
    if vectors:
        lines.append(f"  drive vectors: {vectors}")

    if recent_events:
        lines.append("")
        lines.append("RECENT EVENTS (working memory, oldest first):")
        lines.extend(f"  - {str(e)[:200]}" for e in recent_events)

    if prior_knowledge:
        lines.append("")
        lines.append("PRIOR KNOWLEDGE (from Archive):")
        lines.extend(f"  - {str(k)[:200]}" for k in prior_knowledge)

    if schema_index:
        lines.append("")
        lines.append("AVAILABLE PATHS (pick from this list for knowledge_paths):")
        lines.append("  Format: category = left of slash, topic = right of slash")
        for entry in schema_index:
            lines.append(f"  - category: \"{entry['category']}\" / topic: \"{entry['topic']}\"")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def parse(text: str, task: Task) -> Recommendation:
    """Turn a substrate response into a Recommendation, or raise."""
    obj = extract_json_object(text)
    if obj is None:
        raise ContractViolation(f"no JSON object in response: {text[:200]!r}")

    recommendation = obj.get("recommendation") or obj.get("advice")
    if not isinstance(recommendation, str) or not recommendation.strip():
        raise ContractViolation(f"no usable 'recommendation' in response: {obj!r}")
    recommendation = recommendation.strip()[:MAX_RECOMMENDATION_CHARS]

    knowledge_paths = []
    raw_paths = obj.get("knowledge_paths")
    if isinstance(raw_paths, list):
        for p in raw_paths[:5]:
            if isinstance(p, dict) and p.get("category") and p.get("topic"):
                knowledge_paths.append({
                    "category": str(p["category"]).strip(),
                    "topic": str(p["topic"]).strip(),
                })

    return Recommendation(recommendation=recommendation, knowledge_paths=knowledge_paths, decided_by="llm")


# ---------------------------------------------------------------------------
# Deterministic fallbacks
# ---------------------------------------------------------------------------

def templated_recommendation(envelope: Envelope) -> str:
    """The Phase 0 mock's output, byte for byte.

    Shared so the mock tier and a degraded live tier produce the same
    envelope — the same discipline Governance's templates follow. A
    substrate outage should change the quality of the thinking, not the
    shape of the trace."""
    return f"All agents awake. {envelope.content}"


def fallback(envelope: Envelope, task: Task, reason: str) -> Recommendation:
    """What Analytics says when the substrate's answer can't be used.

    Degrade and keep moving. Nothing is gated on this hop, so a duller
    answer is the right trade — and it is byte-identical to the mock's,
    so an outage changes the quality of the thinking, not the shape of
    the trace."""
    return Recommendation(
        recommendation=templated_recommendation(envelope),
        decided_by="fallback",
        diagnostics={"degraded": True, "reason": reason[:200]},
    )


def loop_detected(envelope: Envelope, repeats: int) -> Recommendation:
    """§5.4's loop check. A mechanical condition gets a mechanical answer
    and costs nothing — the Phase 0.1 lesson, applied here: an agent
    should not pay for inference to notice it has seen the same thing
    three times."""
    return Recommendation(
        recommendation="loop detected, graceful degradation",
        decided_by="deterministic",
        diagnostics={"loop_detected": True, "repeats": repeats},
    )
