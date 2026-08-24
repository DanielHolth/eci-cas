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

What Analytics still contributes, and what it no longer does
-------------------------------------------------------------
It still sets `proceed` and `concern`. That is an ANALYTICAL judgment
("I don't think this is a good idea, and here's the one-line reason"),
not a security one, and Intent reads it out of the bundle to choose
between its ADVISE and REFUSE registers exactly as before — v0.35c is
explicit that this half of the contract is unchanged. Loop detection, a
mechanical count rather than an opinion, is the other place it comes
from.

What it no longer does is decide whether an action happens. Nothing
downstream treats `proceed: false` as a veto: Intent voices the decline,
and Intent, Security and Governance hold the actual gates. That is why
there is no fail-closed asymmetry left in this file.

Fallback posture
----------------
One task, one posture: DEGRADE. Fall back to the templated
recommendation the Phase 0 mock produced. The pipeline keeps moving with
a duller answer, which is the right trade for a hop where nothing is
gated — and it is byte-identical to the mock's output, so a substrate
outage changes quality, not behaviour.

The fail-closed discipline this file used to carry hasn't been lost; it
moved to where the gating went. See agents/intent/contract.py's
REVIEW/REVISE fallbacks.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, Optional

from bus.envelope import Envelope
from substrates.parsing import coerce_bool, extract_json_object

#: A recommendation longer than this is the model writing an essay rather
#: than advising. Truncated rather than rejected — the content is
#: probably fine, there is just too much of it, and Intent reads it next.
MAX_RECOMMENDATION_CHARS = 2000

#: Concerns are shown to the human via Intent's refusal. Keep them short.
MAX_CONCERN_CHARS = 300


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
        "Reason about this event and say what you make of it, in keywords. "
        "Set proceed to false only if the request itself looks like a bad "
        "idea on the merits, and give the reason in one plain sentence — "
        "someone else decides what happens about it."
    ),
}


@dataclass(frozen=True)
class Recommendation:
    """A validated Analytics answer, ready to hand to Intent."""

    recommendation: str
    proceed: bool = True
    concern: str = ""
    decided_by: str = "llm"          # llm | fallback | deterministic
    diagnostics: Dict[str, Any] = field(default_factory=dict)

    def to_meta(self) -> Dict[str, Any]:
        meta: Dict[str, Any] = {"proceed": self.proceed,
                                "decided_by": self.decided_by}
        if self.concern:
            meta["concern"] = self.concern
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

  {"recommendation": "<keywords/short phrase, not a full sentence>",
   "proceed": true | false,
   "concern": "<one short sentence, only when proceed is false>"}

Never write the persona's reply. When unsure, proceed: false.
"""


def build_prompt(envelope: Envelope, task: Task, *,
                 recent_events: Optional[list] = None,
                 prior_knowledge: Optional[list] = None) -> str:
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

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def parse(text: str, task: Task) -> Recommendation:
    """Turn a substrate response into a Recommendation, or raise.

    Note the default passed to coerce_bool: the SAFE value for this task.
    A model that answers with something unreadable in the `proceed` field
    does not get the benefit of the doubt on a gating task."""
    obj = extract_json_object(text)
    if obj is None:
        raise ContractViolation(f"no JSON object in response: {text[:200]!r}")

    recommendation = obj.get("recommendation") or obj.get("advice")
    if not isinstance(recommendation, str) or not recommendation.strip():
        raise ContractViolation(f"no usable 'recommendation' in response: {obj!r}")
    recommendation = recommendation.strip()[:MAX_RECOMMENDATION_CHARS]

    # Nothing downstream is gated on this value any more (v0.35e), so an
    # unreadable one defaults to True: a model that answers the wrong
    # question shouldn't be able to make the persona decline. Where it
    # answers the question clearly, Intent hears it.
    proceed = coerce_bool(obj.get("proceed"), default=True)

    concern = obj.get("concern") or ""
    if not isinstance(concern, str):
        concern = str(concern)
    concern = concern.strip()[:MAX_CONCERN_CHARS]

    if not proceed and not concern:
        # A refusal with no reason gives Intent nothing to say and the
        # human nothing to act on.
        concern = "Analytics advised against this but did not give a reason."

    return Recommendation(recommendation=recommendation, proceed=proceed,
                          concern=concern, decided_by="llm")


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

    One posture now: degrade and keep moving. Nothing is gated on this
    hop, so a duller answer is the right trade — and it is byte-identical
    to the mock's, so an outage changes the quality of the thinking, not
    the shape of the trace.

    The fail-closed half of this function moved to Intent with the gating
    (v0.35e). See agents/intent/contract.py's fallback_gated."""
    return Recommendation(
        recommendation=templated_recommendation(envelope),
        proceed=True,
        decided_by="fallback",
        diagnostics={"degraded": True, "reason": reason[:200]},
    )


def loop_detected(envelope: Envelope, repeats: int) -> Recommendation:
    """§5.4's loop check. A mechanical condition gets a mechanical answer
    and costs nothing — the Phase 0.1 lesson, applied here: an agent
    should not pay for inference to notice it has seen the same thing
    three times."""
    return Recommendation(
        recommendation=("Loop detected — recommend graceful degradation, "
                        "not repetition."),
        proceed=False,
        concern=f"This is the {repeats}th identical attempt; repeating it will not help.",
        decided_by="deterministic",
        diagnostics={"loop_detected": True, "repeats": repeats},
    )
