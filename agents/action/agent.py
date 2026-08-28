"""
Action — deterministic executor (§5.7, §13.1).

No persona, no judgment — executes exactly what Governance hands it
after Security clearance. Fans content out to configured sinks
(agents/action/sinks.py). Silent on success; reports one Failure
envelope to Governance on failure (v0.33 contract).

Blocked notices (from twice-refused exchanges) are handled like any
other action. `meta.expression` carries Impulse's live appraisal for
product-layer rendering.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.action.sinks import Sink, SinkError


class ActionMock:
    tier = "mock"

    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.executed: List[Envelope] = []
        #: Blocked incidents specifically (v0.35e), kept separately so a
        #: harness can assert on them without filtering the whole log.
        self.blocked: List[Envelope] = []
        # Testing knob only: forces the next N executions to fail, so the
        # failure/fallback path is actually exercisable without needing a
        # real, flaky world to fail against.
        self.force_next_failures = 0
        self.bus.subscribe("events.action", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        self.executed.append(envelope)
        if envelope.type == "Blocked":
            self.blocked.append(envelope)

        if self.force_next_failures > 0:
            self.force_next_failures -= 1
            success = False
        else:
            success = True

        if success:
            return  # silent on success — no envelope, no re-entry (v0.33)

        # Action failed. Report to Governance, which owns deciding the
        # fallback response (always: issue a Prompt action explaining
        # the failure, §5.7 v0.33).
        out = envelope.reply(
            source="Action",
            destination="Governance",
            type="Failure",
            content=envelope.content,
        )
        self.bus.publish("events.governance", out)


class ActionAgent(ActionMock):
    """Action running real: the same executor, with sinks attached.

    Subclassing ActionMock rather than duplicating it is deliberate. The
    executed/blocked logs, the failure envelope, the force_next_failures
    knob — all of that is the ROLE, and it was never the mock's. What the
    mock lacked was an outside world; that is the only thing added here.

    Fan-out is all-or-nothing per sink, not per event: every configured
    sink is attempted, and a failure in one does not stop the others.
    Half-delivered is the honest outcome when a system speaks through two
    channels and one of them is broken — and reporting the failure is
    what lets the human hear about it through the channel that still
    works."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, sinks: Optional[List[Sink]] = None):
        self.sinks: List[Sink] = list(sinks or [])
        #: Per-sink emission counts, and the failures each one has had.
        #: A sink that stopped working halfway through a long session is
        #: otherwise invisible: the events still concluded, and Archive
        #: still logged them, because the pipeline does not wait on the
        #: world.
        self.metrics: Dict[str, Any] = {
            "emitted": 0, "failed": 0,
            "per_sink": {s.name: 0 for s in self.sinks},
        }
        super().__init__(bus)

    def on_event(self, envelope: Envelope) -> None:
        self.executed.append(envelope)
        if envelope.type == "Blocked":
            self.blocked.append(envelope)

        errors: List[str] = []
        if self.force_next_failures > 0:
            self.force_next_failures -= 1
            errors.append("forced failure (testing knob)")
        else:
            for sink in self.sinks:
                try:
                    sink.emit(envelope)
                except SinkError as exc:
                    errors.append(str(exc))
                except Exception as exc:
                    # A sink is third-party-ish by nature (a TTS engine, a
                    # socket, somebody's callback). It does not get to take
                    # the pipeline down, and it does not get to fail
                    # silently either.
                    errors.append(f"sink '{sink.name}' raised "
                                  f"{type(exc).__name__}: {exc}")
                else:
                    self.metrics["per_sink"][sink.name] = (
                        self.metrics["per_sink"].get(sink.name, 0) + 1)

        if not errors:
            self.metrics["emitted"] += 1
            return  # silent on success — no envelope, no re-entry (v0.33)

        self.metrics["failed"] += 1
        out = envelope.reply(
            source="Action",
            destination="Governance",
            type="Failure",
            # The ORIGINAL content, not an error message. Governance's
            # fallback quotes this to the human through Intent, so
            # replacing it with a stack trace would put Action's words in
            # the persona's mouth — the exact thing the role forbids. The
            # diagnosis rides in meta, where nothing speaks it aloud.
            content=envelope.content,
            meta={**envelope.meta, "action_errors": errors},
        )
        self.bus.publish("events.governance", out)

    def close(self) -> None:
        """Release every sink. Safe to call more than once."""
        for sink in self.sinks:
            try:
                sink.close()
            except Exception:                        # pragma: no cover
                pass


__all__ = ["ActionMock", "ActionAgent"]
