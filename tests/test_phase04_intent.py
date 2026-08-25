"""
Phase 0.4 test harness — substrate-backed Intent (§5.5, §7, §13.4).

Offline and free, through a scripted provider registered into the real
substrate registry — the same posture as test_phase02_analytics.py, and
for the same reason: what matters here is what Intent does with each
*shape* of answer, especially the shapes unique to this hop:

  - a model that parrots Analytics' recommendation back out (the one
    failure mode that has no equivalent in Analytics' own contract —
    Analytics never has a persona to accidentally drop);
  - a refusal lead-in that reads as assent rather than decline, the one
    place this hop's fallback asymmetry with Analytics' contract doesn't
    hold (see agents/intent/contract.py's module docstring);
  - consolidation actually reasoning about a batch, and the "slow
    coloring" recalibration reaching a live Impulse instance.

The real endpoint would be covered in tests/test_phase04_intent_live.py,
following test_phase02_analytics_live.py's pattern — not added yet, no
credential having exercised this against a real model.
"""
from __future__ import annotations

import json
from pathlib import Path

import pytest
import yaml

from bus.envelope import VERDICT_YELLOW, Envelope
from recovery.bootstrap import BootstrapError, Recovery
from substrates.base import CompletionError, CompletionRequest, CompletionResponse, LLMProvider
from substrates.registry import register_provider

from agents.intent import contract
from agents.intent.contract import ContractViolation

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"
RECOMMENDATION = "responsive check, warm reply appropriate"


# ---------------------------------------------------------------------------
# A scripted provider — one per response shape
# ---------------------------------------------------------------------------

def _advise_correct(prompt: str) -> str:
    return json.dumps({"speech": "Hey — yep, wide awake. Good timing, actually."})


def _advise_parrot(prompt: str) -> str:
    return json.dumps({"speech": RECOMMENDATION})


def _advise_missing_field(prompt: str) -> str:
    return json.dumps({"response_text": "hi"})


def _advise_prose(prompt: str) -> str:
    return "Sure, I'm awake!"


def _refuse_correct(prompt: str) -> str:
    return json.dumps({"lead_in": "Ah — I'd rather sit this one out."})


def _refuse_assent(prompt: str) -> str:
    return json.dumps({"lead_in": "Sure, happy to help with that."})


def _refuse_missing_field(prompt: str) -> str:
    return json.dumps({"nope": True})


def _consolidate_correct(prompt: str) -> str:
    return json.dumps({
        "deltas": [{"trait": "curiosity_bias", "rationale": "engaged well with an open prompt"}],
        "recalibration": {"temperature": 0.05},
        "evolving_delta": "Leaning a little warmer lately.",
    })


def _consolidate_big_recalibration(prompt: str) -> str:
    return json.dumps({"deltas": [], "recalibration": {"temperature": 5.0}})


RESPONDERS = {
    "advise_correct": _advise_correct,
    "advise_parrot": _advise_parrot,
    "advise_missing_field": _advise_missing_field,
    "advise_prose": _advise_prose,
    "refuse_correct": _refuse_correct,
    "refuse_assent": _refuse_assent,
    "refuse_missing_field": _refuse_missing_field,
    "consolidate_correct": _consolidate_correct,
    "consolidate_big_recalibration": _consolidate_big_recalibration,
    "boom": None,
}


class ScriptedIntentProvider(LLMProvider):
    name = "scripted-intent"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "advise_correct")
        self.calls: list[CompletionRequest] = []

    def validate_credentials(self) -> None:
        return

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 90, "output_tokens": 30})


register_provider(ScriptedIntentProvider.name, ScriptedIntentProvider)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, mode: str = "advise_correct", **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["fast-reflex"] = {
        "provider": ScriptedIntentProvider.name,
        "model": "scripted-intent-v1",
        "api_key_env": None,
        "max_tokens": 256,
        "options": {"mode": mode},
    }
    # This suite is about INTENT; hold Analytics deterministic so it needs
    # no credential of its own.
    manifest["roles"]["analytics"]["mock"] = True
    manifest["roles"]["intent"]["mock"] = False
    # v0.35f: consolidation is its own role. This suite is about Intent's
    # VOICE, so hold Consolidator deterministic — its own suite
    # (tests/test_phase05_consolidator.py) covers the reasoning half.
    manifest["roles"]["consolidator"]["mock"] = True
    # Phase 0.6 gave the archive-lookup family a live tier, so the
    # shipped manifest now declares these real. Mocked here for the
    # same reason every other cognitive role is: this test is not
    # about them, and it must run with no credentials.
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["knowledge"]["mock"] = True
    manifest["roles"]["consolidator"]["synchronous"] = True
    manifest["roles"]["intent"].update(role_overrides)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, mode: str = "advise_correct", **overrides):
    eco = Recovery(str(_manifest(tmp_path, mode, **overrides))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _analytics_reply(eco, content=RECOMMENDATION, **extra_meta):
    """Inject straight onto events.intent — same shape Analytics' emit()
    produces, without needing a live Analytics.

    2026-08-25: proceed/concern are gone from this hand-off — Recommend/
    Bundle always resolve to ADVISE now (see Task.from_envelope). Any
    extra meta a caller still wants to pass (e.g. for a REVIEW/REVISE
    envelope built some other way) goes through **extra_meta."""
    env = Envelope(source="Analytics", destination="Intent", type="Recommend",
                  content=content, meta=dict(extra_meta))
    eco.bus.publish("events.intent", env)
    return env.event_id


def _spoken(eco, event_id):
    return [str(e.meta.get("proposed_action")) for e in eco.bus.trace()
            if e.event_id == event_id and e.source == "Intent"]


# ---------------------------------------------------------------------------
# The contract
# ---------------------------------------------------------------------------

class TestContract:
    def test_a_well_formed_advise_parses(self):
        speech = contract.parse_advise(_advise_correct(""), RECOMMENDATION)
        assert "wide awake" in speech.text
        assert speech.decided_by == "llm"

    def test_a_response_that_parrots_the_recommendation_is_rejected(self):
        """The failure mode unique to this hop — Analytics has no persona
        to accidentally drop, but Intent does."""
        with pytest.raises(ContractViolation, match="parrot"):
            contract.parse_advise(_advise_parrot(""), RECOMMENDATION)

    @pytest.mark.parametrize("recommendation,speech", [
        ("responsive check, warm reply appropriate",
         "RESPONSIVE CHECK, WARM REPLY APPROPRIATE"),
        ("responsive check", "Well, responsive check, obviously."),
    ])
    def test_parroting_is_caught_case_and_wrapper_insensitively(self, recommendation, speech):
        assert contract.is_parroting(speech, recommendation)

    def test_genuine_reaction_does_not_trip_the_parrot_guard(self):
        assert not contract.is_parroting(
            "Hey! Good to hear from you.", RECOMMENDATION)

    @pytest.mark.parametrize("bad", ["advise_missing_field", "advise_prose"])
    def test_unusable_advise_answers_raise(self, bad):
        with pytest.raises(ContractViolation):
            contract.parse_advise(RESPONDERS[bad](""), RECOMMENDATION)

    def test_a_refusal_lead_in_is_appended_to_the_concern_verbatim(self):
        speech = contract.parse_refuse(_refuse_correct(""), "it isn't ours to share")
        assert speech.text.startswith("Ah")
        assert speech.text.endswith("it isn't ours to share")

    def test_an_assenting_lead_in_is_rejected_not_trusted(self):
        """The one place Intent's contract does more than Analytics' ever
        had to — Governance forwards this straight to Security with no
        semantic check, so a refusal that reads as agreement can't be
        allowed through even once."""
        with pytest.raises(ContractViolation, match="assent"):
            contract.parse_refuse(_refuse_assent(""), "some concern")

    def test_unusable_refuse_answers_raise(self):
        with pytest.raises(ContractViolation):
            contract.parse_refuse(_refuse_missing_field(""), "concern")

    def test_fallback_advice_matches_the_mock(self):
        assert contract.fallback_advice("x", "reason").text == contract.DEFAULT_ADVISE_FALLBACK

    def test_fallback_refusal_matches_the_mock(self):
        speech = contract.fallback_refusal("a real reason", "reason")
        assert speech.text == f"{contract.DEFAULT_REFUSAL_LEAD_IN} a real reason"


# ---------------------------------------------------------------------------
# Voicing, end to end
# ---------------------------------------------------------------------------

class TestVoicing:
    def test_advise_produces_persona_speech_not_analysis(self, tmp_path):
        eco = _boot(tmp_path, mode="advise_correct")
        event_id = _analytics_reply(eco)
        spoken = _spoken(eco, event_id)
        assert spoken and RECOMMENDATION not in spoken[0]

    def test_a_parroting_model_degrades_to_the_fallback(self, tmp_path):
        """The guard has teeth: it doesn't just reject in parse(), it
        actually changes what reaches the human."""
        eco = _boot(tmp_path, mode="advise_parrot")
        event_id = _analytics_reply(eco)
        spoken = _spoken(eco, event_id)
        assert spoken == [contract.DEFAULT_ADVISE_FALLBACK]
        assert eco.intent.metrics["fallbacks"] == 1

    # 2026-08-25: proceed/concern removed from the Recommend/Bundle
    # hand-off — Task.from_envelope now always resolves those two types to
    # ADVISE (see agents/intent/contract.py), so REFUSE is unreachable via
    # this route any more. The end-to-end "refusal via Recommend" tests
    # that used to live here are gone with it; parse_refuse/fallback_refusal
    # themselves are still exercised directly in TestContract above, since
    # the REFUSE register's machinery is untouched, just no longer wired
    # to Analytics' opinion.

    def test_an_outage_degrades_advise_to_the_deterministic_line(self, tmp_path):
        eco = _boot(tmp_path, mode="boom")
        event_id = _analytics_reply(eco)
        assert _spoken(eco, event_id) == [contract.DEFAULT_ADVISE_FALLBACK]

    def test_strict_mode_surfaces_the_failure_instead(self, tmp_path):
        eco = _boot(tmp_path, mode="advise_prose", strict=True)
        with pytest.raises(ContractViolation):
            _analytics_reply(eco)

    def test_attribution_is_written_to_meta_intent(self, tmp_path):
        eco = _boot(tmp_path, mode="advise_correct")
        event_id = _analytics_reply(eco)
        out = [e for e in eco.bus.trace()
              if e.event_id == event_id and e.source == "Intent"][0]
        assert out.meta["intent"]["tier"] == "live"
        assert out.meta["intent"]["decided_by"] == "llm"
        assert out.meta["intent"]["source_substrate"] == "fast-reflex"
        assert out.meta["intent"]["source_model"] == "scripted-intent-v1"

    def test_the_pipeline_still_reaches_action(self, tmp_path):
        """Wired through Governance/Security/Action, not just up to Intent."""
        eco = _boot(tmp_path, mode="advise_correct")
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        hops = [(e.source, e.destination) for e in eco.bus.trace()
               if e.event_id == event_id]

        # 2026-08-25: Analytics/Personality/Knowledge dispatch concurrently
        # now (agents/sensory/agent.py) — their six hops can interleave in
        # any order, so check them as a set. Impulse's prefix and the
        # bundle-onward suffix stay strictly sequential and ordered.
        assert hops[0] == ("Sensory", "Impulse")
        assert hops[1] == ("Impulse", "Governance")
        assert set(hops[2:8]) == {
            ("Sensory", "Analytics"), ("Analytics", "Governance"),
            ("Sensory", "Personality"), ("Personality", "Governance"),
            ("Sensory", "Knowledge"), ("Knowledge", "Governance"),
        }
        assert hops[8:] == [
            ("Governance", "Intent"), ("Intent", "Governance"),
            ("Governance", "Security"), ("Security", "Governance"),
            ("Governance", "Action"),
        ]


# ---------------------------------------------------------------------------
# Persona hydration and Core Anchors seeding
# ---------------------------------------------------------------------------

class TestPersona:
    def test_core_anchors_are_seeded_on_first_bootstrap(self, tmp_path):
        eco = _boot(tmp_path)
        records = eco.archive.query("identity")
        anchors = [r for r in records if r.get("kind") == "anchors"]
        assert len(anchors) == 1
        assert anchors[0]["anchors"]["stance"]

    def test_seeding_is_idempotent_across_reboots(self, tmp_path):
        path = _manifest(tmp_path)
        Recovery(str(path)).bootstrap()
        Recovery(str(path)).bootstrap()
        from agents.archive.store import ArchiveStore
        import yaml as _yaml
        with open(path) as f:
            storage_root = _yaml.safe_load(f)["storage"]["root"]
        archive = ArchiveStore(root=storage_root)
        anchors = [r for r in archive.query("identity") if r.get("kind") == "anchors"]
        assert len(anchors) == 1

    def test_the_persona_prompt_carries_the_stance(self, tmp_path):
        eco = _boot(tmp_path, mode="advise_correct")
        _analytics_reply(eco)
        user = eco.intent.substrate.provider.calls[-1].user
        assert "STANCE" in user
        assert "active listener" in user.lower()


# ---------------------------------------------------------------------------
# Consolidation (§7.4) and "slow coloring" (§5.3)
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Bootstrap (§9.1, §13.4)
# ---------------------------------------------------------------------------

class TestBootstrap:
    def test_mock_flag_true_selects_the_templated_tier(self, tmp_path):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["roles"]["analytics"]["mock"] = True
        manifest["roles"]["intent"]["mock"] = True
        # Phase 0.6 gave the archive-lookup family a live tier, so the
        # shipped manifest now declares these real. Mocked here for the
        # same reason every other cognitive role is: this test is not
        # about them, and it must run with no credentials.
        manifest["roles"]["personality"]["mock"] = True
        manifest["roles"]["knowledge"]["mock"] = True
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        eco = Recovery(str(path)).bootstrap()
        assert eco.intent.tier == "mock"

    def test_mock_flag_false_selects_the_live_tier(self, tmp_path):
        eco = Recovery(str(_manifest(tmp_path))).bootstrap()
        assert eco.intent.tier == "live"
        assert eco.intent.substrate.substrate_class == "fast-reflex"

    def test_an_unusable_substrate_stops_the_bootstrap(self, tmp_path, monkeypatch):
        monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
        monkeypatch.delenv("OPENAI_API_KEY", raising=False)
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"
        manifest["roles"]["analytics"]["mock"] = True
        # Phase 0.6 gave the archive-lookup family a live tier, so the
        # shipped manifest now declares these real. Mocked here for the
        # same reason every other cognitive role is: this test is not
        # about them, and it must run with no credentials.
        manifest["roles"]["personality"]["mock"] = True
        manifest["roles"]["knowledge"]["mock"] = True
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        with pytest.raises(BootstrapError, match="substrate is not usable"):
            Recovery(str(path)).bootstrap()

    def test_an_unusable_consolidator_substrate_warns_but_does_not_stop_boot(
            self, tmp_path, capsys):
        """Consolidation is a rare, non-blocking background pass — its
        substrate being unreachable shouldn't take down the live pipeline
        (see _provision_consolidator's docstring). v0.35f moved it to its
        own role; the posture is unchanged."""
        path = _manifest(tmp_path)
        with open(path) as f:
            manifest = yaml.safe_load(f)
        manifest["roles"]["consolidator"]["mock"] = False
        manifest["roles"]["consolidator"]["substrate"] = "orthogonal"
        manifest["substrates"]["orthogonal"]["api_key_env"] = "SOME_UNSET_ENV_VAR"
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        eco = Recovery(str(path)).bootstrap()
        assert eco.intent.tier == "live"          # live pipeline unaffected
        assert eco.consolidator.tier == "mock"    # degraded, not dead
        assert "consolidator substrate" in capsys.readouterr().out

    def test_a_stale_nodes_list_is_reported_not_silently_used(self, tmp_path, capsys):
        """v0.35f removed the fleet/rotation model. A manifest still
        carrying `nodes:` gets told, rather than having it quietly ignored
        — a stale list looks like it is doing something."""
        path = _manifest(tmp_path)
        with open(path) as f:
            manifest = yaml.safe_load(f)
        manifest["roles"]["intent"]["nodes"] = [{"id": "node-a",
                                                 "substrate": "orthogonal"}]
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        eco = Recovery(str(path)).bootstrap()
        assert "roles.intent.nodes is set" in capsys.readouterr().out
        # And the flat substrate is what actually got used.
        assert eco.intent.substrate.substrate_class == "fast-reflex"

    def test_the_shipped_manifest_declares_intent_real(self, monkeypatch):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        assert manifest["roles"]["intent"]["mock"] is False
        assert manifest["roles"]["intent"]["substrate"] == "fast-reflex"
        assert manifest["phase"] == 0.5


# ---------------------------------------------------------------------------
# Vendor independence (§10.2)
# ---------------------------------------------------------------------------

class TestVendorIndependence:
    def test_intent_names_no_vendor_and_no_model(self):
        import agents.intent.live as live_mod
        import agents.intent.contract as contract_mod

        for mod in (live_mod, contract_mod):
            source = Path(mod.__file__).read_text().lower()
            for banned in ("claude-", "gpt-", "haiku", "sonnet", "opus",
                           "llama", "mistral", "api.anthropic.com", "sk-ant"):
                assert banned not in source, (
                    f"{mod.__name__} names '{banned}' — vendors belong in the "
                    f"manifest's substrate table (§10.2)")
