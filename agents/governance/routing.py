"""
Governance's routing contract (§5.1, §4, spec v0.35).

Envelope-only dispatcher — no substrate, no state across events.

Topology::

    Sensory ──┬─→ Impulse      ─┐
              ├─→ Analytics    ─┤  (parallel, no Governance hop)
              └─→ Personality  ─┘
                                 └─→ Governance bundles → Intent
    Intent  → Governance → Security
    Security green  → Action     (SPEAK)
    Security yellow → Intent     (REVISE — one chance, then SPEAK anyway)
    Security red    → Action     (BLOCKED, immediate)
    Action failure  → Action     (Prompt fallback)

Non-green verdicts get ONE revision attempt. Red is a rule violation, so
exhausting the attempt blocks. Yellow is NOT a violation — it is the
rules declining to judge — so exhausting the attempt lets the event
through rather than blocking it; blocking on mere ambiguity would make
every unresolved judgment call a hard stop, which is Security's job
description only for red. REVISE carries the original request as
payload; the router instruction rides in meta.
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

#: Impulse's severity read that skips cognition entirely (v0.35d).
CRITICAL = "Critical"


class Trigger(str, Enum):
    """What kind of inbound hop Governance is holding. Derived from the
    envelope alone — Governance keeps no cross-event state (§5.1)."""

    WORKER_REPORT = "worker_report"      # v0.35a/c: one of the four parallel answers
    INTENT_ADVICE = "intent_advice"
    SECURITY_VERDICT = "security_verdict"
    ACTION_FAILURE = "action_failure"    # v0.33
    UNROUTABLE = "unroutable"


#: The agents whose answers Governance bundles (Knowledge removed Phase 0.8 — swarm replaces it).
WORKERS = ("Impulse", "Analytics", "Personality")


@dataclass(frozen=True)
class Route:
    """One structurally legal outbound hop."""

    id: str
    topic: str
    destination: str
    type: str
    content_policy: str            # template | verbatim | proposed_action | bundle
    carry_meta: bool = False


# --- The table -------------------------------------------------------------

#: The four parallel answers, assembled. Replaces v0.34's EVALUATE route
#: into Analytics — Analytics is now an input to this, not the recipient
#: of a relay.
BUNDLE = Route(
    id="bundle",
    topic="events.intent",
    destination="Intent",
    type="Bundle",
    content_policy="bundle",
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

#: The non-green lane. One attempt only.
REVISE = Route(
    id="revise",
    topic="events.intent",
    destination="Intent",
    type="Revise",
    content_policy="sensory",
    carry_meta=True,
)

#: The second red, or a red on the first pass. Not a loop — an outcome
#: (Daniel, 2026-08-24). Yellow no longer routes here (2026-08-28): a
#: second yellow is not a rule violation either, so it proceeds via SPEAK
#: instead of dead-ending here.
BLOCKED = Route(
    id="blocked",
    topic="events.action",
    destination="Action",
    type="Blocked",
    content_policy="template",
    carry_meta=True,
)

#: v0.35d's Critical fast path: straight to Security, skipping the bundle
#: and Intent's voicing on the way in.
REFLEX = Route(
    id="reflex",
    topic="events.security",
    destination="Security",
    type="Clear",
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

#: Which routes are legal for which inbound trigger. Anything not listed
#: here is a topology violation by construction.
LEGAL_ROUTES: Dict[Trigger, Tuple[Route, ...]] = {
    Trigger.WORKER_REPORT: (BUNDLE, REFLEX),
    Trigger.INTENT_ADVICE: (CLEAR,),
    Trigger.SECURITY_VERDICT: (SPEAK, REVISE, BLOCKED),
    Trigger.ACTION_FAILURE: (FALLBACK_PROMPT,),
    Trigger.UNROUTABLE: (),
}

#: Verdict value -> route. Green clears to Action. Yellow gets one chance
#: to re-speak (REVISE). Red is blocked immediately.
VERDICT_ROUTES: Dict[str, Route] = {
    VERDICT_GREEN: SPEAK,
    VERDICT_YELLOW: REVISE,
    VERDICT_RED: BLOCKED,
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
    if source in WORKERS:
        return Trigger.WORKER_REPORT
    if source == "Intent":
        return Trigger.INTENT_ADVICE
    if source == "Security":
        return Trigger.SECURITY_VERDICT
    if source == "Action" and str(envelope.type).strip().lower() == "failure":
        return Trigger.ACTION_FAILURE
    return Trigger.UNROUTABLE


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
    dispatch fail-safe: to reach Action you must say `green` and mean it."""
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


def is_critical(envelope: Envelope) -> bool:
    """v0.35d's fast path: Impulse read this as a genuine emergency.

    Only Impulse's own hop can open it, and only because Sensory tagged
    the event Critical upstream — Impulse's own assessment is hard-capped
    at Elevated (agents/impulse/agent.py's IMPULSE_SEVERITY_CEILING), so
    drive-vector state alone can never manufacture this."""
    return (envelope.source == "Impulse"
            and str(envelope.severity).strip() == CRITICAL)


def route_for(envelope: Envelope, *, bundle_ready: bool = False,
              revision_passes: int = 0,
              max_revision_passes: int = 1) -> Optional[Route]:
    """The route this envelope takes. None means hold or drop.

    `bundle_ready` is Governance's buffer telling the table whether the
    other three answers are in yet: a worker report with an incomplete
    bundle routes nowhere and is held (see agents/governance/buffer.py).

    `revision_passes` is how many clearance attempts this event has
    already spent. A non-green verdict with the budget exhausted becomes
    BLOCKED rather than being re-asked."""
    trigger = classify(envelope)

    if trigger is Trigger.WORKER_REPORT:
        if is_critical(envelope):
            return REFLEX
        return BUNDLE if bundle_ready else None

    if trigger is Trigger.SECURITY_VERDICT:
        verdict = read_verdict(envelope)
        if revision_passes >= max_revision_passes:
            # Red is a rule violation: the budget being gone doesn't change
            # that, so it blocks (it would have blocked on pass 0 too).
            if verdict == VERDICT_RED:
                return BLOCKED
            # Yellow is NOT a rule violation — it is the rules declining to
            # judge (§5.6). One revision is offered so Intent can address
            # the concern; if it comes back yellow again that is still not
            # a violation, so the event proceeds rather than dead-ending in
            # a block. Only red is a hard stop.
            if verdict == VERDICT_YELLOW:
                return SPEAK
        return VERDICT_ROUTES[verdict]

    routes = LEGAL_ROUTES[trigger]
    return routes[0] if routes else None


# --- Content ---------------------------------------------------------------

def template_content(envelope: Envelope, route: Route) -> str:
    """The payload for a route. Every template quotes verbatim; none of
    them summarise, and none of them are Governance's opinion."""
    if route.id == REVISE.id:
        # Quote the PROPOSAL, not the verdict envelope's content — the
        # thing being revised is what Intent said, not what Security said
        # about it. Only reached on yellow (red skips straight to BLOCKED),
        # so this is a chance to address the concern, not a last warning —
        # a second yellow proceeds rather than being blocked.
        proposed = envelope.meta.get("proposed_action", "")
        return (f"Security flagged the prior course ('{proposed}') as a "
                f"judgment call, not a rule violation. Propose a revised "
                f"response if you can address the concern.")
    if route.id == BLOCKED.id:
        # Nothing model-authored reaches the human here: nothing cleared.
        return ("That one was blocked, and my attempt to put it another way "
                "was blocked too. I'm going to leave it there.")
    if route.id == REFLEX.id:
        reflex = envelope.meta.get("reflex", "")
        return (f"Critical severity reflex. Original input: "
                f"'{envelope.content}'. Reaction: '{reflex}'.")
    if route.id == FALLBACK_PROMPT.id:
        return (f"The previous action failed. Explain to the human what "
                f"was attempted and why it didn't work. "
                f"Original request: '{envelope.content}'")
    if route.id == SPEAK.id:
        return envelope.meta.get("proposed_action", envelope.content)
    if route.id in (CLEAR.id, BUNDLE.id):
        return envelope.content
    raise ValueError(f"No template for route '{route.id}'")


def resolve_content(envelope: Envelope, route: Route,
                    sensory: str = "") -> str:
    if route.content_policy == "proposed_action":
        return envelope.meta.get("proposed_action", envelope.content)
    if route.content_policy == "verbatim":
        return envelope.content
    if route.content_policy == "sensory":
        # The original human request. See the module docstring on why the
        # two gating registers carry this rather than the instruction —
        # the instruction rides in meta.router_instruction instead.
        return sensory or envelope.meta.get("proposed_action") or envelope.content
    if route.content_policy == "bundle":
        # The original Sensory content, verbatim — the four answers ride
        # in meta.bundle. Intent must see what was actually said, not any
        # worker's restatement of it.
        return sensory or envelope.content
    if route.content_policy == "template":
        return template_content(envelope, route)
    raise ValueError(f"Unknown content policy '{route.content_policy}'")


# --- Decision --------------------------------------------------------------

def decide(envelope: Envelope, *, bundle_ready: bool = False,
           revision_passes: int = 0, max_revision_passes: int = 1,
           sensory: str = "") -> Optional[RoutingDecision]:
    """Governance's entire decision procedure. Returns None for an
    envelope nothing routes from — the hold-or-drop case."""
    route = route_for(envelope, bundle_ready=bundle_ready,
                      revision_passes=revision_passes,
                      max_revision_passes=max_revision_passes)
    if route is None:
        return None

    diagnostics: Dict[str, Any] = {"route": route.id}
    trigger = classify(envelope)
    if trigger is Trigger.SECURITY_VERDICT:
        verdict = read_verdict(envelope)
        diagnostics["verdict"] = verdict
        # Record when a verdict had to be inferred rather than read. A
        # rising count here means Security is emitting something the enum
        # doesn't cover, which is worth seeing in the queue log rather
        # than silently absorbing.
        if envelope.meta.get("verdict") not in VERDICT_LEVELS:
            diagnostics["verdict_inferred"] = True
        if route.id == BLOCKED.id:
            diagnostics["revision_passes"] = revision_passes
        if route.id == SPEAK.id and verdict == VERDICT_YELLOW:
            # Distinguishes "green cleared it" from "yellow's revision
            # budget ran out and it proceeded anyway" in the audit trail.
            diagnostics["revision_passes"] = revision_passes
            diagnostics["yellow_exhausted"] = True
    if route.id == REFLEX.id:
        diagnostics["critical_reflex"] = True

    return RoutingDecision(
        route=route,
        content=resolve_content(envelope, route, sensory=sensory),
        diagnostics=diagnostics,
    )


__all__ = [
    "Trigger", "Route", "RoutingDecision", "WORKERS", "CRITICAL",
    "BUNDLE", "CLEAR", "SPEAK", "REVISE", "BLOCKED", "REFLEX",
    "FALLBACK_PROMPT", "LEGAL_ROUTES", "VERDICT_ROUTES",
    "classify", "read_verdict", "is_critical", "route_for",
    "template_content", "resolve_content", "decide",
]
