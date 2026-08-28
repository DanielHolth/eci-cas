"""
Intent's output contract, and its persona state (§5.5, §6, v0.35c/e).

Intent does ONE thing: speak to the human. It produces plain text — no
JSON, no structured output. Security and Governance own all safety
routing; Intent never judges, never refuses, never revises knowingly.
It just speaks, and if Security flags the speech, Governance either
asks Intent to speak again (with the concern visible) or blocks outright.

The one thing validated here that no other agent has:
  The response did not just parrot its inputs. A live node with nothing
  to say can copy its input back out with a different label on it, which
  would break the Analytics-writes-analysis / Intent-writes-speech
  boundary. See `is_parroting()`.
"""
from __future__ import annotations

import string
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, List, Optional

from bus.envelope import Envelope

#: No longer enforced (2026-08-29, Daniel) — this was cutting the
#: persona's actual reply to the human off mid-sentence at 600 chars.
#: Kept as a name only for anything still importing it; nothing slices
#: against it any more.
MAX_SPEECH_CHARS = 600

#: How many revision attempts a non-green verdict buys. ONE: if Security
#: blocks the re-speak too, the event is blocked outright.
MAX_REVISION_PASSES = 1

DEFAULT_ADVISE_FALLBACK = "Hey there! Awake and pleased to interact."


class Task(str, Enum):
    ADVISE = "Advise"

    @classmethod
    def from_envelope(cls, envelope: Envelope) -> Optional["Task"]:
        """Intent always advises. The envelope type doesn't matter —
        Bundle, Recommend, Review, Revise all resolve to ADVISE.
        Intent speaks; Governance and Security decide what to do with it."""
        raw = str(envelope.type).strip()
        if raw in ("Recommend", "Bundle", "Review", "Revise"):
            return cls.ADVISE
        try:
            return cls(raw)
        except ValueError:
            return None


@dataclass(frozen=True)
class Speech:
    """Intent's output: plain text speech."""
    text: str
    decided_by: str = "llm"          # llm | fallback | deterministic | budget
    diagnostics: Dict[str, Any] = field(default_factory=dict)


class ContractViolation(ValueError):
    """The substrate's answer cannot be used. Always recoverable."""


# ---------------------------------------------------------------------------
# Persona state (§5.5, §6, §7.4)
# ---------------------------------------------------------------------------

ANCHORS_EPOCH_ID = "genesis-core-anchors"

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
    "boundaries": (
        "Security is a hard stop you do not argue with. If your message "
        "is flagged, you may be asked to try again with the concern in "
        "mind — just speak naturally, addressing the concern."
    ),
}


@dataclass
class PersonaState:
    """Core Anchors. Hydrated once and cached."""

    anchors: Dict[str, Any]

    def render(self, *, max_chars: int = 1400) -> str:
        lines = [f"STANCE: {self.anchors.get('stance', '')}"]
        values = self.anchors.get("values") or []
        if values:
            lines.append("VALUES: " + " ".join(f"- {v}" for v in values))
        boundaries = self.anchors.get("boundaries")
        if boundaries:
            lines.append(f"BOUNDARIES: {boundaries}")
        text = "\n".join(lines)
        return text[:max_chars]


# ---------------------------------------------------------------------------
# Response contract — plain text, no JSON
# ---------------------------------------------------------------------------

RESPONSE_CONTRACT = """
Reply with your response to the human. Nothing else — no JSON, no
labels, no preamble, no wrapping.

RULES:
- One sentence, two at most. Never longer.
- Answer the question directly. Do not restate it, do not narrate
  your reasoning, do not ask what the human meant.
- Never start with "You asked", "You mentioned", "I think you're asking",
  or any paraphrase of what the human said.
- Talk like a person, not a system explaining itself.
- A Knowledge fact whose path starts with "system/" describes YOU (your
  own name, traits, preferences) — never attribute it to the human. A
  fact whose path starts with "person/" describes the human or someone
  they've told you about.
"""


# ---------------------------------------------------------------------------
# Prompting
# ---------------------------------------------------------------------------

def render_conversation(recent: Optional[List[Dict[str, Any]]],
                        *, max_chars_per_event: int = 160) -> str:
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


def _recommendation_lines(recommendations: Optional[List[Dict[str, Any]]]) -> List[str]:
    if not recommendations:
        return []
    lines = ["RECOMMENDATIONS:"]
    for entry in recommendations:
        sender = str(entry.get("sender", "?"))
        keywords = str(entry.get("keywords", ""))
        if not keywords:
            continue
        lines.append(f"  - {sender}: {keywords}")
    return lines if len(lines) > 1 else []


def build_prompt(task: Task, envelope: Envelope, persona: PersonaState, **kw) -> str:
    """Single entry point. Intent always speaks the same way — the task
    enum is kept for interface compatibility but has only one value."""
    lines = [
        "TASK: Speak to the human.",
        "Use the RECOMMENDATIONS below to inform your reply.",
        "",
    ]
    lines += _persona_block(persona)
    conversation = render_conversation(kw.get("recent"))
    if conversation:
        lines += [conversation, ""]
    lines.append(f"THE HUMAN SAID: {envelope.content}")
    lines += _recommendation_lines(kw.get("recommendations"))
    reflex = kw.get("reflex")
    if reflex:
        lines.append(f"GUT REACTION (Impulse): {reflex}")
    if kw.get("reflex_already_acted"):
        lines.append(
            "NOTE: A reflex of yours already acted on this, before you "
            "could weigh in. The human has already seen it happen. "
            "Acknowledge it naturally.")
        reflex_action = kw.get("reflex_action")
        if reflex_action:
            lines.append(f"WHAT THE REFLEX ALREADY DID: {reflex_action}")
    security_concern = kw.get("security_concern")
    if security_concern:
        lines.append(f"SECURITY CONCERN: {security_concern} — address this "
                     "in your reply or find a different way to respond.")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Parsing — plain text, no JSON extraction
# ---------------------------------------------------------------------------

def is_parroting(speech: str, source: str) -> bool:
    """True when `speech` is its input with the serial numbers filed off."""
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


def parse(text: str, task: Task, *, recommendation: str = "", **_kw) -> Speech:
    """Parse plain text speech from the LLM. No JSON extraction needed."""
    speech = text.strip()
    if not speech:
        raise ContractViolation(f"empty response from Intent: {text[:200]!r}")
    if is_parroting(speech, recommendation):
        raise ContractViolation(
            "response parrots the analysis instead of voicing it "
            "— Analytics writes analysis, Intent writes speech")
    return Speech(text=speech, decided_by="llm")


# ---------------------------------------------------------------------------
# Deterministic fallback
# ---------------------------------------------------------------------------

def fallback(task: Task, reason: str, *, recommendation: str = "",
             **_kw) -> Speech:
    """Degrade to a fixed, warm line."""
    return Speech(
        text=DEFAULT_ADVISE_FALLBACK, decided_by="fallback",
        diagnostics={"degraded": True, "reason": reason[:200]},
    )


__all__ = [
    "Task", "Speech", "ContractViolation", "PersonaState",
    "MAX_REVISION_PASSES", "MAX_SPEECH_CHARS",
    "ANCHORS_EPOCH_ID", "DEFAULT_CORE_ANCHORS",
    "RESPONSE_CONTRACT",
    "build_prompt", "render_conversation",
    "is_parroting", "parse",
    "fallback", "DEFAULT_ADVISE_FALLBACK",
]
