"""
Phase 0.9 — Consolidator as a per-event fact writer.

Consolidator used to buffer concluded events (fed by Governance only
after Action ran) and reconcile them in batches, extracting facts and
distilling a narrative delta in one call. That's gone: Consolidator is
now a fan-out member wired exactly like Personality/Knowledge
(`agents/archive_lookup/base.py`'s shape) — it receives the raw Sensory
envelope directly, in parallel with Analytics/Personality, and writes
whatever that single event states immediately. No buffer, no batch
threshold, no epochs, no Impulse recalibration, and it never replies to
Governance.
"""
from __future__ import annotations

import json
from pathlib import Path

import yaml

from agents.archive.store import ArchiveStore
from agents.archive.structured_store import StructuredStore
from agents.consolidator.live import ConsolidatorAgent
from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from recovery.bootstrap import Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    LLMProvider,
)
from substrates.registry import register_provider

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"


# ---------------------------------------------------------------------------
# A scripted substrate
# ---------------------------------------------------------------------------

def _with_writes(_prompt: str) -> str:
    return json.dumps({
        "writes": [
            {"category": "person", "topic": "family", "subtopic": "mother",
             "key": "name", "value": "Maria"},
            {"category": "person", "topic": "relationship", "subtopic": "daughter",
             "key": "name", "value": "Susana"},
            {"category": "event", "topic": "schedule", "subtopic": "daughter",
             "key": "school_pickup_friday", "value": "12:00"},
        ],
    })


def _bad_writes(_prompt: str) -> str:
    return json.dumps({
        "writes": [
            {"category": "person", "topic": "family", "subtopic": "mother",
             "key": "name", "value": "kept"},
            {"category": "", "topic": "bad", "key": "x", "value": "dropped: no category"},
            {"category": "test", "topic": "bad", "key": "x", "value": ""},
            "not even an object",
        ],
    })


RESPONDERS = {
    "with_writes": _with_writes,
    "bad_writes": _bad_writes,
}


class ScriptedConsolidatorProvider(LLMProvider):
    name = "scripted-consolidator"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "with_writes")
        self.calls: list[CompletionRequest] = []

    def validate_credentials(self) -> None:
        return

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 400, "output_tokens": 60})


register_provider(ScriptedConsolidatorProvider.name, ScriptedConsolidatorProvider)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, mode: str = "with_writes", **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["consolidator-scripted"] = {
        "provider": ScriptedConsolidatorProvider.name,
        "model": "scripted-consolidator-v1",
        "api_key_env": None,
        "max_tokens": 512,
        "options": {"mode": mode},
    }
    # This suite is about CONSOLIDATOR; hold the rest of the live
    # pipeline deterministic so nothing else needs a credential.
    manifest["roles"]["analytics"]["mock"] = True
    manifest["roles"]["intent"]["mock"] = True
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["consolidator"]["mock"] = False
    manifest["roles"]["consolidator"]["substrate"] = "consolidator-scripted"
    manifest["roles"]["consolidator"].update(role_overrides)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, mode: str = "with_writes", **overrides):
    eco = Recovery(str(_manifest(tmp_path, mode, **overrides))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _event(content: str = "my daughter Susana gets picked up Fridays at 12:00") -> Envelope:
    return Envelope(source="Sensory", destination="Consolidator", type="prompt",
                    content=content)


# ---------------------------------------------------------------------------
# Per-event scope — no buffer, no batch
# ---------------------------------------------------------------------------

class TestPerEventScope:
    def test_a_single_event_is_written_immediately(self, tmp_path):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus(archive=archive)
        store = StructuredStore(root=str(tmp_path / "archive"))
        ConsolidatorAgent(bus, _scripted_substrate("with_writes"), archive,
                          structured_store=store)
        bus.publish("events.consolidator", _event())
        records = store.query("knowledge")
        assert len(records) == 3
        assert any(r["value"] == "Susana" for r in records)


# ---------------------------------------------------------------------------
# Multi-instruction writes
# ---------------------------------------------------------------------------

class TestMultiInstructionWrites:
    def test_one_pass_can_write_structured_records(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        eco.bus.publish("events.consolidator", _event())

        store = eco.consolidator.structured_store
        records = store.query("knowledge")
        assert len(records) == 3
        assert any(r["value"] == "Maria" for r in records)
        assert eco.consolidator.metrics["writes_executed"] == 3

    def test_malformed_instructions_are_dropped_never_guessed_at(self, tmp_path):
        eco = _boot(tmp_path, mode="bad_writes")
        eco.bus.publish("events.consolidator", _event())
        assert eco.consolidator.metrics["writes_executed"] == 1
        assert eco.consolidator.metrics["writes_dropped"] == 3

    def test_upsert_overwrites_existing_key(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        eco.bus.publish("events.consolidator", _event())
        eco.bus.publish("events.consolidator", _event())
        store = eco.consolidator.structured_store
        matches = [r for r in store.query("knowledge", category="person",
                                           topic="family", key="name")]
        assert len(matches) == 1
        assert matches[0]["value"] == "Maria"


# ---------------------------------------------------------------------------
# Fallback posture — fail open
# ---------------------------------------------------------------------------

class TestFallback:
    def test_an_outage_degrades_to_no_writes_and_never_raises(self, tmp_path):
        eco = _boot(tmp_path, mode="boom")
        eco.bus.publish("events.consolidator", _event())          # must not raise
        assert eco.consolidator.metrics["fallbacks"] == 1
        assert eco.consolidator.structured_store.query("knowledge") == []


# ---------------------------------------------------------------------------
# Budget mode
# ---------------------------------------------------------------------------

class TestBudgetMode:
    def test_budget_mode_skips_the_substrate_and_writes_nothing(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        eco.budget.switch_manual("budget")
        eco.bus.publish("events.consolidator", _event())
        assert eco.consolidator.metrics["llm_calls"] == 0
        assert eco.consolidator.structured_store.query("knowledge") == []


# ---------------------------------------------------------------------------
# Bootstrap (§9.1, §13.4)
# ---------------------------------------------------------------------------

class TestBootstrap:
    def test_mock_flag_false_selects_the_live_tier(self, tmp_path):
        eco = _boot(tmp_path)
        assert eco.consolidator.tier == "live"
        assert eco.consolidator.substrate.substrate_class == "consolidator-scripted"

    def test_an_unusable_substrate_degrades_rather_than_stopping_the_boot(
            self, tmp_path, capsys):
        eco = _boot(tmp_path, substrate="orthogonal")
        assert eco.consolidator.tier == "mock"
        assert eco.intent.tier == "mock"          # live pipeline unaffected
        assert "not usable" in capsys.readouterr().out

    def test_consolidator_is_wired_into_sensorys_fan_out(self, tmp_path):
        eco = _boot(tmp_path)
        eco.sensory.ingest("my daughter Susana gets picked up Fridays at 12:00")
        assert eco.consolidator.metrics["events"] == 1


def _scripted_substrate(mode: str):
    """A bare ScriptedConsolidatorProvider-backed Substrate, for the
    standalone (non-bootstrap) construction test."""
    from substrates.base import Substrate

    return Substrate(
        substrate_class="consolidator-scripted",
        provider=ScriptedConsolidatorProvider(options={"mode": mode}),
        model="scripted-consolidator-v1",
        max_tokens=512,
    )
