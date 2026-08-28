"""
Intent — MOCK tier (§5.5, §13.1).

Templated voicing, zero LLM cost. Intent always speaks — Security and
Governance decide what to do with the speech.
"""
from __future__ import annotations

from agents.intent import contract
from agents.intent.base import DEFAULT_CONTEXT_EVENTS, IntentBase
from agents.intent.contract import Speech, Task
from bus.envelope import Envelope


class IntentMock(IntentBase):
    tier = "mock"

    def __init__(self, bus, archive, *,
                 context_events: int = DEFAULT_CONTEXT_EVENTS):
        super().__init__(bus, archive, context_events=context_events)

    def voice(self, envelope: Envelope, task: Task) -> Speech:
        diagnostics = {"source_substrate": "mock",
                       "source_model": "none (mock tier, zero LLM cost)"}
        speech = contract.fallback(task, "mock tier",
                                   recommendation=str(envelope.content))
        return Speech(text=speech.text, decided_by="deterministic",
                      diagnostics=diagnostics)


__all__ = ["IntentMock"]
