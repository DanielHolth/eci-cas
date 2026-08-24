"""
Governance's routing contract — the one source of truth for what
Governance is structurally allowed to do (§5.1, §4, spec v0.35).

Governance is a dispatcher
--------------------------
Phase 0.1 set out to put a model behind Governance and ended up proving
it doesn't need one. Every hop turned out to be settled by the envelope
alone. So the table below is the whole of Governance. It is data, it is
total, and it is evaluated without a substrate, a prompt, or a network
call. Governance decides nothing (§2.1), holds no memory ACROSS events
(§5.1 — see agents/governance/buffer.py on the one thing it now holds
within one), and spends nothing.

v0.35 made it the universal router: every hop passes through here except
the one Sensory fan-out (v0.35a), which is deliberately ungated.

The topology
------------
    Sensory ──┬─→ Impulse      ─┐
              ├─→ Analytics    ─┤  (parallel, NO Governance hop —
              ├─→ Personality  ─┤   the one deliberate exception)
              └─→ Knowledge    ─┘
                                 └─→ Governance buffers all four,
                                     bundles them, sends ONE message
                                     to Intent
    Intent  → Governance → Security
    Security green  → Governance → Action     (release)
    Security yellow → Governance → Intent     (Review — Intent decides)
    Security red    → Governance → Intent     (Revise — one chance)
    Security red    → Governance → Action     (Blocked, on the second red)
    Action failure  → Governance → Action     (Prompt, v0.33 fallback)

Two lanes changed in v0.35e, and the second is wider than the spec draft
--------------------------------------------------------------------------
Before v0.35: Security's yellow and red both went to ANALYTICS, and
Intent was "advisory only... holds no veto" (§5.5).

After v0.35e, as confirmed by Daniel on 2026-08-24: **Analytics is
isolated from Security in every way.** Both non-green lanes route to
Intent, which now decides `proceed` on them — a real veto. Analytics is
cut back to its bare minimum: unbiased analytical keywords, contributed
into Intent's bundle, gating nothing.

The reasoning, recorded because it reverses a documented safety property:
by the time Security says anything, Intent already holds every analytical
read of the event (its own bundle) PLUS the broader conversation window
none of the single-event agents ever see. A fresh Analytics call would be
strictly less grounded than the agent that already has all of it.

Anything that is not an explicit `green` still routes away from Action.
That property is unchanged and is what keeps the dispatch fail-safe: to
reach the pipeline's one irreversible step you must say `green` and mean
it. What changed is only WHICH agent picks up the doubt.

One chance to clear, then blocked
----------------------------------
A non-green verdict buys exactly ONE more attempt
(contract.MAX_REVISION_PASSES, Daniel 2026-08-24). If the next verdict is
also non-green, the event does not loop: it becomes a BLOCKED incident —
a deterministic notice to Action carrying an expression drawn from
Impulse's live appraisal, plus a frustration nudge back into Impulse on
the control plane. Nothing about that notice is model-authored, because
nothing about it cleared Security.

The budget deliberately covers YELLOW as well as red, and that is not
over-engineering — it is the only thing standing between this router and
a live-lock. Governance forwards whatever Intent writes to Security
(INTENT_ADVICE -> CLEAR, unconditionally), including a fail-closed
DECLINE. A rule engine that yellows a decline will yellow it again, every
time, forever — and on a synchronous bus that is not a slow loop, it is
stack exhaustion inside a single ingest() call. Bounding red alone left
that door open.

Content policy per route
------------------------
No payload is Governance's to write:

  template         An instruction to another agent, generated from a
                   fixed template that quotes the relevant payload
                   verbatim. Downstream agents see what was actually
                   said, never Governance's summary of it.
  verbatim         The payload passes through untouched.
  proposed_action  The PERSONA'S words (Intent's proposal, cleared by
                   Security). §5.1's "no persona, no opinions" would be a
                   dead letter if the router could rewrite the line
                   before Action speaks it.
  bundle           The four parallel answers, carried as structured meta
                   with the original Sensory content verbatim as the
                   payload. Governance assembles the envelope; it writes
                   none of its contents.
  sensory          The original human request, verbatim, for the two
                   registers where Intent now holds the veto. The router's
                   instruction and the blocked proposal ride in meta
                   instead of replacing it — see below.

Why REVIEW and REVISE carry the request rather than the instruction
--------------------------------------------------------------------
Both were `template` routes before v0.35e, when they went to Analytics
and the payload was "here is the situation, judge it". Now they go to the
agent that HOLDS THE VETO, and its prompt renders the payload as "THE
HUMAN SAID". Sending the router's own instruction there would ask Intent
to decide "unsure means no" about a request it was never shown — and on
REVISE it would have quoted Security's verdict text as the thing to
revise. So both routes now carry `state.sensory` (the original words),
and the instruction, the blocked proposal and Security's concern ride in
meta where they can be attributed correctly.

Severity is never routed either. It is computed upstream and propagates
untouched (§3's OR-upscale-only rule); Governance inherits it via
Envelope.reply() and no code path here can set it.
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


#: The four agents whose answers Governance bundles (v0.35a).
WORKERS = ("Impulse", "Analytics", "Personality", "Knowledge")


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

#: The YELLOW lane. v0.35e: Intent, not Analytics.
REVIEW = Route(
    id="review",
    topic="events.intent",
    destination="Intent",
    type="Review",
    content_policy="sensory",
    carry_meta=True,
)

#: The RED lane. v0.35e: Intent, not Analytics. One attempt only.
REVISE = Route(
    id="revise",
    topic="events.intent",
    destination="Intent",
    type="Revise",
    content_policy="sensory",
    carry_meta=True,
)

#: The second red. Not a loop — an outcome (Daniel, 2026-08-24).
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

ROUTES: Dict[str, Route] = {
    r.id: r for r in (BUNDLE, CLEAR, SPEAK, REVIEW, REVISE, BLOCKED, REFLEX,
                      FALLBACK_PROMPT)
}

#: Which routes are legal for which inbound trigger. Anything not listed
#: here is a topology violation by construction.
LEGAL_ROUTES: Dict[Trigger, Tuple[Route, ...]] = {
    Trigger.WORKER_REPORT: (BUNDLE, REFLEX),
    Trigger.INTENT_ADVICE: (CLEAR,),
    Trigger.SECURITY_VERDICT: (SPEAK, REVIEW, REVISE, BLOCKED),
    Trigger.ACTION_FAILURE: (FALLBACK_PROMPT,),
    Trigger.UNROUTABLE: (),
}

#: Verdict value -> route. The ONLY entry that reaches Action's SPEAK is
#: `green`. Red resolves to REVISE or BLOCKED depending on how many
#: attempts have already been spent — see route_for().
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
    if source in WORKERS:
        return Trigger.WORKER_REPORT
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
        # Green is the only value that leaves the loop by clearing. Every
        # other value spends an attempt, and when the budget is gone the
        # event is blocked rather than re-asked. See the module docstring
        # on why this covers yellow too.
        if verdict != VERDICT_GREEN and revision_passes >= max_revision_passes:
            return BLOCKED
        return VERDICT_ROUTES[verdict]

    routes = LEGAL_ROUTES[trigger]
    return routes[0] if routes else None


# --- Content ---------------------------------------------------------------

def template_content(envelope: Envelope, route: Route) -> str:
    """The payload for a route. Every template quotes verbatim; none of
    them summarise, and none of them are Governance's opinion."""
    if route.id == REVIEW.id:
        # The yellow lane. Say plainly that nothing was blocked — Intent
        # is being asked for a judgment, not a fix, and telling it
        # otherwise would be Governance putting words in Security's mouth.
        proposed = envelope.meta.get("proposed_action", "")
        return (f"Security could not clear or block this by rule. Decide "
                f"whether it should proceed. The proposed action was: "
                f"'{proposed}'.")
    if route.id == REVISE.id:
        # Quote the PROPOSAL, not the verdict envelope's content — the
        # thing being revised is what Intent said, not what Security said
        # about it.
        proposed = envelope.meta.get("proposed_action", "")
        return (f"Security blocked the prior course ('{proposed}'). "
                f"Propose a revised response. This is the only revision "
                f"available — if it is blocked again the exchange is dropped.")
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
    if route.id == REFLEX.id:
        diagnostics["critical_reflex"] = True

    return RoutingDecision(
        route=route,
        content=resolve_content(envelope, route, sensory=sensory),
        diagnostics=diagnostics,
    )


__all__ = [
    "Trigger", "Route", "RoutingDecision", "WORKERS", "CRITICAL",
    "BUNDLE", "CLEAR", "SPEAK", "REVIEW", "REVISE", "BLOCKED", "REFLEX",
    "FALLBACK_PROMPT", "ROUTES", "LEGAL_ROUTES", "VERDICT_ROUTES",
    "classify", "legal_routes", "read_verdict", "is_critical", "route_for",
    "template_content", "resolve_content", "decide",
]
