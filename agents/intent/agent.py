"""
Intent — MOCK tier (§5.5, §13.1).

Templated voicing, zero LLM cost — the same posture as AnalyticsMock:
keeps every deterministic part of the role real (the persona cache, the
conversation window, the temp log, emission) and stands in only for the
thinking. `agents/intent/base.py`'s IntentBase carries that shared
scaffolding; this file supplies the one thing a tier owns, voice().

v0.35e note — the mock and the veto
------------------------------------
Intent now holds a veto on two registers (REVIEW, REVISE), and a mock
cannot judge. So it does the honest thing on both: it DECLINES, using the
same deterministic fail-closed line the live tier degrades to. That is
the opposite of AnalyticsMock's choice on its old gating tasks, which
proceeded — and the difference is deliberate. AnalyticsMock proceeded
because its gating tasks sat in the middle of the Phase 0 exit-criteria
pipeline, and a mock that declined everything would have made that suite
test something other than the pipeline. Intent's gating registers are only
ever reached from a NON-GREEN Security verdict, which the Phase 0 flow
never produces (SecurityMock always clears), so declining there costs the
happy path nothing and keeps the mock from ever rubber-stamping a yellow
or a red it cannot actually assess.

The mock's advise line is templated and never a function of its input, so
it can never trip `contract.is_parroting()` — keeping that guard's
meaning unambiguous: "the model forgot which agent it is", not "the
deterministic tier is doing what it always did".
"""
from __future__ import annotations

from agents.intent import contract
from agents.intent.base import DEFAULT_CONTEXT_EVENTS, IntentBase
from agents.intent.contract import Speech, Task
from bus.envelope import Envelope


class IntentMock(IntentBase):
    tier = "mock"

    def __init__(self, bus, archive, *,
                 context_events: int = DEFAULT_CONTEXT_EVENTS,
                 consolidator=None):
        super().__init__(bus, archive, context_events=context_events,
                         consolidator=consolidator)

    def voice(self, envelope: Envelope, task: Task) -> Speech:
        diagnostics = {"source_substrate": "mock",
                       "source_model": "none (mock tier, zero LLM cost)"}

        if task in contract.FAIL_CLOSED_TASKS:
            # See the module docstring: a mock cannot judge, so it declines.
            declined = contract.fallback_gated(task, "mock tier")
            return Speech(text=declined.text, proceed=False,
                          concern=declined.concern, decided_by="deterministic",
                          diagnostics={**declined.diagnostics, **diagnostics})

        concern = str(envelope.meta.get("concern", "")).strip()
        speech = (contract.fallback_refusal(concern, "mock tier")
                  if task is Task.REFUSE
                  else contract.fallback_advice(str(envelope.content), "mock tier"))
        # The mock's own line is deterministic-by-tier, not a degraded
        # answer — "fallback" would misreport it as an outage in the queue
        # log (same distinction AnalyticsMock draws).
        return Speech(text=speech.text, proceed=speech.proceed,
                      concern=speech.concern, decided_by="deterministic",
                      diagnostics=diagnostics)


__all__ = ["IntentMock"]
