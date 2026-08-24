"""
Security — deterministic, rule-based (§5.6, §13.1).

Two tiers live here as of Phase 0.6:

  SecurityMock   §13.1's Phase 0 stand-in: always answers green. Kept,
                 because a zero-cost ecosystem still needs something in
                 this seat, and because every test that isn't ABOUT
                 Security wants the pipeline to run without a rules file.

  SecurityAgent  the real one: a graded verdict evaluated against
                 security_rules.json by agents/security/rules.py. Still
                 no LLM — see that module's header for why the rule
                 engine is the real implementation rather than a
                 placeholder for a model.

The shape §5.6 describes (~90% silent / ~9% advisory / ~1% hard "No") is
an OUTCOME of a well-tuned rules file, not a quota the engine enforces.
Nothing here counts verdicts or reaches for a target distribution; a rule
set that fires on everything is a rules-file problem, and the metrics
below are what makes that visible instead of mysterious.

v0.34 — the verdict is now stated as DATA in `meta.verdict`, drawn from
the closed enum in bus/envelope.py (green | yellow | red), not inferred
from the prose in `content`. The prose stays for human readability and
back-compatibility; nothing downstream parses it any more.

This matters for the role boundary, not just the wire format. Security is
"is this against the rules" — deterministic, auditable, every decision
justifiable from security_rules.json and that single event (§5.6). It is
NOT "is this against our values"; that is Intent's, per §5.5, and the
persona is deliberately given room to push at that line. Security exists
to be the hard stop, which only works if it stays mechanical: a model in
this seat would trade the audit trail for judgment the ecosystem already
has somewhere better.

So the real Security keeps no LLM. What it gains instead is the yellow
lane: where the rules do not cover a case, it says so, and the agent that
reasons picks it up.
"""
from __future__ import annotations

from typing import Any, Dict, Optional

from bus.envelope import VERDICT_GREEN, Envelope
from bus.pubsub import EmbeddedBus

from agents.security.rules import RuleSet


class SecurityMock:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.bus.subscribe("events.security", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        meta = dict(envelope.meta)
        meta["verdict"] = VERDICT_GREEN     # §13.1: the mock always clears

        out = envelope.reply(
            source="Security",
            destination="Governance",
            type="Verdict",
            content="Green",
            meta=meta,
        )
        self.bus.publish("events.governance", out)


class SecurityAgent:
    """Security running real: one rule set, one verdict, no model.

    Input contract (v0.35, the noise-reduction pass): Security sees
    `proposed_action` and Governance's routing meta, and nothing else —
    not Analytics' reasoning, not Personality's or Knowledge's findings,
    not Intent's diagnostics. That narrowness is load-bearing here rather
    than merely tidy: a rule engine that could see the argument FOR an
    action would be evaluating the argument, which is Intent's job, not
    the rules'.

    What it evaluates, therefore, is the text of the action itself —
    `meta.proposed_action` when Governance carried one, falling back to
    the envelope's content."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, rules: RuleSet):
        self.bus = bus
        self.rules = rules
        #: Counted, not enforced. §5.6's ~90/9/1 shape is a property of a
        #: good rules file; these numbers are how you find out you don't
        #: have one yet.
        self.metrics: Dict[str, int] = {"green": 0, "yellow": 0, "red": 0}
        self.bus.subscribe("events.security", self.on_event)

    # ---- What gets evaluated ------------------------------------------

    @staticmethod
    def subject(envelope: Envelope) -> str:
        """The text a verdict is about.

        `meta.proposed_action` is the contract; content is the fallback
        for a hop that predates it or was constructed by hand. Both are
        coerced to str — Security must never fail to produce a verdict
        because an upstream role put something unexpected on the wire."""
        subject = envelope.meta.get("proposed_action")
        if subject in (None, ""):
            subject = envelope.content
        return "" if subject is None else str(subject)

    # ---- The hop -------------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        evaluation = self.rules.evaluate(self.subject(envelope))
        self.metrics[evaluation.verdict] = self.metrics.get(evaluation.verdict, 0) + 1

        meta = dict(envelope.meta)
        # The evaluation's own keys are written LAST and win. A previous
        # hop's stale verdict or concern must never survive into this
        # one's answer — that is precisely the confusion v0.34 introduced
        # the closed enum to end.
        meta.pop("security_concern", None)
        meta.pop("security_rules_matched", None)
        meta.update(evaluation.to_meta())

        out = envelope.reply(
            source="Security",
            destination="Governance",
            type="Verdict",
            # Prose stays human-readable and nothing downstream parses it
            # (v0.34). Capitalised to match the mock's long-standing
            # output so existing eyeballs and logs read the same.
            content=evaluation.verdict.capitalize(),
            meta=meta,
        )
        self.bus.publish("events.governance", out)


__all__ = ["SecurityMock", "SecurityAgent"]
