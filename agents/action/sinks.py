"""
Action's output sinks (§5.7).

A sink receives the content Governance authorised and emits it. It is
never handed the pipeline's reasoning — only the text and envelope
metadata. A sink signals failure by raising; ActionAgent catches it
and reports to Governance.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

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
    filter a bus trace to find out.

    Date-partitioned like Archive's queue log (`agents/archive/store.py`'s
    `log_event`): `self.path` is a base (directory + stem), and the actual
    file written to is recomputed per emit() as `<stem>_<date><suffix>`, so
    the file rolls over at midnight without needing a restart."""

    name = "file"

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)

    def _dated_path(self) -> Path:
        date = time.strftime("%Y-%m-%d", time.gmtime())
        return self.path.with_name(f"{self.path.stem}_{date}{self.path.suffix}")

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
        dated_path = self._dated_path()
        try:
            with dated_path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(record, ensure_ascii=False) + "\n")
        except OSError as exc:
            raise SinkError(f"transcript write failed ({dated_path}): {exc}") from exc


#: Sink types a manifest may name. Deliberately closed to these: a
#: manifest naming a Python callable to import would make the deployment
#: file an execution vector, and Action is the one role whose whole job
#: is to affect the world.
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
           "SINK_TYPES", "build_sink", "build_sinks"]
