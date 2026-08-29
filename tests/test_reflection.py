"""Reflection Agent + domain taxonomy (dispatch #4, 2026-08-29).

Reflection looks back over a batch of concluded events (Governance's
`_conclude()` fork, `events.reflection`) and produces at most one of:
a domain="internal" archive write, an Idea ping back through Sensory, or
silence. Domain is the new field under category/topic/... — "external"
(everything Consolidator writes) vs "internal" (Reflection's own).
"""
from __future__ import annotations

import json
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq
import pytest
import yaml

from agents.archive.store import ArchiveStore
from agents.archive.structured_store import DEFAULT_DOMAIN, SCHEMA, StructuredStore
from agents.reflection.agent import ReflectionMock
from agents.reflection.base import ReflectionBase
from agents.reflection.contract import ReflectionResult, parse
from agents.reflection.live import ReflectionAgent
from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from recovery.bootstrap import Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    LLMProvider,
    Substrate,
)
from substrates.registry import register_provider
from tests.conftest import assert_unusable_substrate_stops_bootstrap

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"


# ---------------------------------------------------------------------------
# domain in StructuredStore
# ---------------------------------------------------------------------------

class TestDomainInStructuredStore:
    def test_upsert_defaults_domain_to_external(self, tmp_path):
        store = StructuredStore(root=str(tmp_path / "archive"))
        store.upsert("knowledge", [{"category": "person", "topic": "family",
                                    "subtopic": "mother", "key": "name", "value": "Maria"}])
        rows = store.query("knowledge")
        assert len(rows) == 1
        assert rows[0]["domain"] == DEFAULT_DOMAIN == "external"

    def test_query_can_scope_to_one_domain(self, tmp_path):
        store = StructuredStore(root=str(tmp_path / "archive"))
        store.upsert("knowledge", [
            {"category": "person", "topic": "family", "key": "name", "value": "Maria"},
            {"domain": "internal", "category": "behavior", "topic": "reflection",
             "key": "pattern", "value": "escalates when tired"},
        ])
        assert len(store.query("knowledge", domain="external")) == 1
        assert len(store.query("knowledge", domain="internal")) == 1
        assert len(store.query("knowledge")) == 2

    def test_same_path_in_two_domains_does_not_collide(self, tmp_path):
        """domain is part of upsert's dedup key — an internal reflection
        and an external fact can share every other field without either
        overwriting the other."""
        store = StructuredStore(root=str(tmp_path / "archive"))
        store.upsert("knowledge", [{"category": "system", "topic": "identity",
                                    "subtopic": "persona", "key": "name", "value": "Morrow"}])
        store.upsert("knowledge", [{"domain": "internal", "category": "system",
                                    "topic": "identity", "subtopic": "persona",
                                    "key": "name", "value": "internal-note"}])
        assert len(store.query("knowledge")) == 2

    def test_schema_index_defaults_to_external_only(self, tmp_path):
        store = StructuredStore(root=str(tmp_path / "archive"))
        store.upsert("knowledge", [
            {"category": "person", "topic": "family", "key": "name", "value": "Maria"},
            {"domain": "internal", "category": "behavior", "topic": "reflection",
             "key": "pattern", "value": "x"},
        ])
        index = store.schema_index("knowledge")
        assert index == [{"category": "person", "topic": "family"}]


# ---------------------------------------------------------------------------
# tools/migrate_domain.py
# ---------------------------------------------------------------------------

class TestMigration:
    def test_migrates_a_pre_domain_file_to_external(self, tmp_path):
        from tools.migrate_domain import migrate

        old_schema = pa.schema([f for f in SCHEMA if f.name != "domain"])
        path = tmp_path / "knowledge.parquet"
        pq.write_table(pa.Table.from_pylist(
            [{"category": "person", "topic": "family", "subtopic": "mother",
              "subject": "", "key": "name", "value": "Maria",
              "written_at": "2026-08-29T00:00:00Z", "source": "consolidator"}],
            schema=old_schema), path)

        migrated = migrate(path)
        assert migrated == 1
        table = pq.read_table(path)
        assert "domain" in table.schema.names
        assert table.to_pylist()[0]["domain"] == "external"

    def test_rerunning_on_an_already_migrated_file_is_a_noop(self, tmp_path):
        from tools.migrate_domain import migrate

        store = StructuredStore(root=str(tmp_path / "archive"))
        store.upsert("knowledge", [{"category": "a", "topic": "b", "key": "c", "value": "d"}])
        path = tmp_path / "archive" / "structured" / "knowledge.parquet"
        assert migrate(path) == 0


# ---------------------------------------------------------------------------
# contract.parse — structural validation only
# ---------------------------------------------------------------------------

class TestContract:
    def test_write_needs_every_required_field(self):
        result = parse({"outcome": "write", "category": "behavior"})
        assert result.outcome == "silent"
        assert result.diagnostics["dropped_reason"]

    def test_a_complete_write_survives(self):
        result = parse({"outcome": "write", "category": "behavior", "topic": "bias",
                        "key": "urgency_overread", "value": "escalates when tired"})
        assert result.outcome == "write"
        assert result.write["value"] == "escalates when tired"
        assert result.write["subject"] == "this"  # no subject given -> forced fallback

    def test_idea_needs_text(self):
        assert parse({"outcome": "idea", "idea": ""}).outcome == "silent"
        assert parse({"outcome": "idea", "idea": "revisit X?"}).outcome == "idea"

    def test_unknown_outcome_reads_as_silent(self):
        assert parse({"outcome": "shrug"}).outcome == "silent"

    def test_not_a_dict_reads_as_silent(self):
        assert parse(None).outcome == "silent"
        assert parse(["not", "a", "dict"]).outcome == "silent"


# ---------------------------------------------------------------------------
# ReflectionBase — batching + applying outcomes
# ---------------------------------------------------------------------------

class _StubSensory:
    def __init__(self):
        self.ingested = []

    def ingest(self, content, source_type="prompt", triggered_by="sensory"):
        self.ingested.append((content, source_type, triggered_by))
        return "stub-event-id"


class _ScriptedReflection(ReflectionBase):
    """A ReflectionBase whose reflect() is scripted per test, so the
    batching/applying machinery in the base class is exercised without a
    substrate."""

    def __init__(self, *args, script=None, **kwargs):
        self._script = script or (lambda pending, prior: ReflectionResult(outcome="silent"))
        self.calls = []
        super().__init__(*args, **kwargs)

    def reflect(self, pending, prior_learnings):
        self.calls.append((list(pending), list(prior_learnings)))
        return self._script(pending, prior_learnings)


def _envelope(content: str = "hello") -> Envelope:
    return Envelope(source="Governance", destination="Reflection", type="Concluded",
                    content=content, event_id="e1",
                    meta={"final_proposal": "Hi!", "verdict": "green"})


class TestBatching:
    def test_does_not_reflect_until_batch_size_is_reached(self, tmp_path):
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, batch_size=3)
        bus.publish("events.reflection", _envelope())
        bus.publish("events.reflection", _envelope())
        assert agent.calls == []
        assert agent.metrics["events"] == 2

    def test_reflects_once_the_batch_fills_then_clears_it(self, tmp_path):
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, batch_size=2)
        bus.publish("events.reflection", _envelope("first"))
        bus.publish("events.reflection", _envelope("second"))
        assert len(agent.calls) == 1
        pending, _ = agent.calls[0]
        assert [p["sensory"] for p in pending] == ["first", "second"]
        assert agent._pending == []
        assert agent.metrics["passes"] == 1


class TestApplyingOutcomes:
    def test_write_outcome_upserts_domain_internal(self, tmp_path):
        store = StructuredStore(root=str(tmp_path / "archive"))
        write = {"category": "behavior", "topic": "bias", "subtopic": "general",
                 "subject": "this", "key": "urgency_overread", "value": "escalates when tired"}
        script = lambda pending, prior: ReflectionResult(outcome="write", write=write)
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, structured_store=store, batch_size=1, script=script)
        bus.publish("events.reflection", _envelope())

        rows = store.query("knowledge", domain="internal")
        assert len(rows) == 1
        assert rows[0]["source"] == "reflection"
        assert agent.metrics["writes"] == 1

    def test_idea_outcome_pings_sensory(self, tmp_path):
        sensory = _StubSensory()
        script = lambda pending, prior: ReflectionResult(outcome="idea", idea="revisit X?")
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, sensory=sensory, batch_size=1, script=script)
        bus.publish("events.reflection", _envelope())

        assert sensory.ingested == [("revisit X?", "idea", "Reflection")]
        assert agent.metrics["ideas"] == 1

    def test_idea_outcome_with_no_sensory_wired_is_a_safe_no_op(self, tmp_path):
        script = lambda pending, prior: ReflectionResult(outcome="idea", idea="revisit X?")
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, sensory=None, batch_size=1, script=script)
        bus.publish("events.reflection", _envelope())  # must not raise
        assert agent.metrics["ideas"] == 1

    def test_silent_outcome_writes_nothing_and_pings_nothing(self, tmp_path):
        store = StructuredStore(root=str(tmp_path / "archive"))
        sensory = _StubSensory()
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, structured_store=store, sensory=sensory, batch_size=1)
        bus.publish("events.reflection", _envelope())
        assert store.query("knowledge") == []
        assert sensory.ingested == []
        assert agent.metrics["silent"] == 1

    def test_on_reflect_hook_fires_for_every_pass(self, tmp_path):
        bus = EmbeddedBus()
        agent = _ScriptedReflection(bus, batch_size=1)
        seen = []
        agent.on_reflect = seen.append
        bus.publish("events.reflection", _envelope())
        assert len(seen) == 1
        assert seen[0].outcome == "silent"


class TestReflectionMock:
    def test_always_silent_zero_cost(self, tmp_path):
        bus = EmbeddedBus()
        agent = ReflectionMock(bus, batch_size=1)
        bus.publish("events.reflection", _envelope())
        assert agent.metrics["silent"] == 1
        assert agent.metrics["passes"] == 1


# ---------------------------------------------------------------------------
# ReflectionAgent — live tier
# ---------------------------------------------------------------------------

def _write_response(_prompt: str) -> str:
    return json.dumps({"outcome": "write", "category": "behavior", "topic": "bias",
                       "key": "urgency_overread", "value": "escalates when tired"})


def _idea_response(_prompt: str) -> str:
    return json.dumps({"outcome": "idea", "idea": "revisit stance on X?"})


def _silent_response(_prompt: str) -> str:
    return json.dumps({"outcome": "silent"})


RESPONDERS = {"write": _write_response, "idea": _idea_response, "silent": _silent_response}


class ScriptedReflectionProvider(LLMProvider):
    name = "scripted-reflection"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "silent")
        self.calls = []

    def validate_credentials(self) -> None:
        return

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 300, "output_tokens": 40})


register_provider(ScriptedReflectionProvider.name, ScriptedReflectionProvider)


def _scripted_substrate(mode: str) -> Substrate:
    return Substrate(substrate_class="reflection-scripted",
                     provider=ScriptedReflectionProvider(options={"mode": mode}),
                     model="scripted-reflection-v1", max_tokens=512)


class TestReflectionAgentLive:
    def test_write_outcome_reaches_the_store(self, tmp_path):
        store = StructuredStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus()
        agent = ReflectionAgent(bus, _scripted_substrate("write"),
                                structured_store=store, batch_size=1)
        bus.publish("events.reflection", _envelope())
        assert len(store.query("knowledge", domain="internal")) == 1

    def test_idea_outcome_reaches_sensory(self, tmp_path):
        sensory = _StubSensory()
        bus = EmbeddedBus()
        agent = ReflectionAgent(bus, _scripted_substrate("idea"),
                                sensory=sensory, batch_size=1)
        bus.publish("events.reflection", _envelope())
        assert sensory.ingested[0][1] == "idea"

    def test_an_outage_degrades_to_silent_and_never_raises(self, tmp_path):
        bus = EmbeddedBus()
        agent = ReflectionAgent(bus, _scripted_substrate("boom"), batch_size=1)
        bus.publish("events.reflection", _envelope())  # must not raise
        assert agent.metrics["fallbacks"] == 1

    def test_budget_mode_skips_the_substrate(self, tmp_path):
        class _Budget:
            def should_call_substrate(self):
                return False

        bus = EmbeddedBus()
        agent = ReflectionAgent(bus, _scripted_substrate("write"),
                                batch_size=1, budget=_Budget())
        bus.publish("events.reflection", _envelope())
        assert agent.metrics["llm_calls"] == 0
        assert agent.metrics["silent"] == 1


# ---------------------------------------------------------------------------
# Governance's _conclude() fork
# ---------------------------------------------------------------------------

class TestGovernanceForksToReflection:
    def test_conclude_publishes_the_finished_arc(self, tmp_path):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus(archive=archive)

        from agents.governance.agent import Governance
        governance = Governance(bus)
        bus.subscribe("events.reflection", lambda e: None)  # so publish doesn't no-op silently

        seen = []
        bus.subscribe("events.reflection", seen.append)

        # Drive one event through: worker report -> intent -> security -> action route.
        eid = "ev1"
        from agents.governance import routing
        bus.publish("events.governance", Envelope(
            source="Impulse", destination="Governance", type="Report",
            content="hi", event_id=eid, meta={"impulse": {}}))
        bus.publish("events.governance", Envelope(
            source="Analytics", destination="Governance", type="Report",
            content="hi", event_id=eid, meta={"analytics": {"recommendation": "k"}}))
        bus.publish("events.governance", Envelope(
            source="Personality", destination="Governance", type="Report",
            content="hi", event_id=eid, meta={"personality": {"findings": "f"}}))
        bus.publish("events.governance", Envelope(
            source="Intent", destination="Governance", type="Advise",
            content="Hello!", event_id=eid, meta={"proposed_action": "Hello!"}))
        bus.publish("events.governance", Envelope(
            source="Security", destination="Governance", type="Verdict",
            content="Green", event_id=eid))

        assert len(seen) == 1
        reflection_envelope = seen[0]
        assert reflection_envelope.meta["final_proposal"] == "Hello!"
        assert reflection_envelope.meta["verdict"] == "green"


# ---------------------------------------------------------------------------
# Bootstrap (dispatch #4)
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    manifest["roles"]["analytics"]["mock"] = True
    manifest["roles"]["intent"]["mock"] = True
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["consolidator"]["mock"] = True
    manifest["roles"]["reflection"] = {**manifest["roles"].get("reflection", {}),
                                       **role_overrides}
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


class TestBootstrap:
    def test_mock_true_selects_the_mock_tier(self, tmp_path):
        eco = Recovery(str(_manifest(tmp_path, mock=True))).bootstrap()
        assert eco.reflection.tier == "mock"

    def test_an_unusable_substrate_degrades_rather_than_stopping_the_boot(
            self, tmp_path, capsys):
        eco = Recovery(str(_manifest(tmp_path, mock=False, substrate="medium"))).bootstrap()
        assert eco.reflection.tier == "mock"
        assert "not usable" in capsys.readouterr().out

    def test_reflection_is_wired_to_governances_conclude_fork(self, tmp_path):
        eco = Recovery(str(_manifest(tmp_path, mock=True))).bootstrap()
        event_id = eco.sensory.ingest("hello there")
        hops = [e for e in eco.bus.trace()
                if e.event_id == event_id and e.destination == "Reflection"]
        assert len(hops) == 1
        assert hops[0].source == "Governance"
