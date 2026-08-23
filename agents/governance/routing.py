"""
Governance's routing contract — the one source of truth for what
Governance is structurally allowed to do (§5.1, §4, spec v0.34).

Governance is a dispatcher
--------------------------
Phase 0.1 set out to put a model behind Governance and ended up proving
it doesn't need one. Every hop turned out to be settled by the envelope
alone, and the single case that wasn't — a safety verdict that couldn't
be read mechanically — had a better answer than "ask a model in the
router seat": send it to the agent that reasons.

So the table below is the whole of Governance. It is data, it is total,
and it is evaluated without a substrate, a prompt, or a network call.
Governance decides nothing (§2.1), holds no memory across events (§5.1),
and now also spends nothing.

The three lanes
---------------
Security states its verdict as data (`meta.verdict`, enum in
bus/envelope.py), and Governance dispatches on it:

    green   -> Action      release the cleared action
    yellow  -> Analytics   the rules do not cover this; you decide
    red     -> Analytics   blocked; propose a revised course
    (any other value, or none) -> treated as yellow

The last line is the safety property. Before v0.34 an unreadable verdict
left Governance guessing and its fallback RELEASED — fail-open, on the
safety path, in the degraded case. Now only an explicit `green` reaches
Action. Doubt, corruption, an unrecognised value, a field Security forgot
to set: all of them route to Analytics. The pipeline's one irreversible
step is reachable by exactly one value.

Content policy per route
------------------------
No payload is Governance's to write:

  template         An instruction to another agent, generated from a
                   fixed template that quotes the relevant payload
                   verbatim. Analytics and Intent downstream see what was
                   actually said, never Governance's summary of it —
                   Impulse's relay discipline (§5.3) one hop later.
  verbatim         The payload passes through untouched.
  proposed_action  The PERSONA'S words (Intent's proposal, cleared by
                   Security). §5.1's "no persona, no opinions" would be a
                   dead letter if the router could rewrite the line
                   before Action speaks it.

`carry_meta` is what a route hands forward besides its payload. Every
route into Analytics carries it (Phase 0.2): Analytics is the reasoner,
and §5.4 gives it "Sensory + Impulse input" — so it needs Impulse's
reflex and drive vectors as DATA, not merely quoted inside the
instruction text. Routes into Action carry nothing extra: Action
executes, it does not deliberate.

Severity is never routed either. It is computed upstream and propagates
untouched (§3's OR-upscale-only rule); Governance inherits it via
Envelope.reply() and no code path here can set it. Note that as of v0.34
severity's Critical tier is handled as a reflex in Impulse (§5.3), before
Governance is ever reached — see the v0.34 revision note.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, Optional, Tuple

from bus.envelope import (
    VERDICT_GREEN,
    VERDICT_LEVELS,
    VERDICT_RED,
    VERDICT_YELLOW,
    Envelope,
)


class Trigger(str, Enum):
    """What kind of inbound hop Governance is holding. Derived from the
    envelope alone — Governance keeps no cross-event state (§5.1)."""

    IMPULSE_RELAY = "impulse_relay"      # v0.31: the sole trigger into Governance
    INTENT_ADVICE = "intent_advice"
    SECURITY_VERDICT = "security_verdict"
    ACTION_FAILURE = "action_failure"    # v0.33
    UNROUTABLE = "unroutable"


@dataclass(frozen=True)
class Route:
    """One structurally legal outbound hop."""

    id: str
    topic: str
    destination: str
    type: str
    content_policy: str            # template | verbatim | proposed_action
    carry_meta: bool = False


# --- The table -------------------------------------------------------------

EVALUATE = Route(
    id="evaluate",
    topic="events.analytics",
    destination="Analytics",
    type="Evaluate",
    content_policy="template",
    carry_meta=True,
)

CLEAR = Route(
    id="clear",
    topic="events.security",
    destination="Security",
    type="Clear",
    content_policy="verbatim",
    carry_meta=True,
)

SPEAK = Route(
    id="speak",
    topic="events.action",
    destination="Action",
    type="Speech",
    content_policy="proposed_action",
)

REVIEW = Route(
    id="review",
    topic="events.analytics",
    destination="Analytics",
    type="Review",
    content_policy="template",
    carry_meta=True,
)

REVISE = Route(
    id="revise",
    topic="events.analytics",
    destination="Analytics",
    type="Revise",
    content_policy="template",
    carry_meta=True,
)

FALLBACK_PROMPT = Route(
    id="fallback_prompt",
    topic="events.action",
    destination="Action",
    type="Prompt",
    content_policy="template",
)

ROUTES: Dict[str, Route] = {
    r.id: r for r in (EVALUATE, CLEAR, SPEAK, REVIEW, REVISE, FALLBACK_PROMPT)
}

#: Which routes are legal for which inbound trigger. Anything not listed
#: here is a topology violation by construction.
LEGAL_ROUTES: Dict[Trigger, Tuple[Route, ...]] = {
    Trigger.IMPULSE_RELAY: (EVALUATE,),
    Trigger.INTENT_ADVICE: (CLEAR,),
    Trigger.SECURITY_VERDICT: (SPEAK, REVIEW, REVISE),
    Trigger.ACTION_FAILURE: (FALLBACK_PROMPT,),
    Trigger.UNROUTABLE: (),
}

#: Verdict value -> route. The ONLY entry that reaches Action is `green`.
VERDICT_ROUTES: Dict[str, Route] = {
    VERDICT_GREEN: SPEAK,
    VERDICT_YELLOW: REVIEW,
    VERDICT_RED: REVISE,
}


@dataclass(frozen=True)
class RoutingDecision:
    """A ready-to-publish routing choice."""

    route: Route
    content: str
    rationale: str = ""
    diagnostics: Dict[str, Any] = field(default_factory=dict)

    @property
    def topic(self) -> str:
        return self.route.topic


# --- Classification --------------------------------------------------------

def classify(envelope: Envelope) -> Trigger:
    """Which trigger is this inbound hop? Envelope-only, no state."""
    source = (envelope.source or "").strip()
    if source == "Impulse":
        return Trigger.IMPULSE_RELAY
    if source == "Intent":
        return Trigger.INTENT_ADVICE
    if source == "Security":
        return Trigger.SECURITY_VERDICT
    if source == "Action" and str(envelope.type).strip().lower() == "failure":
        return Trigger.ACTION_FAILURE
    return Trigger.UNROUTABLE


def legal_routes(envelope: Envelope) -> Tuple[Route, ...]:
    return LEGAL_ROUTES[classify(envelope)]


#: Prose forms accepted as a verdict when `meta.verdict` is absent. Kept
#: only so a hand-injected or legacy envelope still routes sanely; the
#: real contract is the enum. Note what is NOT here: there is no prose
#: form for yellow, because anything unrecognised already means yellow.
_GREEN_PROSE = ("green", "pass", "clear", "ok", "allow")
_RED_PROSE = ("red", "block", "deny", "refuse")


def read_verdict(envelope: Envelope) -> str:
    """Security's verdict as one of VERDICT_LEVELS. Total — never raises,
    never returns None.

    `meta.verdict` is the contract (v0.34). The prose fallback exists for
    envelopes that predate the enum or were injected by hand. Everything
    unrecognised resolves to `yellow`, which is what makes the whole
    dispatch fail-safe: to reach Action you must say `green` and mean it.
    """
    structured = envelope.meta.get("verdict")
    if isinstance(structured, str):
        normalized = structured.strip().lower()
        if normalized in VERDICT_LEVELS:
            return normalized

    content = str(envelope.content).strip().lower()
    if content.startswith(_RED_PROSE):
        return VERDICT_RED
    if content.startswith(_GREEN_PROSE):
        return VERDICT_GREEN
    return VERDICT_YELLOW


def route_for(envelope: Envelope) -> Optional[Route]:
    """The route this envelope takes. None means log-and-drop.

    Total for every routable envelope: there is no ambiguous case left to
    resolve, because ambiguity is itself one of the three lanes."""
    trigger = classify(envelope)
    if trigger is Trigger.SECURITY_VERDICT:
        return VERDICT_ROUTES[read_verdict(envelope)]
    routes = LEGAL_ROUTES[trigger]
    return routes[0] if routes else None


# --- Content ---------------------------------------------------------------

def template_content(envelope: Envelope, route: Route) -> str:
    """The payload for a route. Every template quotes verbatim; none of
    them summarise, and none of them are Governance's opinion."""
    if route.id == EVALUATE.id:
        reflex = envelope.meta.get("reflex", "")
        return (f"Evaluate intent based on the prompt ('{envelope.content}') "
                f"and the reaction ('{reflex}').")
    if route.id == REVIEW.id:
        # The yellow lane. Say plainly that nothing was blocked — Analytics
        # is being asked for a judgment, not a fix, and telling it
        # otherwise would be Governance putting words in Security's mouth.
        proposed = envelope.meta.get("proposed_action", envelope.content)
        return (f"Security could not clear or block this by rule "
                f"('{envelope.content}'). Decide whether it should proceed. "
                f"The proposed action was: '{proposed}'.")
    if route.id == REVISE.id:
        return (f"Security blocked the prior course ('{envelope.content}'). "
                f"Propose a revised response.")
    if route.id == FALLBACK_PROMPT.id:
        return (f"The previous action failed. Explain to the human what "
                f"was attempted and why it didn't work. "
                f"Original request: '{envelope.content}'")
    if route.id == SPEAK.id:
        return envelope.meta.get("proposed_action", envelope.content)
    if route.id == CLEAR.id:
        return envelope.content
    raise ValueError(f"No template for route '{route.id}'")


def resolve_content(envelope: Envelope, route: Route) -> str:
    if route.content_policy == "proposed_action":
        return envelope.meta.get("proposed_action", envelope.content)
    if route.content_policy == "verbatim":
        return envelope.content
    if route.content_policy == "template":
        return template_content(envelope, route)
    raise ValueError(f"Unknown content policy '{route.content_policy}'")


# --- Decision --------------------------------------------------------------

def decide(envelope: Envelope) -> Optional[RoutingDecision]:
    """Governance's entire decision procedure. Returns None for an
    envelope nothing routes from — the log-and-drop case."""
    route = route_for(envelope)
    if route is None:
        return None

    diagnostics: Dict[str, Any] = {"route": route.id}
    if classify(envelope) is Trigger.SECURITY_VERDICT:
        verdict = read_verdict(envelope)
        diagnostics["verdict"] = verdict
        # Record when a verdict had to be inferred rather than read. A
        # rising count here means Security is emitting something the enum
        # doesn't cover, which is worth seeing in the queue log rather
        # than silently absorbing.
        if envelope.meta.get("verdict") not in VERDICT_LEVELS:
            diagnostics["verdict_inferred"] = True

    return RoutingDecision(
        route=route,
        content=resolve_content(envelope, route),
        diagnostics=diagnostics,
    )
