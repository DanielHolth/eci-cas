"""
Phase 0.6 — Action going live (§5.7, §13.4's last mock).

`ActionMock` appended to a list. That was the right Phase 0 stand-in and
it is still the right zero-cost one, but it meant nothing this system
decided to say ever left the process.

The thing this suite guards hardest is not the plumbing — it's the role
invariant. Action "executes exactly what Governance hands it and authors
nothing of its own". Sinks are where that invariant is most tempting to
break (a prefix here, a friendly error message there), so several tests
below exist purely to make breaking it fail loudly.
"""
from __future__ import annotations

import io
import json
from pathlib import Path

import pytest
import yaml

from agents.action.agent import ActionAgent, ActionMock
from agents.action.sinks import (
    CallbackSink,
    FileSink,
    NullSink,
    SinkError,
    StreamSink,
    build_sink,
)
from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from recovery.bootstrap import BootstrapError, Recovery

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"


def action_envelope(content="hello there", **kwargs):
    meta = kwargs.pop("meta", {})
    return Envelope(source="Governance", destination="Action",
                    type=kwargs.pop("type", "Prompt"), content=content,
                    meta=meta, **kwargs)


# ---------------------------------------------------------------------------
# Sinks
# ---------------------------------------------------------------------------

class TestSinks:
    def test_null_sink_records_without_emitting(self):
        sink = NullSink()
        envelope = action_envelope()
        sink.emit(envelope)
        assert sink.emitted == [envelope]

    def test_stream_sink_writes_the_content(self):
        stream = io.StringIO()
        StreamSink(stream).emit(action_envelope("good morning"))
        assert "good morning" in stream.getvalue()

    def test_stream_sink_failure_becomes_a_sink_error(self):
        class Broken:
            def write(self, _):
                raise OSError("device gone")

        with pytest.raises(SinkError):
            StreamSink(Broken()).emit(action_envelope())

    def test_file_sink_appends_one_json_object_per_action(self, tmp_path):
        path = tmp_path / "spoken.jsonl"
        sink = FileSink(path)
        sink.emit(action_envelope("first"))
        sink.emit(action_envelope("second"))

        lines = [json.loads(l) for l in path.read_text().splitlines() if l.strip()]
        assert [r["content"] for r in lines] == ["first", "second"]

    def test_callback_sink_hands_over_the_envelope(self):
        seen = []
        CallbackSink(seen.append).emit(action_envelope("for the avatar"))
        assert seen[0].content == "for the avatar"

    def test_callback_sink_refuses_a_non_callable(self):
        with pytest.raises(TypeError):
            CallbackSink("not callable")


class TestSinkConfig:
    def test_it_builds_each_known_type(self, tmp_path):
        assert build_sink({"type": "null"}).name == "null"
        assert build_sink({"type": "stdout"}).name == "stream"
        assert build_sink({"type": "file",
                           "path": str(tmp_path / "x.jsonl")}).name == "file"

    def test_an_unknown_type_raises_rather_than_becoming_silence(self):
        with pytest.raises(ValueError) as exc:
            build_sink({"type": "stdoutt"})
        assert "unknown action sink type" in str(exc.value)

    def test_callback_is_not_manifest_configurable(self):
        """A manifest naming a Python callable to import would make the
        deployment file an execution vector — in the one role whose job
        is to affect the world."""
        with pytest.raises(ValueError):
            build_sink({"type": "callback", "target": "os.system"})


# ---------------------------------------------------------------------------
# The agent
# ---------------------------------------------------------------------------

class TestActionAgent:
    def _agent(self, *sinks):
        bus = EmbeddedBus()
        reports = []
        bus.subscribe("events.governance", reports.append)
        return ActionAgent(bus, list(sinks)), reports

    def test_it_emits_through_every_sink(self):
        a, b = NullSink(), NullSink()
        agent, _ = self._agent(a, b)
        agent.bus.publish("events.action", action_envelope("shared"))
        assert len(a.emitted) == 1 and len(b.emitted) == 1

    def test_it_is_silent_on_success(self):
        """v0.33's contract: no envelope, no re-entry."""
        agent, reports = self._agent(NullSink())
        agent.bus.publish("events.action", action_envelope())
        assert reports == []

    def test_a_failing_sink_reports_to_governance(self):
        def explode(_):
            raise RuntimeError("offline")

        agent, reports = self._agent(CallbackSink(explode))
        agent.bus.publish("events.action", action_envelope())
        assert len(reports) == 1
        assert reports[0].type == "Failure"
        assert reports[0].destination == "Governance"

    def test_the_failure_report_carries_the_original_content(self):
        """Not an error message. Governance's fallback quotes this to the
        human through Intent — putting a stack trace here would put
        Action's words in the persona's mouth."""
        def explode(_):
            raise RuntimeError("offline")

        agent, reports = self._agent(CallbackSink(explode))
        agent.bus.publish("events.action", action_envelope("what it meant to say"))
        assert reports[0].content == "what it meant to say"

    def test_one_broken_sink_does_not_stop_the_others(self):
        """Half-delivered is the honest outcome, and reporting it is what
        lets the human hear about it through the channel that works."""
        def explode(_):
            raise RuntimeError("offline")

        good = NullSink()
        agent, reports = self._agent(CallbackSink(explode), good)
        agent.bus.publish("events.action", action_envelope())
        assert len(good.emitted) == 1
        assert len(reports) == 1

    def test_it_authors_nothing(self):
        """The invariant, stated as a test. A sink receives the envelope
        and emits it; it is never handed the pipeline's reasoning, so
        there is nothing here to author with."""
        captured = []
        agent, _ = self._agent(CallbackSink(captured.append))
        original = action_envelope("exactly this")
        agent.bus.publish("events.action", original)
        assert captured[0].content == original.content
        assert captured[0].event_id == original.event_id


# ---------------------------------------------------------------------------
# Bootstrap
# ---------------------------------------------------------------------------

class TestBootstrap:
    def _manifest(self, tmp_path, **action):
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["roles"]["action"] = {"tier": "deterministic", **action}
        for role in ("analytics", "intent", "consolidator",
                     "personality", "knowledge"):
            if role in manifest["roles"]:
                manifest["roles"][role]["mock"] = True
        path = tmp_path / "manifest.yaml"
        path.write_text(yaml.safe_dump(manifest))
        return str(path)

    def test_the_shipped_manifest_boots_action_live(self, tmp_path):
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        assert manifest["roles"]["action"]["mock"] is False

    def test_mock_true_still_gives_the_mock(self, tmp_path):
        eco = Recovery(self._manifest(tmp_path, mock=True)).bootstrap()
        assert isinstance(eco.action, ActionMock)
        assert not isinstance(eco.action, ActionAgent)

    def test_it_builds_the_configured_sinks(self, tmp_path):
        eco = Recovery(self._manifest(
            tmp_path, mock=False,
            sinks=[{"type": "null"}, {"type": "file", "path": "spoken.jsonl"}]
        )).bootstrap()
        assert [s.name for s in eco.action.sinks] == ["null", "file"]

    def test_an_unknown_sink_type_stops_the_bootstrap(self, tmp_path):
        with pytest.raises(BootstrapError):
            Recovery(self._manifest(
                tmp_path, mock=False, sinks=[{"type": "telepathy"}])).bootstrap()


# ---------------------------------------------------------------------------
# End to end
# ---------------------------------------------------------------------------

class TestEndToEnd:
    def test_a_prompt_reaches_the_transcript(self, tmp_path):
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["roles"]["action"] = {
            "tier": "deterministic", "mock": False,
            "sinks": [{"type": "file", "path": "spoken.jsonl"}]}
        for role in ("analytics", "intent", "consolidator",
                     "personality", "knowledge"):
            if role in manifest["roles"]:
                manifest["roles"][role]["mock"] = True
        path = tmp_path / "manifest.yaml"
        path.write_text(yaml.safe_dump(manifest))

        eco = Recovery(str(path)).bootstrap()
        eco.sensory.ingest("Good morning", source_type="prompt")

        transcript = Path(eco.action.sinks[0].path)
        assert transcript.exists()
        records = [json.loads(l) for l in transcript.read_text().splitlines() if l.strip()]
        assert records, "nothing this system said reached the world"
