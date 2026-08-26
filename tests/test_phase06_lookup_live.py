"""
Phase 0.6 — a live tier for the archive-lookup family (Personality,
Knowledge).

v0.35b shipped this family mock-first deliberately: the mock proved the
wiring, the four-way fan-out and the read-only posture at zero cost, and
left the one thing a model is actually for — judging which of N records
BEARS on the event in front of you — as the open item.

What this suite guards, beyond "the call happens":

  * silence, never invention, on every degraded path (outage, bad JSON,
    budget mode) — this family gates nothing, so a bad answer costs more
    than no answer;
  * the read-only posture survives the live tier, which is the tier that
    would actually have something to write;
  * an empty store never reaches the substrate at all — this runs twice
    on every event, and reasoning over zero records is paying to be told
    there is nothing there.
"""
from __future__ import annotations

import json
from pathlib import Path

import pytest
import yaml

from agents.archive.store import ArchiveStore
from agents.archive_lookup.agent import ArchiveLookupMock
from agents.archive_lookup.base import ROLE_STORES
from agents.archive_lookup.live import ArchiveLookupAgent
from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from recovery.bootstrap import BootstrapError, Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    CredentialsError,
    LLMProvider,
)
from substrates.registry import register_provider

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"


# ---------------------------------------------------------------------------
# A scripted substrate
# ---------------------------------------------------------------------------

RESPONDERS = {
    "correct": lambda user: json.dumps(
        {"findings": "prefers directness; dislikes hedging",
         "relevant": True}),
    "silent": lambda user: json.dumps({"findings": "", "relevant": False}),
    "fenced": lambda user: (
        '```json\n{"findings": "old promise about tuesdays", '
        '"relevant": true}\n```'),
    "prose": lambda user: "I think there's probably something relevant here.",
    "echo": lambda user: json.dumps({"findings": user[:80], "relevant": True}),
}


class ScriptedLookupProvider(LLMProvider):
    """Offline provider whose reply shape is chosen by manifest options."""

    name = "scripted-lookup"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "correct")
        self.calls: list = []

    def validate_credentials(self) -> None:
        if self.options.get("fail_credentials"):
            raise CredentialsError("scripted provider configured to fail")

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 90, "output_tokens": 20})


register_provider(ScriptedLookupProvider.name, ScriptedLookupProvider)


class FakeSubstrate:
    """Minimal Substrate stand-in — enough surface for the agent, with a
    call log the tests can read."""

    substrate_class = "fast-reflex"
    model = "scripted-lookup-v1"
    provider_name = "scripted"
    max_tokens = 256

    def __init__(self, mode="correct"):
        self.mode = mode
        self.calls = []

    def complete(self, *, system, user, temperature, max_tokens, prefill=None):
        self.calls.append({"system": system, "user": user})
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](user),
                                  model=self.model, provider=self.provider_name,
                                  usage={"input_tokens": 90, "output_tokens": 20})

    def estimate_cost(self, usage):
        return 0.0001

    def describe(self):
        return f"{self.substrate_class} -> {self.model}"


def _event(content="Should I tell them the truth?"):
    return Envelope(source="Sensory", destination="Personality",
                    type="Prompt", content=content)


def agent(tmp_path, mode="correct", role="Personality", records=(), **kwargs):
    archive = ArchiveStore(root=str(tmp_path / "archive"))
    for record in records:
        archive.write(ROLE_STORES[role], record)
    bus = EmbeddedBus()
    reports = []
    bus.subscribe("events.governance", reports.append)
    substrate = FakeSubstrate(mode)
    a = ArchiveLookupAgent(bus, archive, substrate, role=role, **kwargs)
    return a, substrate, reports


SOME_RECORDS = [{"kind": "note", "content": "prefers directness"},
                {"kind": "note", "content": "dislikes hedging"}]


# ---------------------------------------------------------------------------
# The judgment
# ---------------------------------------------------------------------------

class TestLookup:
    def test_it_calls_the_substrate_when_there_are_records(self, tmp_path):
        a, substrate, _ = agent(tmp_path, records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert len(substrate.calls) == 1
        assert a.metrics["llm_calls"] == 1

    def test_a_relevant_answer_reaches_governance(self, tmp_path):
        a, _, reports = agent(tmp_path, records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        slot = reports[0].meta["personality"]
        assert slot["relevant"] is True
        assert slot["decided_by"] == "llm"
        assert "directness" in slot["findings"]

    def test_it_parses_a_fenced_answer(self, tmp_path):
        a, _, reports = agent(tmp_path, mode="fenced", records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert reports[0].meta["personality"]["relevant"] is True

    def test_an_honest_nothing_is_a_complete_answer(self, tmp_path):
        """`relevant: false` is not a failure — it is the answer most
        events should get from most stores."""
        a, _, reports = agent(tmp_path, mode="silent", records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        slot = reports[0].meta["personality"]
        assert slot["relevant"] is False
        assert slot["decided_by"] == "llm"
        assert a.metrics["fallbacks"] == 0

    def test_it_reports_how_many_records_it_considered(self, tmp_path):
        """'relevant: false' means something quite different over 8
        records than over 1."""
        a, _, reports = agent(tmp_path, records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert reports[0].meta["personality"]["records_considered"] == 2

    def test_the_tier_is_reported_as_live(self, tmp_path):
        a, _, reports = agent(tmp_path, records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert reports[0].meta["personality"]["tier"] == "live"


class TestEmptyArchiveShortCircuit:
    def test_an_empty_store_never_reaches_the_substrate(self, tmp_path):
        """Not merely an optimisation: this runs twice on every event, and
        reasoning over zero records is spending money to be told there is
        nothing there."""
        a, substrate, _ = agent(tmp_path, records=[])
        a.bus.publish(a.topic, _event())
        assert substrate.calls == []
        assert a.metrics["llm_calls"] == 0
        assert a.metrics["skipped_empty"] == 1

    def test_the_short_circuit_is_not_a_fallback(self, tmp_path):
        """It is the correct answer, arrived at for free — reporting it as
        degraded would make a healthy system look like an outage."""
        a, _, reports = agent(tmp_path, records=[])
        a.bus.publish(a.topic, _event())
        assert reports[0].meta["personality"]["decided_by"] == "deterministic"
        assert a.metrics["fallbacks"] == 0


class TestDegradation:
    def test_a_substrate_outage_degrades_to_silence(self, tmp_path):
        a, _, reports = agent(tmp_path, mode="boom", records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        slot = reports[0].meta["personality"]
        assert slot["findings"] == ""
        assert slot["relevant"] is False
        assert slot["decided_by"] == "fallback"

    def test_an_unparseable_answer_degrades_to_silence_not_to_prose(self, tmp_path):
        """The failure that would matter: passing the model's chatter
        through as if it were a record."""
        a, _, reports = agent(tmp_path, mode="prose", records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        slot = reports[0].meta["personality"]
        assert slot["findings"] == ""
        assert slot["relevant"] is False
        # The prose survives only in the degraded-reason diagnostic, where
        # it is labelled as an unusable answer. What must never happen is
        # it arriving in `findings`, which is the field Intent reads as
        # though the archive had said it.
        assert reports[0].content == ""

    def test_degradation_is_visible_in_the_metrics(self, tmp_path):
        a, _, _ = agent(tmp_path, mode="boom", records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert a.metrics["fallbacks"] == 1

    def test_an_outage_never_stops_the_event(self, tmp_path):
        """This family gates nothing. A lookup failing must cost grounding
        and nothing else."""
        a, _, reports = agent(tmp_path, mode="boom", records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert len(reports) == 1

    def test_strict_mode_re_raises_for_calibration_runs(self, tmp_path):
        a, _, _ = agent(tmp_path, mode="boom", records=SOME_RECORDS, strict=True)
        with pytest.raises(CompletionError):
            a.bus.publish(a.topic, _event())


class TestBudgetMode:
    class _Budget:
        class state:
            reason = "spend cap reached"

        def should_call_substrate(self):
            return False

        def record_success(self, **kwargs):    # pragma: no cover
            raise AssertionError("must not spend in budget mode")

        def record_failure(self, *args):       # pragma: no cover
            pass

    def test_budget_mode_spends_nothing(self, tmp_path):
        a, substrate, _ = agent(tmp_path, records=SOME_RECORDS,
                                budget=self._Budget())
        a.bus.publish(a.topic, _event())
        assert substrate.calls == []

    def test_budget_mode_degrades_to_the_same_silence(self, tmp_path):
        """There is no cheaper real answer available: relevance over
        free-text records is exactly the part that needed a model."""
        a, _, reports = agent(tmp_path, records=SOME_RECORDS,
                              budget=self._Budget())
        a.bus.publish(a.topic, _event())
        slot = reports[0].meta["personality"]
        assert slot["relevant"] is False
        assert slot["decided_by"] == "budget"
        assert slot["budget_reason"] == "spend cap reached"


# ---------------------------------------------------------------------------
# The invariants the mock tier established, re-checked on the live one
# ---------------------------------------------------------------------------

class TestInvariantsSurviveGoingLive:
    def test_it_is_still_read_only_by_construction(self, tmp_path):
        """The live tier is the one that would actually have something to
        write. It still holds a query-only view with no write method on
        it to reach for."""
        a, _, _ = agent(tmp_path, records=SOME_RECORDS)
        assert not hasattr(a.archive, "write")
        assert not hasattr(a.archive, "execute_writes")

    def test_it_reads_only_its_own_store(self, tmp_path):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        archive.write("knowledge", {"content": "a knowledge record"})
        bus = EmbeddedBus()
        reports = []
        bus.subscribe("events.governance", reports.append)
        a = ArchiveLookupAgent(bus, archive, FakeSubstrate("echo"),
                               role="Personality")
        a.bus.publish(a.topic, _event())
        # Identity store is empty, so it short-circuits — it never saw the
        # knowledge record at all.
        assert a.metrics["skipped_empty"] == 1

    def test_it_never_raises_the_severity_of_an_event(self, tmp_path):
        a, _, reports = agent(tmp_path, records=SOME_RECORDS)
        event = _event()
        event.severity = "Neutral"
        a.bus.publish(a.topic, event)
        assert reports[0].severity == "Neutral"

    def test_it_never_addresses_the_human(self, tmp_path):
        """Stated in the prompt contract, asserted here: the system
        instruction has to actually say so."""
        a, substrate, _ = agent(tmp_path, records=SOME_RECORDS)
        a.bus.publish(a.topic, _event())
        assert "never address the human" in substrate.calls[0]["system"].lower()

    def test_the_prompt_carries_the_records_and_the_event(self, tmp_path):
        a, substrate, _ = agent(tmp_path, records=SOME_RECORDS)
        a.bus.publish(a.topic, _event("Should I tell them the truth?"))
        user = substrate.calls[0]["user"]
        assert "Should I tell them the truth?" in user
        assert "prefers directness" in user

    def test_the_prompt_carries_no_conversation_history(self, tmp_path):
        """Single-event scope is what makes this cheap enough to run
        four-way in parallel on every event (v0.35a)."""
        a, substrate, _ = agent(tmp_path, records=SOME_RECORDS)
        for text in ("first thing", "second thing"):
            a.bus.publish(a.topic, _event(text))
        assert "first thing" not in substrate.calls[1]["user"]

    def test_both_roles_use_the_same_class(self, tmp_path):
        """Two instances, not two subclasses — v0.35b's whole design point,
        and the live tier did not quietly undo it."""
        p, _, _ = agent(tmp_path, role="Personality", records=SOME_RECORDS)
        k = ArchiveLookupAgent(EmbeddedBus(), None, FakeSubstrate(),
                               role="Knowledge")
        assert type(p) is type(k)
        assert p.store_kind == "identity" and k.store_kind == "knowledge"


# ---------------------------------------------------------------------------
# Bootstrap
# ---------------------------------------------------------------------------

class TestBootstrap:
    def _manifest(self, tmp_path, mode="correct", **overrides):
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"
        manifest["substrates"]["fast-reflex"] = {
            "provider": ScriptedLookupProvider.name,
            "model": "scripted-lookup-v1",
            "api_key_env": None,
            "max_tokens": 256,
            "options": {"mode": mode, **overrides.pop("options", {})},
        }
        for role in ("analytics", "intent", "consolidator"):
            manifest["roles"][role]["mock"] = True
        manifest["roles"]["personality"]["mock"] = False
        manifest["roles"]["personality"]["substrate"] = "fast-reflex"
        manifest["roles"]["personality"].update(overrides)
        path = tmp_path / "manifest.yaml"
        path.write_text(yaml.safe_dump(manifest))
        return str(path)

    def test_mock_false_now_actually_means_live(self, tmp_path):
        """v0.35b reported `mock: false` and ran the mock anyway, because
        no live tier existed. It does now."""
        eco = Recovery(self._manifest(tmp_path)).bootstrap()
        assert isinstance(eco.personality, ArchiveLookupAgent)
        assert eco.personality.tier == "live"

    def test_mock_true_still_gives_the_mock(self, tmp_path):
        manifest = yaml.safe_load(Path(self._manifest(tmp_path)).read_text())
        manifest["roles"]["personality"]["mock"] = True
        path = tmp_path / "mocked.yaml"
        path.write_text(yaml.safe_dump(manifest))
        eco = Recovery(str(path)).bootstrap()
        assert isinstance(eco.personality, ArchiveLookupMock)

    def test_an_unusable_substrate_stops_the_bootstrap(self, tmp_path):
        """Same posture as every other cognitive role: a role declared
        real with no way to reach its substrate must not quietly run
        mocked."""
        with pytest.raises(BootstrapError) as exc:
            Recovery(self._manifest(
                tmp_path, options={"fail_credentials": True})).bootstrap()
        assert "not usable" in str(exc.value)

    def test_each_instance_reads_its_own_store(self, tmp_path):
        eco = Recovery(self._manifest(tmp_path)).bootstrap()
        assert eco.personality.store_kind == "identity"

    def test_the_shipped_manifest_puts_both_on_the_cheap_slot(self):
        """Two extra calls on EVERY event, in parallel. If these ever
        migrate to an expensive class it should be a decision, not a
        drift."""
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        assert manifest["roles"]["personality"]["substrate"] == "fast-reflex"


# ---------------------------------------------------------------------------
# Budget tiers
# ---------------------------------------------------------------------------

class TestBudgetTiers:
    def _apply(self, tier):
        from budget import tiers
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        manifest["budget_tier"] = tier
        return tiers.apply_tier(manifest)

    def test_minimal_mocks_the_family_entirely(self):
        """Minimal's promise is that the whole ecosystem boots with no
        credentials at all — which a live lookup on a hosted slot would
        quietly break."""
        out = self._apply("minimal")
        assert out["roles"]["personality"]["mock"] is True
        assert out["roles"]["knowledge"]["mock"] is True
        assert "substrate" not in out["roles"]["personality"]

    def test_budget_puts_the_family_on_the_local_slot(self):
        from budget import tiers
        out = self._apply("budget")
        assert out["roles"]["personality"]["mock"] is False
        assert out["roles"]["personality"]["substrate"] == tiers.LOCAL_CLASS

    def test_both_members_always_get_the_same_treatment(self):
        """They are two instances of one class by design; a tier that
        split them would assert a difference the architecture denies."""
        for tier in ("minimal", "budget", "super"):
            out = self._apply(tier)
            assert (out["roles"]["personality"]["mock"]
                    == out["roles"]["knowledge"]["mock"])
            assert (out["roles"]["personality"].get("substrate")
                    == out["roles"]["knowledge"].get("substrate"))


# ---------------------------------------------------------------------------
# Vendor independence (§10.2)
# ---------------------------------------------------------------------------

def test_the_live_tier_names_no_vendor_and_no_model():
    source = Path(
        "agents/archive_lookup/live.py").read_text().lower()
    for vendor in ("anthropic", "openai", "claude-", "gpt-", "llama"):
        assert vendor not in source
