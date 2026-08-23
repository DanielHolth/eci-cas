"""
Analytics' output contract — what it is asked, and what happens when the
answer can't be used (§5.4, spec v0.34).

Why this looks different from Governance's contract
---------------------------------------------------
Phase 0.1 gave Governance a routing whitelist: a closed set of legal
answers, so a model could be checked against it exactly. Analytics has no
such set. It is the reasoner — "the heavy lifter" (§2.1) — and the whole
point of putting a model there is that the useful answers are not
enumerable in advance.

So the contract here constrains a different thing. Not *which* answer, but
its SHAPE, and — the part that matters — what each task falls back to when
the shape is wrong. Governance's fallback was always "the route you were
going to take anyway." Analytics has no such luxury: there is no template
that reasons. The fallbacks are therefore chosen per task, on the
principle that the cost of a wrong answer is not symmetric.

The three tasks
---------------
Analytics receives exactly three message types from Governance, one per
lane of the v0.34 verdict dispatch:

  Evaluate   an ordinary event relayed by Impulse. Reason about it and
             recommend a response. Nothing has gone wrong; nothing is
             being gated.
  Review     the YELLOW lane. Security could neither clear nor block this
             by rule, so Analytics decides whether it proceeds. This is a
             safety-adjacent judgment.
  Revise     the RED lane. Security blocked the proposal; propose a
             different course.

Fallback posture, and why it differs per task
---------------------------------------------
  Evaluate -> DEGRADE. Fall back to the templated recommendation the
              Phase 0 mock produced. The pipeline keeps moving with a
              duller answer, which is the right trade for an event where
              nothing is being gated. Identical to the mock's output, so
              a substrate outage changes quality, not behaviour.

  Review   -> DECLINE. Fail toward not acting. Analytics was asked
              precisely because nobody could confirm this was safe; an
              unparseable answer is not confirmation. Treating "I don't
              know" as "go ahead" is how the pre-v0.34 fail-open bug
              worked, one hop further up.

  Revise   -> DECLINE. Security already blocked this once. Falling back
              to "here, try this instead" without having reasoned about
              it would launder a block into an unreviewed retry, and
              could loop.

The asymmetry is the design. Two of three tasks fail closed, and the one
that fails open is the one where nothing was being gated in the first
place.
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
    REVIEW = "Review"
    REVISE = "Revise"

    @classmethod
    def from_envelope(cls, envelope: Envelope) -> Optional["Task"]:
        try:
            return cls(str(envelope.type).strip())
        except ValueError:
            return None


#: Per-task: what the model is asked to do, and whether `proceed` is
#: consulted at all. `proceed` is part of the schema for every task so
#: validation stays uniform, but on Evaluate nothing is being gated, so
#: the field is ignored rather than obeyed — a model cannot halt an
#: ordinary event by answering the wrong question.
TASK_BRIEFS: Dict[Task, str] = {
    Task.EVALUATE: (
        "Reason about this event and recommend how to respond to it."
        # No "set proceed to true" here on purpose: parse() force-sets it
        # for every non-gating task regardless of what the model answers
        # (see GATING_TASKS below), so telling the model to set it would
        # be an instruction that changes nothing — pure token cost.
    ),
    Task.REVIEW: (
        "The safety rules could neither clear nor block this action, so "
        "the decision is yours. Decide whether it should go ahead. Set "
        "proceed to false if you are not confident it is appropriate, and "
        "say why in concern - the human will be told. If you are unsure, "
        "decline: nothing was confirmed safe here."
    ),
    Task.REVISE: (
        "The safety rules blocked the previous proposal. Recommend a "
        "different course that addresses the objection. If no acceptable "
        "alternative exists, set proceed to false and say why in concern "
        "rather than proposing something that would be blocked again."
    ),
}

#: Tasks where `proceed: false` actually stops the action.
GATING_TASKS = frozenset({Task.REVIEW, Task.REVISE})

#: Tasks that fail toward not acting when the answer can't be used.
FAIL_CLOSED_TASKS = GATING_TASKS


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

    safe_default = task not in FAIL_CLOSED_TASKS
    proceed = coerce_bool(obj.get("proceed"), default=safe_default)
    if task not in GATING_TASKS:
        # Nothing is being gated on an Evaluate; a model cannot halt an
        # ordinary event by answering the wrong question.
        proceed = True

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

    Evaluate degrades and keeps moving. Review and Revise decline — see
    the module docstring for why the asymmetry is deliberate."""
    if task in FAIL_CLOSED_TASKS:
        return Recommendation(
            recommendation=(
                "Could not complete the requested judgment. Recommending "
                "against proceeding until this can be reviewed."),
            proceed=False,
            concern="I could not properly weigh this one, so I would rather not act on it.",
            decided_by="fallback",
            diagnostics={"degraded": True, "reason": reason[:200]},
        )
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
