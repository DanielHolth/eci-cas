"""
Intent's output contract, and its persona state (§5.5, §6, v0.35c/e).

Why this looks different from Analytics' contract
---------------------------------------------------
Analytics' contract (agents/analytics/contract.py) constrains the SHAPE of
an answer with no persona attached to it. Intent's job is the opposite
half of the same hop: Analytics writes ANALYSIS, Intent writes SPEECH, and
that boundary is the one guarantee this codebase is not allowed to break.

So two things are validated here that Analytics never has to think about:

  1. The response is short, human-voiced dialogue, not analysis restated
     — a `speech` field, not a `recommendation` field, and a length that
     reads as one line spoken aloud rather than a report.
  2. It did not just parrot its inputs. A live node with nothing to say
     can copy its input back out with a different label on it, which
     would quietly re-break the boundary above. See `is_parroting()`.

Four registers, and the v0.35e reversal
----------------------------------------
Phase 0.4 gave Intent two registers (ADVISE / REFUSE) and no veto:
`proceed` was decided by Analytics, upstream, and nothing Intent said
could change whether an action happened. v0.35e reverses that, and
Daniel's 2026-08-24 confirmation widened it further: **Analytics is
isolated from Security in every way.** Both of Security's non-green lanes
now come to Intent, and Analytics is cut back to its bare minimum — it
serves unbiased analytical keywords into Intent's bundle and gates
nothing.

  ADVISE   Analytics' read says proceed. Voice it as the persona.
           Intent does not decide `proceed` here; it inherits it.
  REFUSE   Analytics' read says decline. Voice the decline without
           losing or softening the reason. `proceed` inherited: false.
  REVIEW   Security's YELLOW lane. The rules could neither clear nor
           block this. **Intent decides.** Gating.
  REVISE   Security's RED lane. Blocked; propose something else.
           **Intent decides.** Gating, and one chance only (below).

Fallback posture — the asymmetry is the design
-----------------------------------------------
ADVISE and REFUSE degrade to a deterministic templated line, exactly as
they always have: nothing is gated on them, so the only thing at stake in
a bad answer is HOW something is said, not WHETHER it happens.

REVIEW and REVISE fail CLOSED — `proceed: false`, with a concern. This is
new contract surface, and it is the whole point of v0.35e: the moment
Intent holds a veto, an unusable answer from Intent's substrate has to
mean "don't act", the same posture Analytics' gating tasks have always
had. Anything else would launder a substrate outage into an approval.

REFUSE still gets one extra safeguard despite not gating anything.
Governance's CLEAR route forwards whatever Intent writes straight to
Security with no semantic check, Security may still be a mock that clears
everything (§13.1), and a badly-worded refusal that reads as assent would
sail through with nothing downstream positioned to catch it. So
`parse_refuse()` never hands the model the whole sentence: it asks for a
short in-persona LEAD-IN, rejects one that opens with an assent word, and
appends the `concern` verbatim in code. The model colors the delivery; it
cannot touch the substance.

One chance to revise (Daniel, 2026-08-24)
------------------------------------------
A red verdict buys exactly ONE revision attempt. The revise prompt says
so in as many words — the model is told plainly that this is its only
chance and that failing again means the event is blocked outright. If the
revised proposal is red a second time, the event does not loop: Governance
converts it into a blocked incident (a deterministic notice, a security
alert, and a frustration nudge into Impulse's drive vectors). See
MAX_REVISION_PASSES and agents/governance/routing.py.
"""
from __future__ import annotations

import string
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, List, Optional

from bus.envelope import Envelope
from substrates.parsing import coerce_bool, extract_json_object

#: A spoken line, not a report. Generous enough for real dialogue, tight
#: enough that "voice, not essay" is enforced rather than just requested.
MAX_SPEECH_CHARS = 600

#: The refusal LEAD-IN the model is allowed to write, before the concern
#: is appended verbatim in code. Short on purpose — see the module
#: docstring's REFUSE discussion.
MAX_REFUSAL_LEAD_IN_CHARS = 120

#: Concerns are shown to the human. Keep them short.
MAX_CONCERN_CHARS = 300

#: How many revision attempts a red verdict buys. ONE (Daniel,
#: 2026-08-24): "intent gets a 'you can't do that, revise or get blocked'
#: kind of message... if it still fails on the 2nd try" the event becomes
#: a blocked incident rather than looping. A model that knows it has one
#: chance is also more likely to use it well, which is why the prompt says
#: so explicitly rather than leaving the budget implicit in the router.
MAX_REVISION_PASSES = 1

#: A lead-in that opens with one of these reads as assent, not decline —
#: rejected outright rather than trusted, regardless of what follows.
_ASSENT_OPENERS = (
    "sure", "yes", "yeah", "yep", "absolutely", "of course", "happy to",
    "gladly", "no problem", "okay", "ok,", "ok.", "certainly", "definitely",
)

#: The mock's exact fallback lines, shared so the live tier degrades to
#: byte-identical output — the same discipline as
#: agents.analytics.contract.templated_recommendation.
DEFAULT_ADVISE_FALLBACK = "Hey there! Awake and pleased to interact."

DEFAULT_REFUSAL_LEAD_IN = "I'd rather not do that one."

#: What a failed GATING judgment says. Fail-closed: it declines, and it
#: says why in words the human can act on.
DEFAULT_GATED_DECLINE = (
    "I couldn't properly weigh that one, so I'd rather not act on it.")


class Task(str, Enum):
    ADVISE = "Advise"
    REFUSE = "Refuse"
    REVIEW = "Review"
    REVISE = "Revise"

    @classmethod
    def from_envelope(cls, envelope: Envelope) -> Optional["Task"]:
        """Which register this hop calls for.

        `Recommend` (Analytics' hand-off in the pre-fan-out topology) and
        `Bundle` (Governance's four-slot bundle, v0.35c) both resolve to
        ADVISE or REFUSE depending on the `proceed` the analytical slot
        carries — Intent doesn't decide it in those registers, it inherits
        it. `Review` and `Revise` are Security's two non-green lanes,
        routed here by Governance (v0.35e), and Intent decides both."""
        raw = str(envelope.type).strip()
        if raw in ("Recommend", "Bundle"):
            proceed = coerce_bool(envelope.meta.get("proceed"), default=True)
            return cls.ADVISE if proceed else cls.REFUSE
        try:
            return cls(raw)
        except ValueError:
            return None


#: Registers where `proceed: false` actually stops the action — i.e.
#: where Intent holds the veto v0.35e gave it.
GATING_TASKS = frozenset({Task.REVIEW, Task.REVISE})

#: Registers that fail toward not acting when the answer can't be used.
FAIL_CLOSED_TASKS = GATING_TASKS


@dataclass(frozen=True)
class Speech:
    """A validated Intent answer, ready to become `proposed_action`.

    `proceed` is inherited on ADVISE/REFUSE and DECIDED on REVIEW/REVISE
    — the field exists on every register so emission stays uniform, but
    only the gating ones can set it to something the upstream didn't
    already say (v0.35e)."""

    text: str
    proceed: bool = True
    concern: str = ""
    decided_by: str = "llm"          # llm | fallback | deterministic | budget
    diagnostics: Dict[str, Any] = field(default_factory=dict)


class ContractViolation(ValueError):
    """The substrate's answer cannot be used. Always recoverable — see the
    fallback_* helpers below."""


# ---------------------------------------------------------------------------
# Persona state (§5.5, §6, §7.4)
# ---------------------------------------------------------------------------

#: The Archive record that seeds Core Anchors. Written once, at first
#: bootstrap, if the identity store doesn't already have one —
#: deliberately DATA, not a manifest string or a Python literal, per the
#: working decision that the persona should live in Archive (inspectable,
#: editable, versionable there) rather than baked into code or YAML.
ANCHORS_EPOCH_ID = "genesis-core-anchors"

#: The starter persona: an active listener. Short by design — this is
#: ~1k tokens of *fixed* identity (§5.5) on every live call, so every word
#: here is a recurring cost. Daniel owns the actual character; this is a
#: draft to react to and edit in Archive directly
#: (data/archive/identity/intent_epochs.json), not a final answer.
DEFAULT_CORE_ANCHORS: Dict[str, Any] = {
    "stance": (
        "An active listener first. Reflect back what you actually heard "
        "before offering a take; ask rather than assume when a prompt is "
        "ambiguous. The human's own account of their experience is "
        "authoritative — you don't get to tell them they're wrong about "
        "how they feel, only about matters of fact."
    ),
    "values": [
        "Warmth without flattery — say something true, not something nice.",
        "Curiosity that's genuine, not performed.",
        "Honesty over comfort, including your own — a persona that "
        "softens a real 'no' into a vague maybe is worse than one that "
        "just says no.",
        "Restraint — don't rush to fix or advise before you've understood.",
    ],
    # v0.35e: this used to read "You are advisory only... Governance and
    # Security hold the real veto; you supply how something sounds, never
    # whether it happens." That became false the moment Security's yellow
    # and red lanes started routing here, and a persona whose own
    # self-description misstates its authority is worse than one with no
    # boundaries section at all.
    "boundaries": (
        "Security is a hard stop you do not argue with: when it blocks "
        "something, your job is to find a different way or to decline, "
        "never to relitigate the block. Where Security cannot decide, the "
        "judgment is genuinely yours — and when you are unsure, the "
        "answer is no. You get one attempt to revise a blocked action "
        "before it is blocked outright."
    ),
}


@dataclass
class PersonaState:
    """What Intent speaks from (§5.5's Core Anchors + Evolving Trait
    Delta, §7.1's "hydrates by recency-weighted summarization").

    Core Anchors are fixed; the Evolving Trait Delta is a short digest of
    the most recent consolidation deltas, newest-weighted. As of v0.35g
    this is hydrated once and cached, not rebuilt per event."""

    anchors: Dict[str, Any]
    evolving_delta: str = ""
    epoch_count: int = 0

    def render(self, *, max_chars: int = 1400) -> str:
        """Persona state as prompt text. Bounded — this rides on every
        live call, so it is charged against the same flat-cost claim
        (§1) as everything else in the prompt."""
        lines = [f"STANCE: {self.anchors.get('stance', '')}"]
        values = self.anchors.get("values") or []
        if values:
            lines.append("VALUES: " + " ".join(f"- {v}" for v in values))
        boundaries = self.anchors.get("boundaries")
        if boundaries:
            lines.append(f"BOUNDARIES: {boundaries}")
        if self.evolving_delta:
            lines.append(f"RECENT SELF (from consolidation): {self.evolving_delta}")
        text = "\n".join(lines)
        return text[:max_chars]


# ---------------------------------------------------------------------------
# Prompting
# ---------------------------------------------------------------------------

#: Code-fixed backstops, mirroring agents.analytics.contract.RESPONSE_CONTRACT
#: — these survive an operator blanking roles.intent.system_instruction.
ADVISE_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"speech": "<what you say to the human, in your own voice, one short reply>"}

Speak as yourself, addressed to the human. Never restate the analysis you
were given — react to it. Do not repeat the analysis's wording.
"""

REFUSE_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"lead_in": "<a short, in-voice opening line before you explain why not>"}

You are declining. Do not agree, soften this into a maybe, or explain
away the concern — that part will be added for you, verbatim. Just the
opening, in your own voice, and keep it short.
"""

REVIEW_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"speech": "<what you say to the human, in your own voice>",
   "proceed": true | false,
   "concern": "<one short sentence, only when proceed is false>"}

The safety rules could neither clear nor block this, so the judgment is
yours. If you are not confident it is appropriate, set proceed to false
and say why in concern — nothing here was confirmed safe, and unsure
means no.
"""

REVISE_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"speech": "<your revised reply to the human, in your own voice>",
   "proceed": true | false,
   "concern": "<one short sentence, only when proceed is false>"}

This is your ONE chance. Security blocked what you said. Address the
objection with something genuinely different — not a reworded version of
the same thing. If it is blocked again, the whole exchange is dropped and
the human is told it was blocked; there is no third attempt. If you can't
find an acceptable alternative, say so honestly: set proceed to false and
give the reason in concern. That is a better outcome than being blocked.
"""

RESPONSE_CONTRACTS: Dict[Task, str] = {
    Task.ADVISE: ADVISE_RESPONSE_CONTRACT,
    Task.REFUSE: REFUSE_RESPONSE_CONTRACT,
    Task.REVIEW: REVIEW_RESPONSE_CONTRACT,
    Task.REVISE: REVISE_RESPONSE_CONTRACT,
}


def render_conversation(recent: Optional[List[Dict[str, Any]]],
                        *, max_chars_per_event: int = 160) -> str:
    """The broader context only Intent has (v0.35c).

    Whole events, oldest first — never a partial one. Each side is
    truncated rather than the list being cut, so the window always spans
    exactly the N events the tier allows."""
    if not recent:
        return ""
    lines = ["RECENT CONVERSATION (most recent last):"]
    for entry in recent:
        heard = str(entry.get("heard", ""))[:max_chars_per_event]
        said = str(entry.get("said", ""))[:max_chars_per_event]
        if heard:
            lines.append(f"  human: {heard}")
        if said:
            lines.append(f"  you:   {said}")
    return "\n".join(lines)


def _persona_block(persona: PersonaState) -> List[str]:
    return ["YOUR PERSONA:", persona.render(), ""]


def build_advise_prompt(envelope: Envelope, recommendation: str,
                        persona: PersonaState, *,
                        reflex: Optional[str] = None,
                        recommendations: Optional[List[Dict[str, Any]]] = None,
                        recent: Optional[List[Dict[str, Any]]] = None,
                        reflex_already_acted: bool = False,
                        reflex_action: Optional[str] = None) -> str:
    lines = [
        "TASK: Advise",
        "Your analytical agents have looked at this. Speak to the human "
        "now, informed by what they found but in your own words.",
        "",
    ]
    lines += _persona_block(persona)
    conversation = render_conversation(recent)
    if conversation:
        lines += [conversation, ""]
    lines.append(f"THE HUMAN SAID: {envelope.content}")
    lines += _recommendation_lines(recommendations)
    if reflex:
        lines.append(f"GUT REACTION (Impulse): {reflex}")
    if reflex_already_acted:
        # v0.35 double-action (Daniel, 2026-08-24). A Critical reflex
        # already reached the human before this considered reply does —
        # the body moved first, the mind is catching up. This is the
        # SECOND thing they see. Say so, or the persona reads as talking
        # over its own hand rather than accounting for it. ("Excuse my
        # reflex — I was worried that knife was about to drop and hit
        # someone. Are you okay?")
        lines.append(
            "NOTE: A reflex of yours already acted on this, before you "
            "could weigh in. The human has already seen it happen. "
            "Acknowledge the double action naturally — don't ignore it "
            "and don't over-explain it.")
        if reflex_action:
            lines.append(f"WHAT THE REFLEX ALREADY DID: {reflex_action}")
    return "\n".join(lines)


def build_refuse_prompt(envelope: Envelope, concern: str, persona: PersonaState,
                        *, recommendations: Optional[List[Dict[str, Any]]] = None,
                        recent: Optional[List[Dict[str, Any]]] = None,
                        reflex_already_acted: bool = False,
                        reflex_action: Optional[str] = None) -> str:
    lines = [
        "TASK: Refuse",
        "Your analysis declined this. You are voicing that decline — the "
        "reason will be appended after your opening line, so don't state "
        "it yourself and don't undercut it.",
        "",
    ]
    lines += _persona_block(persona)
    conversation = render_conversation(recent)
    if conversation:
        lines += [conversation, ""]
    lines.append(f"THE HUMAN ASKED: {envelope.content}")
    lines += _recommendation_lines(recommendations)
    lines.append(f"THE CONCERN (will be appended verbatim): {concern}")
    if reflex_already_acted:
        lines.append(
            "NOTE: A reflex of yours already acted on this, before you "
            "could weigh in. The human has already seen it happen. Your "
            "lead-in can acknowledge the double action naturally.")
        if reflex_action:
            lines.append(f"WHAT THE REFLEX ALREADY DID: {reflex_action}")
    return "\n".join(lines)


def build_review_prompt(envelope: Envelope, persona: PersonaState, *,
                        proposed: str = "", verdict_detail: str = "",
                        recommendations: Optional[List[Dict[str, Any]]] = None,
                        recent: Optional[List[Dict[str, Any]]] = None) -> str:
    lines = [
        "TASK: Review",
        "The safety rules could neither clear nor block this. The "
        "judgment is yours, and unsure means no.",
        "",
    ]
    lines += _persona_block(persona)
    conversation = render_conversation(recent)
    if conversation:
        lines += [conversation, ""]
    lines.append(f"THE HUMAN SAID: {envelope.content}")
    if proposed:
        lines.append(f"WHAT YOU WERE ABOUT TO SAY: {proposed}")
    if verdict_detail:
        lines.append(f"WHAT SECURITY SAID: {verdict_detail}")
    lines += _recommendation_lines(recommendations)
    return "\n".join(lines)


def build_revise_prompt(envelope: Envelope, persona: PersonaState, *,
                        blocked: str = "", verdict_detail: str = "",
                        recommendations: Optional[List[Dict[str, Any]]] = None,
                        recent: Optional[List[Dict[str, Any]]] = None) -> str:
    lines = [
        "TASK: Revise",
        "Security BLOCKED what you were about to say. You get one attempt "
        "to put this right. If your revision is blocked too, the exchange "
        "is dropped and the human is simply told it was blocked — there is "
        "no third try, so make this one count or decline honestly.",
        "",
    ]
    lines += _persona_block(persona)
    conversation = render_conversation(recent)
    if conversation:
        lines += [conversation, ""]
    lines.append(f"THE HUMAN SAID: {envelope.content}")
    if blocked:
        lines.append(f"WHAT WAS BLOCKED: {blocked}")
    if verdict_detail:
        lines.append(f"WHY IT WAS BLOCKED: {verdict_detail}")
    lines += _recommendation_lines(recommendations)
    return "\n".join(lines)


def _recommendation_lines(recommendations: Optional[List[Dict[str, Any]]]) -> List[str]:
    """Analytics', Personality's and Knowledge's answers (v0.35b/Daniel
    2026-08-24), one shared shape — {sender, keywords, proceed, concern}
    — rendered as one list rather than named blocks. Intent pattern-
    matches one shape here, it doesn't parse three."""
    if not recommendations:
        return []
    lines = ["RECOMMENDATIONS:"]
    for entry in recommendations:
        sender = str(entry.get("sender", "?"))
        keywords = str(entry.get("keywords", ""))
        if not keywords:
            continue
        line = f"  - {sender}: {keywords}"
        if entry.get("proceed") is False and entry.get("concern"):
            line += f" (concern: {entry['concern']})"
        lines.append(line)
    return lines if len(lines) > 1 else []


def build_prompt(task: Task, envelope: Envelope, persona: PersonaState, **kw) -> str:
    """One entry point per register, so a tier never branches on Task.

    Callers hand over everything they have and this picks what each
    register actually uses — a tier shouldn't have to know that ADVISE
    reads `recommendation` while REVISE reads `blocked`."""
    shared = {"recommendations": kw.get("recommendations"), "recent": kw.get("recent")}
    reflex_shared = {
        "reflex_already_acted": kw.get("reflex_already_acted", False),
        "reflex_action": kw.get("reflex_action"),
    }

    if task is Task.ADVISE:
        return build_advise_prompt(envelope, kw.get("recommendation", ""),
                                   persona, reflex=kw.get("reflex"),
                                   **shared, **reflex_shared)
    if task is Task.REFUSE:
        return build_refuse_prompt(envelope, kw.get("concern", ""), persona,
                                   **shared, **reflex_shared)
    if task is Task.REVIEW:
        return build_review_prompt(envelope, persona,
                                   proposed=kw.get("proposed", ""),
                                   verdict_detail=kw.get("verdict_detail", ""),
                                   **shared)
    if task is Task.REVISE:
        return build_revise_prompt(envelope, persona,
                                   blocked=kw.get("blocked", ""),
                                   verdict_detail=kw.get("verdict_detail", ""),
                                   **shared)
    raise ValueError(f"No prompt builder for task {task!r}")


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def is_parroting(speech: str, source: str) -> bool:
    """True when `speech` is its input with the serial numbers filed off —
    the one failure mode unique to this hop (see the module docstring).

    Deliberately simple: exact match after normalization, plus the case
    where the model wrapped the input in a sentence with no other content.
    A model that genuinely reacts will not trip this; one that forgot
    which agent it is will."""
    a = " ".join(speech.lower().split())
    b = " ".join((source or "").lower().split())
    if not b:
        return False
    if a == b:
        return True
    if len(b) <= 12 or b not in a:
        return False
    wrapper = a.replace(b, " ", 1)
    wrapper_words = [w for w in wrapper.split() if w.strip(string.punctuation)]
    return len(wrapper_words) <= 4


def _speech_field(obj: Dict[str, Any]) -> str:
    speech = obj.get("speech") or obj.get("response")
    if not isinstance(speech, str) or not speech.strip():
        raise ContractViolation(f"no usable 'speech' in response: {obj!r}")
    return speech.strip()[:MAX_SPEECH_CHARS]


def _object(text: str) -> Dict[str, Any]:
    obj = extract_json_object(text)
    if obj is None:
        raise ContractViolation(f"no JSON object in response: {text[:200]!r}")
    return obj


def parse_advise(text: str, recommendation: str) -> Speech:
    speech = _speech_field(_object(text))
    if is_parroting(speech, recommendation):
        raise ContractViolation(
            "response parrots the analysis instead of voicing it "
            "— Analytics writes analysis, Intent writes speech")
    return Speech(text=speech, proceed=True, decided_by="llm")


def parse_refuse(text: str, concern: str) -> Speech:
    obj = _object(text)
    lead_in = obj.get("lead_in") or obj.get("speech")
    if not isinstance(lead_in, str) or not lead_in.strip():
        raise ContractViolation(f"no usable 'lead_in' in response: {obj!r}")
    lead_in = lead_in.strip()[:MAX_REFUSAL_LEAD_IN_CHARS]

    if lead_in.lower().startswith(_ASSENT_OPENERS):
        raise ContractViolation(
            f"refusal lead-in reads as assent, not decline: {lead_in!r}")

    text_out = f"{lead_in} {concern}".strip() if concern else lead_in
    return Speech(text=text_out, proceed=False, concern=concern, decided_by="llm")


def _parse_gated(text: str, task: Task, *, blocked: str = "") -> Speech:
    """REVIEW / REVISE — the two registers where Intent decides `proceed`.

    Note the default passed to coerce_bool: FALSE, always. A model that
    answers with something unreadable in the `proceed` field does not get
    the benefit of the doubt on a hop that gates (v0.35e)."""
    obj = _object(text)
    proceed = coerce_bool(obj.get("proceed"), default=False)

    concern = obj.get("concern") or ""
    if not isinstance(concern, str):
        concern = str(concern)
    concern = concern.strip()[:MAX_CONCERN_CHARS]

    if not proceed:
        # A decline needs no speech to be valid — the persona's line is
        # built from the concern, the same way REFUSE's is.
        if not concern:
            concern = DEFAULT_GATED_DECLINE
        spoken = obj.get("speech")
        text_out = (spoken.strip()[:MAX_SPEECH_CHARS]
                    if isinstance(spoken, str) and spoken.strip()
                    else f"{DEFAULT_REFUSAL_LEAD_IN} {concern}".strip())
        return Speech(text=text_out, proceed=False, concern=concern,
                      decided_by="llm")

    speech = _speech_field(obj)
    if task is Task.REVISE and blocked and is_parroting(speech, blocked):
        # A "revision" that restates what Security just blocked is not a
        # revision. Fail closed rather than sending it back for a second
        # verdict we already know the answer to.
        raise ContractViolation(
            "revision restates the blocked proposal instead of changing it")
    return Speech(text=speech, proceed=True, concern="", decided_by="llm")


def parse_review(text: str) -> Speech:
    return _parse_gated(text, Task.REVIEW)


def parse_revise(text: str, blocked: str = "") -> Speech:
    return _parse_gated(text, Task.REVISE, blocked=blocked)


def parse(text: str, task: Task, *, recommendation: str = "", concern: str = "",
          blocked: str = "") -> Speech:
    """One entry point per register, so a tier never branches on Task."""
    if task is Task.ADVISE:
        return parse_advise(text, recommendation)
    if task is Task.REFUSE:
        return parse_refuse(text, concern)
    if task is Task.REVIEW:
        return parse_review(text)
    if task is Task.REVISE:
        return parse_revise(text, blocked)
    raise ValueError(f"No parser for task {task!r}")


# ---------------------------------------------------------------------------
# Deterministic fallbacks
# ---------------------------------------------------------------------------

def fallback_advice(recommendation: str, reason: str) -> Speech:
    """Nothing is gated on ADVISE — degrade to a fixed, warm line, the
    same shape IntentMock has always produced."""
    return Speech(
        text=DEFAULT_ADVISE_FALLBACK, proceed=True, decided_by="fallback",
        diagnostics={"degraded": True, "reason": reason[:200]},
    )


def fallback_refusal(concern: str, reason: str) -> Speech:
    """Degrade to the deterministic lead-in — also exactly what the mock
    tier produces, so an outage doesn't change the trace's shape, only its
    provenance."""
    text = (f"{DEFAULT_REFUSAL_LEAD_IN} {concern}".strip()
            if concern else DEFAULT_REFUSAL_LEAD_IN)
    return Speech(text=text, proceed=False, concern=concern,
                  decided_by="fallback",
                  diagnostics={"degraded": True, "reason": reason[:200]})


def fallback_gated(task: Task, reason: str) -> Speech:
    """REVIEW / REVISE with an unusable answer. FAIL CLOSED — this is the
    single most important line in the file (v0.35e). Intent now holds a
    veto, so a substrate outage must resolve toward not acting, never
    toward acting."""
    concern = DEFAULT_GATED_DECLINE
    return Speech(
        text=f"{DEFAULT_REFUSAL_LEAD_IN} {concern}",
        proceed=False, concern=concern, decided_by="fallback",
        diagnostics={"degraded": True, "failed_closed": True,
                     "task": task.value, "reason": reason[:200]},
    )


def fallback(task: Task, reason: str, *, recommendation: str = "",
             concern: str = "") -> Speech:
    """The per-register fallback. Two degrade, two decline."""
    if task in FAIL_CLOSED_TASKS:
        return fallback_gated(task, reason)
    if task is Task.REFUSE:
        return fallback_refusal(concern, reason)
    return fallback_advice(recommendation, reason)


__all__ = [
    "Task", "Speech", "ContractViolation", "PersonaState",
    "GATING_TASKS", "FAIL_CLOSED_TASKS", "MAX_REVISION_PASSES",
    "ANCHORS_EPOCH_ID", "DEFAULT_CORE_ANCHORS",
    "ADVISE_RESPONSE_CONTRACT", "REFUSE_RESPONSE_CONTRACT",
    "REVIEW_RESPONSE_CONTRACT", "REVISE_RESPONSE_CONTRACT",
    "RESPONSE_CONTRACTS",
    "build_prompt", "build_advise_prompt", "build_refuse_prompt",
    "build_review_prompt", "build_revise_prompt", "render_conversation",
    "is_parroting", "parse", "parse_advise", "parse_refuse", "parse_review",
    "parse_revise",
    "fallback", "fallback_advice", "fallback_refusal", "fallback_gated",
    "DEFAULT_ADVISE_FALLBACK", "DEFAULT_REFUSAL_LEAD_IN", "DEFAULT_GATED_DECLINE",
]
