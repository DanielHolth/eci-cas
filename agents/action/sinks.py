"""
Action's output sinks (Phase 0.6, §5.7).

Going live for Action does not mean giving it judgment — it means giving
it somewhere for `envelope.content` to actually GO. Everything upstream
of here reasons; this is the one place where the system stops thinking
and does something in the world.

So the invariant is stated as an interface rather than as a comment: a
sink receives the content Governance authorised and emits it. It gets no
opportunity to rewrite, prefix, summarise or decorate, because it is
never handed the pipeline's reasoning in the first place — only the text
and the envelope's own metadata. "Action executes exactly what Governance
hands it and authors nothing of its own" survives by construction.

A sink signals failure by RAISING. That is deliberate: Phase 0.33's
failure contract (report to Governance, which issues a Prompt explaining
the failure to the human) needs a clean boundary between "emitted" and
"did not emit", and a boolean return invites a sink to half-succeed
quietly. Anything a sink raises is caught by the agent and turned into
the one Failure envelope the contract allows.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from bus.envelope import Envelope


class SinkError(RuntimeError):
    """A sink could not emit. Caught by ActionAgent, never propagated."""


class Sink:
    """One output channel. Subclass and implement emit().

    `name` is what shows up in diagnostics and in the failure report, so
    a multi-sink deployment can say WHICH channel failed rather than just
    that something did."""

    name = "sink"

    def emit(self, envelope: Envelope) -> None:      # pragma: no cover - iface
        raise NotImplementedError

    def close(self) -> None:
        """Release anything held. Idempotent; safe to call on a sink that
        never opened anything."""

    def __repr__(self) -> str:                       # pragma: no cover - debug
        return f"<{type(self).__name__} {self.name}>"


class NullSink(Sink):
    """Emits nowhere, records everything.

    Not a test double bolted on afterwards — this is what ActionMock's
    `executed` list always was, given a name and a place in the hierarchy.
    A deployment with no configured output is running this, and it says
    so, rather than looking like a working system that nobody can hear."""

    name = "null"

    def __init__(self) -> None:
        self.emitted: List[Envelope] = []

    def emit(self, envelope: Envelope) -> None:
        self.emitted.append(envelope)


class StreamSink(Sink):
    """Writes the content to a text stream — stdout by default.

    The plainest real interface there is, and the one a console session
    actually needs. `prefix` is a display affordance for the human (so a
    Blocked notice is visually distinguishable from ordinary speech), NOT
    authored content: it is derived from the envelope's own type and
    Impulse's `meta.expression`, both of which arrived from upstream.
    Action still decides nothing."""

    name = "stream"

    def __init__(self, stream=None, *, show_expression: bool = True):
        self.stream = stream if stream is not None else sys.stdout
        self.show_expression = show_expression

    def emit(self, envelope: Envelope) -> None:
        expression = envelope.meta.get("expression")
        label = str(envelope.type)
        if self.show_expression and expression:
            label = f"{label}/{expression}"
        try:
            self.stream.write(f"[{label}] {envelope.content}\n")
            flush = getattr(self.stream, "flush", None)
            if callable(flush):
                flush()
        except Exception as exc:
            raise SinkError(f"stream write failed: {exc}") from exc


class FileSink(Sink):
    """Appends one JSON object per emitted action.

    The durable transcript of everything this system has actually said or
    done — separate from Archive's queue log on purpose. The queue log
    records every hop of every event, including the ones that never
    reached the world; this file records only what the world saw. When
    somebody asks "what did it actually do", they should not have to
    filter a bus trace to find out."""

    name = "file"

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)

    def emit(self, envelope: Envelope) -> None:
        record = {
            "emitted_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "event_id": envelope.event_id,
            "type": envelope.type,
            "severity": envelope.severity,
            "content": envelope.content,
            "expression": envelope.meta.get("expression"),
            "blocked": bool(envelope.meta.get("blocked")),
        }
        try:
            with self.path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(record, ensure_ascii=False) + "\n")
        except OSError as exc:
            raise SinkError(f"transcript write failed ({self.path}): {exc}") from exc


class CallbackSink(Sink):
    """Hands the envelope to a Python callable.

    The seam a product layer attaches to — TTS, an avatar frame, a chat
    widget, a websocket push — without any of those becoming a dependency
    of this repo. A callback that raises is a failed emission, exactly
    like a failed file write."""

    name = "callback"

    def __init__(self, callback: Callable[[Envelope], Any], *, name: str = "callback"):
        if not callable(callback):
            raise TypeError("CallbackSink needs a callable")
        self.callback = callback
        self.name = name

    def emit(self, envelope: Envelope) -> None:
        try:
            self.callback(envelope)
        except Exception as exc:
            raise SinkError(f"callback sink '{self.name}' failed: {exc}") from exc


#: Sink types a manifest may name. Deliberately closed: `callback` is not
#: here, because a manifest naming a Python callable to import would make
#: the deployment file an execution vector, and Action is the one role
#: whose whole job is to affect the world. Attach a CallbackSink in
#: process, where somebody has already decided to run this code.
SINK_TYPES = {"null", "stdout", "stderr", "file"}


def build_sink(config: Dict[str, Any]) -> Sink:
    """One sink from one manifest entry. Raises ValueError on anything
    unrecognised — a misspelled sink type must not silently become
    silence."""
    if not isinstance(config, dict):
        raise ValueError(f"sink config must be an object, got "
                         f"{type(config).__name__}")
    kind = str(config.get("type") or "").strip().lower()
    if kind not in SINK_TYPES:
        raise ValueError(f"unknown action sink type {kind!r}; "
                         f"valid: {sorted(SINK_TYPES)}")
    if kind == "null":
        return NullSink()
    if kind == "stdout":
        return StreamSink(sys.stdout)
    if kind == "stderr":
        return StreamSink(sys.stderr)
    path = config.get("path")
    if not path:
        raise ValueError("action sink type 'file' needs a 'path'")
    return FileSink(path)


def build_sinks(configs: Optional[List[Dict[str, Any]]]) -> List[Sink]:
    if not configs:
        return []
    if not isinstance(configs, list):
        raise ValueError("roles.action.sinks must be a list")
    return [build_sink(c) for c in configs]


__all__ = ["Sink", "SinkError", "NullSink", "StreamSink", "FileSink",
           "CallbackSink", "SINK_TYPES", "build_sink", "build_sinks"]
