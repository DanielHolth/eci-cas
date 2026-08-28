"""
Phase 0.2 test harness — substrate-backed Analytics (§5.4, §13.4).

Offline and free, through a scripted provider registered into the real
substrate registry, so these exercise the actual manifest → registry →
agent path rather than a bypass of it.

The scripted provider is not an attempt to simulate a model. It pins down
what Analytics does with each *shape* of answer one can give. Analytics
now returns plain text (keywords on the first line, paths on subsequent
lines), and degradation is tested via substrate failures (boom) and
empty responses.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from bus.envelope import VERDICT_GREEN, VERDICT_RED, VERDICT_YELLOW, Envelope
from recovery.bootstrap import BootstrapError, Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    CredentialsError,
    LLMProvider,
)
from substrates.parsing import coerce_bool, extract_json_object
from substrates.registry import register_provider

from agents.analytics import contract
from agents.analytics.base import LOOP_THRESHOLD, ROLLING_WINDOW
from agents.analytics.contract import ContractViolation, Task

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"
PROPOSED = "Hey there! I'm awake."


# ---------------------------------------------------------------------------
# A scripted provider — one per response shape a model might produce
# ---------------------------------------------------------------------------

def _reply_correct(prompt: str) -> str:
    return ("responsive, warm acknowledgement, greeting\n"
            "person/identity\n"
            "person/preferences")


def _reply_persona_speech(prompt: str) -> str:
    return ("Hey! Yes, I'm wide awake and happy to chat!\n"
            "person/identity")


def _reply_empty(prompt: str) -> str:
    return ""


def _reply_essay(prompt: str) -> str:
    return "x" * (contract.MAX_RECOMMENDATION_CHARS + 500)


RESPONDERS = {
    "correct": _reply_correct,
    "persona_speech": _reply_persona_speech,
    "empty": _reply_empty,
    "essay": _reply_essay,
}


class ScriptedProvider(LLMProvider):
    """Offline provider whose reply shape is chosen by manifest options."""

    name = "scripted-analytics"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "correct")
        self.calls: list[CompletionRequest] = []

    def validate_credentials(self) -> None:
        if self.options.get("fail_credentials"):
            raise CredentialsError("scripted provider configured to fail validation")

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 120, "output_tokens": 40})


register_provider(ScriptedProvider.name, ScriptedProvider)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, mode: str = "correct", **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["fast-reflex"] = {
        "provider": ScriptedProvider.name,
        "model": "scripted-reasoner-v1",
        "api_key_env": None,
        "max_tokens": 256,
        "options": {"mode": mode},
    }
    manifest["roles"]["analytics"]["mock"] = False
    manifest["roles"]["analytics"].update(role_overrides)
    manifest["roles"]["intent"]["mock"] = True
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["knowledge"]["mock"] = True
    manifest["roles"]["consolidator"]["mock"] = True
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, mode: str = "correct", **overrides):
    eco = Recovery(str(_manifest(tmp_path, mode, **overrides))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _verdict(eco, verdict, content="Verdict", proposed=PROPOSED):
    env = Envelope(source="Security", destination="Governance", type="Verdict",
                   content=content, meta={"verdict": verdict, "proposed_action": proposed})
    eco.bus.publish("events.governance", env)
    return env.event_id


def _typed_hops(eco, event_id):
    return [(e.source, e.destination, e.type) for e in eco.bus.trace()
            if e.event_id == event_id]


def _spoken(eco, event_id):
    """What Action was actually handed."""
    return [str(e.content) for e in eco.bus.trace()
            if e.event_id == event_id and e.destination == "Action"]


def _envelope(source="Governance", type="Evaluate", content=PROMPT, **meta):
    return Envelope(source=source, destination="Analytics", type=type,
                    content=content, meta=meta)


# ---------------------------------------------------------------------------
# The output contract
# ---------------------------------------------------------------------------

class TestContract:
    def test_a_well_formed_answer_parses(self):
        rec = contract.parse(_reply_correct(""), Task.EVALUATE)
        assert "responsive" in rec.recommendation
        assert rec.decided_by == "llm"
        assert len(rec.knowledge_paths) == 2
        assert rec.knowledge_paths[0] == {"category": "person", "topic": "identity"}

    @pytest.mark.parametrize("raw,expected", [
        (True, True), ("FALSE", False),
    ])
    def test_booleans_are_read_the_way_models_spell_them(self, raw, expected):
        assert coerce_bool(raw, default=not expected) is expected

    def test_empty_response_raises(self):
        with pytest.raises(ContractViolation):
            contract.parse("", Task.EVALUATE)

    # ---- the fallback -------------------------------------------

    def test_evaluate_degrades_and_keeps_moving(self):
        env = _envelope()
        rec = contract.fallback(env, Task.EVALUATE, "substrate down")
        assert rec.recommendation == contract.templated_recommendation(env)
        assert rec.diagnostics["degraded"] is True

    def test_there_are_no_gating_tasks_left_here(self):
        assert [t.value for t in Task] == ["Evaluate"]
        assert not hasattr(contract, "GATING_TASKS")
        assert not hasattr(contract, "FAIL_CLOSED_TASKS")
        assert not hasattr(contract.Recommendation, "proceed")
        assert not hasattr(contract.Recommendation, "concern")

    def test_unknown_message_types_are_not_tasks(self):
        assert Task.from_envelope(_envelope(type="LoopCheck")) is None
        for retired in ("Review", "Revise"):
            assert Task.from_envelope(_envelope(type=retired)) is None

    def test_every_sensory_modality_reads_as_evaluate(self):
        assert Task.from_envelope(_envelope(type="prompt")) is Task.EVALUATE


# ---------------------------------------------------------------------------
# What never reaches the substrate
# ---------------------------------------------------------------------------

class TestMechanicalWorkIsFree:
    def test_a_detected_loop_costs_nothing_and_is_flagged(self, tmp_path):
        eco = _boot(tmp_path)
        for _ in range(LOOP_THRESHOLD):
            eco.bus.publish("events.analytics", _envelope(content="same thing"))

        assert eco.analytics.metrics["loops_detected"] == 1
        assert eco.analytics.metrics["llm_calls"] == LOOP_THRESHOLD - 1
        last = [e for e in eco.bus.trace() if e.source == "Analytics"][-1]
        assert last.destination == "Governance"
        assert last.meta["analytics"]["loop_detected"] is True

    def test_the_control_plane_never_calls_a_model(self, tmp_path):
        eco = _boot(tmp_path)
        eco.sensory.inject_diagnostic_ping("SystemCheck")

        assert eco.analytics.metrics["llm_calls"] == 0
        types_seen = [(e.source, e.destination, e.type) for e in eco.bus.trace()]
        assert ("Analytics", "Recovery", "SystemCheckAck") in types_seen

    def test_an_unknown_message_type_is_dropped_not_guessed(self, tmp_path):
        eco = _boot(tmp_path)
        eco.bus.publish("events.analytics", _envelope(type="Nonsense"))
        assert eco.analytics.metrics["llm_calls"] == 0
        assert not [e for e in eco.bus.trace() if e.source == "Analytics"]


# ---------------------------------------------------------------------------
# The pipeline
# ---------------------------------------------------------------------------

class TestPipeline:
    def test_the_worked_example_still_traverses(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        hops = [(e.source, e.destination) for e in eco.bus.trace()
                if e.event_id == event_id]
        assert ("Sensory", "Analytics") in hops
        assert ("Analytics", "Governance") in hops
        assert ("Governance", "Intent") in hops
        assert ("Intent", "Governance") in hops
        assert ("Governance", "Security") in hops
        assert ("Security", "Governance") in hops
        assert ("Governance", "Action") in hops
        assert eco.analytics.metrics["llm_calls"] == 1

    def test_analytics_reports_to_governance_and_never_speaks(self, tmp_path):
        eco = _boot(tmp_path, mode="persona_speech")
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        analytics_out = [e for e in eco.bus.trace()
                         if e.event_id == event_id and e.source == "Analytics"][0]
        assert analytics_out.destination == "Governance"
        assert analytics_out.type == "Recommend"
        assert "wide awake and happy to chat" not in _spoken(eco, event_id)[0]

    def test_analytics_never_hears_from_security_at_all(self, tmp_path):
        eco = _boot(tmp_path)
        for verdict in (VERDICT_YELLOW, VERDICT_RED):
            eco.bus.reset_trace()
            event_id = _verdict(eco, verdict)
            inbound = [e for e in eco.bus.trace()
                       if e.event_id == event_id and e.destination == "Analytics"]
            assert inbound == []

    def test_judgments_are_attributed_in_the_queue_log(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        logged = eco.archive.query_queue(
            predicate=lambda r: r.get("event_id") == event_id
                                and r.get("source") == "Analytics")
        assert logged
        analytics_meta = logged[0]["meta"]["analytics"]
        assert analytics_meta["tier"] == "live"
        assert analytics_meta["decided_by"] == "llm"
        assert analytics_meta["source_substrate"] == "fast-reflex"
        assert analytics_meta["source_model"] == "scripted-reasoner-v1"
        assert analytics_meta["usage"]["input_tokens"] == 120


# ---------------------------------------------------------------------------
# Degradation
# ---------------------------------------------------------------------------

class TestDegradation:
    @pytest.mark.parametrize("mode", ["boom", "empty"])
    def test_an_event_survives_a_bad_substrate(self, tmp_path, mode):
        eco = _boot(tmp_path / mode, mode=mode)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        assert eco.analytics.metrics["fallbacks"] == 1
        assert len(eco.action.executed) == 1
        analytics_out = [e for e in eco.bus.trace()
                         if e.event_id == event_id and e.source == "Analytics"][0]
        assert analytics_out.meta["analytics"]["degraded"] is True
        spoken = _spoken(eco, event_id)
        assert spoken
        assert "CompletionError" not in spoken[0]

    def test_strict_mode_surfaces_the_failure_instead(self, tmp_path):
        eco = _boot(tmp_path, mode="empty", strict=True)
        with pytest.raises(ContractViolation):
            eco.sensory.ingest(PROMPT, source_type="prompt")


# ---------------------------------------------------------------------------
# The prompt
# ---------------------------------------------------------------------------

class TestPrompt:
    def _prompt_for(self, eco):
        return eco.analytics.substrate.provider.calls[-1]

    def test_the_prompt_carries_the_task_and_the_event(self, tmp_path):
        eco = _boot(tmp_path)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        user = self._prompt_for(eco).user
        assert "TASK: Evaluate" in user
        assert PROMPT in user

    def test_the_prompt_does_not_grow_without_bound(self, tmp_path):
        eco = _boot(tmp_path)
        for i in range(ROLLING_WINDOW * 3):
            eco.sensory.ingest(f"event number {i}", source_type="prompt")

        calls = eco.analytics.substrate.provider.calls
        early = len(calls[ROLLING_WINDOW].user)
        late = len(calls[-1].user)
        assert late < early * 1.5, f"prompt grew from {early} to {late} chars"

    def test_the_system_instruction_comes_from_the_manifest(self, tmp_path):
        eco = _boot(tmp_path)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        assert "You are ANALYTICS" in self._prompt_for(eco).system
        assert "First line" in self._prompt_for(eco).system


# ---------------------------------------------------------------------------
# Vendor independence (§10.2)
# ---------------------------------------------------------------------------

class TestVendorIndependence:
    def test_swapping_the_vendor_is_a_manifest_edit(self, tmp_path):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"
        manifest["substrates"]["fast-reflex"] = {
            "provider": "echo", "model": "some-other-vendor-model",
            "options": {"script": [_reply_correct("")]},
        }
        manifest["roles"]["analytics"]["mock"] = False
        manifest["roles"]["intent"]["mock"] = True
        manifest["roles"]["personality"]["mock"] = True
        manifest["roles"]["knowledge"]["mock"] = True
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        eco = Recovery(str(path)).bootstrap()
        eco.bus.reset_trace()
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        assert eco.analytics.substrate.provider_name == "echo"
        analytics_out = [e for e in eco.bus.trace()
                         if e.event_id == event_id and e.source == "Analytics"][0]
        assert analytics_out.meta["analytics"]["source_model"] == "some-other-vendor-model"
        assert analytics_out.meta["analytics"]["decided_by"] == "llm"


# ---------------------------------------------------------------------------
# Bootstrap
# ---------------------------------------------------------------------------

class TestBootstrap:
    def test_mock_flag_selects_the_matching_tier(self, tmp_path):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["roles"]["analytics"]["mock"] = True
        manifest["roles"]["intent"]["mock"] = True
        manifest["roles"]["personality"]["mock"] = True
        manifest["roles"]["knowledge"]["mock"] = True
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        eco = Recovery(str(path)).bootstrap()
        assert eco.analytics.tier == "mock"

        eco = Recovery(str(_manifest(tmp_path))).bootstrap()
        assert eco.analytics.tier == "live"
        assert eco.analytics.substrate.substrate_class == "fast-reflex"

    def test_an_unusable_substrate_stops_the_bootstrap(self, tmp_path, monkeypatch):
        monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
        monkeypatch.delenv("OPENAI_API_KEY", raising=False)
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        with pytest.raises(BootstrapError, match="substrate is not usable"):
            Recovery(str(path)).bootstrap()


# ---------------------------------------------------------------------------
# Shared parsing helper
# ---------------------------------------------------------------------------

class TestJsonExtraction:
    @pytest.mark.parametrize("text", [
        '{"recommendation": "x"}',
        'prose {"recommendation": "x"} more prose',
    ])
    def test_finds_the_object_in_realistic_noise(self, text):
        assert extract_json_object(text)["recommendation"]

    @pytest.mark.parametrize("text", ["", "no json here"])
    def test_returns_none_when_there_is_no_object(self, text):
        assert extract_json_object(text) is None
