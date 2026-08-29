"""
Phase 0.9 — Consolidator as a per-event fact writer.

Consolidator used to buffer concluded events (fed by Governance only
after Action ran) and reconcile them in batches, extracting facts and
distilling a narrative delta in one call. That's gone, and it writes
whatever a single event states immediately: no buffer, no batch
threshold, no epochs, no Impulse recalibration, and it never replies to
Governance.

It briefly ran as a fan-out member wired like Personality (reading the
raw Sensory envelope directly, blind to everything else the ecosystem
already knew). As of 2026-08-29 it's wired to Governance's BUNDLE route
instead (`agents/governance/agent.py`'s emit()) — the same evidence
Intent reasons over, including whatever the knowledge swarm already
retrieved as relevant to this event — so it can reuse an existing
subtopic/subject when this event's fact matches one, instead of
guessing at a consistent spelling blind.
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
# Reuse cue — the swarm's per-event retrieval, forked from Governance
# ---------------------------------------------------------------------------

class TestAlreadyKnown:
    """Consolidator no longer carries a hardcoded rulebook for staying
    consistent (there was one, briefly, for this system's own agents —
    removed 2026-08-29: it fixed that one closed set and did nothing for
    the open-ended one, a user's own family/job/hobbies). The general
    mechanism is meta["knowledge_swarm"] — Governance's per-event,
    relevance-bounded retrieval, forked to Consolidator's envelope
    unchanged from what Intent gets. These tests pin the plumbing, not
    the judgment call itself (that needs a real model)."""

    def test_the_prompt_surfaces_whatever_the_swarm_already_found(self, tmp_path):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus(archive=archive)
        store = StructuredStore(root=str(tmp_path / "archive"))
        agent = ConsolidatorAgent(bus, _scripted_substrate("with_writes"), archive,
                                   structured_store=store)
        envelope = Envelope(source="Governance", destination="Consolidator",
                            type="Bundle", content="what's my wife's name again?",
                            meta={"knowledge_swarm":
                                  "person/relationship/wife/Yahnessa: marriage_date = 07.03.2004"})
        prompt = agent._prompt(envelope)
        assert "ALREADY KNOWN" in prompt
        assert "wife/Yahnessa" in prompt

    def test_an_empty_swarm_reads_as_nothing_on_file_not_a_blank(self, tmp_path):
        """A missing/empty meta must not render as silence the model could
        mistake for "check elsewhere" — it should read as an explicit
        "there is nothing to reuse here, invent freely"."""
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus(archive=archive)
        agent = ConsolidatorAgent(bus, _scripted_substrate("with_writes"), archive)
        prompt = agent._prompt(_event())
        assert "(nothing on file yet)" in prompt


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
# Doodle backend — control-plane notification, event-level dedup
# (docs/ideas/consolidation-doodle.md)
# ---------------------------------------------------------------------------

def _click_event(ref_event_id: str, event_id: str) -> Envelope:
    return Envelope(source="Governance", destination="Consolidator", type="Bundle",
                    content="the user looked at what was just learned",
                    event_id=event_id,
                    meta={"source_type": "ui_click", "ref_event_id": ref_event_id})


class TestConsolidationWrittenNotification:
    def test_a_write_pass_that_writes_something_publishes_on_control(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        notices = []
        eco.bus.subscribe("system.control", notices.append)
        event_id = eco.sensory.ingest(
            "my daughter Susana gets picked up Fridays at 12:00")

        written = [e for e in notices if e.type == "ConsolidationWritten"]
        assert len(written) == 1
        assert written[0].meta["event_id"] == event_id
        assert "Susana" in written[0].content

    def test_an_empty_write_pass_publishes_nothing(self, tmp_path):
        """A pass that writes nothing (substrate outage, e.g.) has nothing
        for the doodle to show — no notice, per the module docstring's
        'skip if records is empty' rule."""
        eco = _boot(tmp_path, mode="boom")
        notices = []
        eco.bus.subscribe("system.control", notices.append)
        eco.bus.publish("events.consolidator", _event())
        assert [e for e in notices if e.type == "ConsolidationWritten"] == []


class TestDoodleDedup:
    def test_the_first_click_on_a_pass_runs_normally(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        eco.bus.publish("events.consolidator", _click_event("pass-1", "click-1"))
        assert eco.consolidator.metrics["events"] == 1
        assert eco.consolidator.substrate.provider.calls  # the substrate WAS called

    def test_a_repeat_click_on_the_same_pass_is_a_no_op(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        eco.bus.publish("events.consolidator", _click_event("pass-1", "click-1"))
        calls_after_first = len(eco.consolidator.substrate.provider.calls)

        eco.bus.publish("events.consolidator", _click_event("pass-1", "click-2"))
        assert len(eco.consolidator.substrate.provider.calls) == calls_after_first
        assert eco.consolidator.metrics["events"] == 2  # still counted as received

    def test_a_click_on_a_different_pass_is_not_deduped(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes")
        eco.bus.publish("events.consolidator", _click_event("pass-1", "click-1"))
        calls_after_first = len(eco.consolidator.substrate.provider.calls)

        eco.bus.publish("events.consolidator", _click_event("pass-2", "click-2"))
        assert len(eco.consolidator.substrate.provider.calls) > calls_after_first


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
        eco = _boot(tmp_path, substrate="medium")
        assert eco.consolidator.tier == "mock"
        assert eco.intent.tier == "mock"          # live pipeline unaffected
        assert "not usable" in capsys.readouterr().out

    def test_consolidator_is_wired_into_governances_bundle_fork(self, tmp_path):
        """Not Sensory's fan-out any more (2026-08-29) — Governance forks
        its BUNDLE route to Consolidator alongside the copy Intent gets,
        so both reason over the same evidence for this event."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(
            "my daughter Susana gets picked up Fridays at 12:00")
        assert eco.consolidator.metrics["events"] == 1

        hops = [e for e in eco.bus.trace()
                if e.event_id == event_id and e.destination == "Consolidator"]
        assert len(hops) == 1
        assert hops[0].source == "Governance"


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
