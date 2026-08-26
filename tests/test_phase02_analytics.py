"""
Phase 0.2 test harness — substrate-backed Analytics (§5.4, §13.4).

Offline and free, through a scripted provider registered into the real
substrate registry, so these exercise the actual manifest → registry →
agent path rather than a bypass of it.

The scripted provider is not an attempt to simulate a model. It pins down
what Analytics does with each *shape* of answer one can give — including
the shapes that matter most, which are the malformed ones on a gating
task. Phase 0.1 could check a model's answer against a closed whitelist;
Analytics has no such set, so what gets tested here is the schema and,
above all, the asymmetry of the fallbacks: Evaluate degrades and keeps
moving, Review and Revise decline.

The real endpoint is covered in tests/test_phase02_analytics_live.py.
"""
from __future__ import annotations

import json
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
    return json.dumps({
        "recommendation": "The human is checking whether we're responsive. "
                          "A short, warm acknowledgement is appropriate.",
        "proceed": True,
    })


def _reply_decline(prompt: str) -> str:
    return json.dumps({
        "recommendation": "This asks us to do something we shouldn't.",
        "proceed": False,
        "concern": "It would share something that isn't ours to share.",
    })


def _reply_decline_without_reason(prompt: str) -> str:
    return json.dumps({"recommendation": "No.", "proceed": False})


def _reply_string_proceed(prompt: str) -> str:
    """Models spell booleans several ways."""
    return json.dumps({"recommendation": "Seems fine.", "proceed": "yes"})


def _reply_unreadable_proceed(prompt: str) -> str:
    """The field is present but says nothing usable — the case where the
    fail-closed default has to carry the decision."""
    return json.dumps({"recommendation": "Hmm.", "proceed": "it depends"})


def _reply_persona_speech(prompt: str) -> str:
    """A model that forgets it is advising and writes the reply."""
    return json.dumps({
        "recommendation": "Hey! Yes, I'm wide awake and happy to chat!",
        "proceed": True,
    })


def _reply_missing_field(prompt: str) -> str:
    return json.dumps({"proceed": True, "concern": "none"})


def _reply_prose(prompt: str) -> str:
    return "I think this is fine, honestly. No JSON for you."


def _reply_fenced(prompt: str) -> str:
    return ("Here's my assessment:\n\n```json\n" + _reply_correct(prompt)
            + "\n```\nHope that helps!")


def _reply_essay(prompt: str) -> str:
    return json.dumps({
        "recommendation": "x" * (contract.MAX_RECOMMENDATION_CHARS + 500),
        "proceed": True,
    })


RESPONDERS = {
    "correct": _reply_correct,
    "decline": _reply_decline,
    "decline_without_reason": _reply_decline_without_reason,
    "string_proceed": _reply_string_proceed,
    "unreadable_proceed": _reply_unreadable_proceed,
    "persona_speech": _reply_persona_speech,
    "missing_field": _reply_missing_field,
    "prose": _reply_prose,
    "fenced": _reply_fenced,
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
    # This suite pins roles.analytics.* directly below, so budget_tier
    # must be a no-op (custom/default — budget/tiers.py's _NOOP_TIERS) or
    # whatever the SHIPPED manifest's budget_tier happens to be right now
    # (e.g. an operator's live "minimal") would silently overwrite mock
    # back to True before this fixture's own override ever took effect.
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["deep-reasoning"] = {
        "provider": ScriptedProvider.name,
        "model": "scripted-reasoner-v1",
        "api_key_env": None,
        "max_tokens": 256,
        "options": {"mode": mode},
    }
    manifest["roles"]["analytics"]["mock"] = False
    manifest["roles"]["analytics"].update(role_overrides)
    # This suite is about ANALYTICS; hold Intent deterministic so it needs
    # no credential of its own (Phase 0.4 note — same reasoning as every
    # other pre-Phase-0.4 fixture pinning roles.intent.mock).
    manifest["roles"]["intent"]["mock"] = True
    # Phase 0.6 gave the archive-lookup family a live tier, so the
    # shipped manifest now declares these real. Mocked here for the
    # same reason every other cognitive role is: this test is not
    # about them, and it must run with no credentials.
    manifest["roles"]["personality"]["mock"] = True
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

    def test_a_decline_still_parses_as_a_plain_recommendation(self):
        """2026-08-25: proceed/concern are gone entirely. A model that
        still sends them (old prompt cached somewhere, a stray fixture)
        should not break parsing — the extra fields are simply ignored,
        and Analytics has no power to stop anything either way."""
        rec = contract.parse(_reply_decline(""), Task.EVALUATE)
        assert not hasattr(rec, "proceed")
        assert not hasattr(rec, "concern")
        assert rec.recommendation

    @pytest.mark.parametrize("raw,expected", [
        (True, True), (False, False), ("yes", True), ("no", False),
        ("true", True), ("FALSE", False), ("decline", False), ("proceed", True),
        (1, True), (0, False),
    ])
    def test_booleans_are_read_the_way_models_spell_them(self, raw, expected):
        assert coerce_bool(raw, default=not expected) is expected

    @pytest.mark.parametrize("bad", ["prose", "missing_field"])
    def test_unusable_answers_raise(self, bad):
        with pytest.raises(ContractViolation):
            contract.parse(RESPONDERS[bad](""), Task.EVALUATE)

    def test_an_essay_is_truncated_not_rejected(self):
        """Too much content is a length problem, not a correctness one."""
        rec = contract.parse(_reply_essay(""), Task.EVALUATE)
        assert len(rec.recommendation) == contract.MAX_RECOMMENDATION_CHARS

    def test_prose_wrapped_json_is_understood(self):
        assert contract.parse(_reply_fenced(""), Task.EVALUATE).recommendation

    # ---- the fallback -------------------------------------------

    def test_evaluate_degrades_and_keeps_moving(self):
        env = _envelope()
        rec = contract.fallback(env, Task.EVALUATE, "substrate down")
        assert rec.recommendation == contract.templated_recommendation(env)
        assert rec.diagnostics["degraded"] is True

    def test_there_are_no_gating_tasks_left_here(self):
        """v0.35e severed Analytics from Security entirely (Daniel,
        2026-08-24: "analytics is isolated from security in every way").
        2026-08-25: proceed/concern removed too — Analytics is "as dumb
        as Personality and Knowledge" and gates nothing, period. Review
        and Revise are Intent's, and this file should carry no trace of
        gating — a fail-closed path with nothing to gate is dead code that
        reads like a safety property."""
        assert [t.value for t in Task] == ["Evaluate"]
        assert not hasattr(contract, "GATING_TASKS")
        assert not hasattr(contract, "FAIL_CLOSED_TASKS")
        assert not hasattr(contract.Recommendation, "proceed")
        assert not hasattr(contract.Recommendation, "concern")

    def test_the_degraded_evaluate_matches_the_mock_exactly(self):
        """A substrate outage should change the quality of the thinking,
        not the shape of the trace."""
        env = _envelope()
        assert (contract.fallback(env, Task.EVALUATE, "x").recommendation
                == contract.templated_recommendation(env))

    def test_unknown_message_types_are_not_tasks(self):
        assert Task.from_envelope(_envelope(type="LoopCheck")) is None

    def test_securitys_old_lanes_are_no_longer_tasks_here(self):
        for retired in ("Review", "Revise"):
            assert Task.from_envelope(_envelope(type=retired)) is None

    @pytest.mark.parametrize("modality", ["prompt", "feedback", "vision",
                                          "audio", "https"])
    def test_every_sensory_modality_reads_as_evaluate(self, modality):
        """v0.35a: Sensory fans out to Analytics directly, so the type on
        the envelope is the modality rather than a task name."""
        assert Task.from_envelope(_envelope(type=modality)) is Task.EVALUATE


# ---------------------------------------------------------------------------
# What never reaches the substrate
# ---------------------------------------------------------------------------

class TestMechanicalWorkIsFree:
    def test_a_detected_loop_costs_nothing(self, tmp_path):
        """Phase 0.1's lesson carried forward: an agent shouldn't pay for
        inference to notice it has seen the same thing three times."""
        eco = _boot(tmp_path)
        for _ in range(LOOP_THRESHOLD):
            eco.bus.publish("events.analytics", _envelope(content="same thing"))

        assert eco.analytics.metrics["loops_detected"] == 1
        assert eco.analytics.metrics["llm_calls"] == LOOP_THRESHOLD - 1

    def test_a_detected_loop_is_flagged(self, tmp_path):
        eco = _boot(tmp_path)
        for _ in range(LOOP_THRESHOLD):
            eco.bus.publish("events.analytics", _envelope(content="same thing"))
        last = [e for e in eco.bus.trace() if e.source == "Analytics"][-1]
        assert last.destination == "Governance"
        assert last.meta["analytics"]["loop_detected"] is True

    def test_the_control_plane_never_calls_a_model(self, tmp_path):
        """§9's zero-LLM-dependency deploy, and §11's Level 2 ladder."""
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
        assert hops == [
            # v0.35a: the four-way fan-out, ungated. Impulse first, so an
            # emergency is on its way to Security before the rest are even
            # dispatched (v0.35d).
            ("Sensory", "Impulse"), ("Impulse", "Governance"),
            ("Sensory", "Analytics"), ("Analytics", "Governance"),
            ("Sensory", "Personality"), ("Personality", "Governance"),
            # v0.35c: Governance bundles all three into one message.
            ("Governance", "Intent"),
            ("Intent", "Governance"), ("Governance", "Security"),
            ("Security", "Governance"), ("Governance", "Action"),
        ]
        assert eco.analytics.metrics["llm_calls"] == 1

    def test_analytics_reports_to_governance_and_never_speaks(self, tmp_path):
        """v0.35c redirected this hop: Analytics' answer is now one of
        four inputs Governance bundles, not a message to Intent. Even when
        the model forgets and writes the reply, that text reaches Intent
        as one labelled slot in a bundle — structural, not a rule anyone
        has to keep."""
        eco = _boot(tmp_path, mode="persona_speech")
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        analytics_out = [e for e in eco.bus.trace()
                         if e.event_id == event_id and e.source == "Analytics"][0]
        assert analytics_out.destination == "Governance"
        assert analytics_out.type == "Recommend"
        # What Action speaks came from Intent, not from Analytics' text.
        assert "wide awake and happy to chat" not in _spoken(eco, event_id)[0]

    def test_analytics_never_hears_from_security_at_all(self, tmp_path):
        """v0.35e, in its widest form (Daniel, 2026-08-24): Analytics is
        isolated from Security in every way. Neither non-green lane
        reaches it — both are Intent's now."""
        eco = _boot(tmp_path)
        for verdict in (VERDICT_YELLOW, VERDICT_RED):
            eco.bus.reset_trace()
            event_id = _verdict(eco, verdict)
            inbound = [e for e in eco.bus.trace()
                       if e.event_id == event_id and e.destination == "Analytics"]
            assert inbound == []

    def test_severity_still_propagates_untouched(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("knife on counter", source_type="prompt",
                                      severity="Critical")
        severities = {e.severity for e in eco.bus.trace() if e.event_id == event_id}
        assert severities == {"Critical"}

    def test_judgments_are_attributed_in_the_queue_log(self, tmp_path):
        """§7.4's source_substrate / source_model split, so Diagnostic
        (§12) can trace which substrate produced which judgment."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        logged = eco.archive.query_queue(
            predicate=lambda r: r.get("event_id") == event_id
                                and r.get("source") == "Analytics")
        assert logged
        analytics_meta = logged[0]["meta"]["analytics"]
        assert analytics_meta["tier"] == "live"
        assert analytics_meta["decided_by"] == "llm"
        assert analytics_meta["source_substrate"] == "deep-reasoning"
        assert analytics_meta["source_model"] == "scripted-reasoner-v1"
        assert analytics_meta["usage"]["input_tokens"] == 120


# ---------------------------------------------------------------------------
# Degradation
# ---------------------------------------------------------------------------

class TestDegradation:
    @pytest.mark.parametrize("mode", ["boom", "prose", "missing_field"])
    def test_an_event_survives_a_bad_substrate(self, tmp_path, mode):
        """One posture now: degrade and keep moving. v0.35e took the
        gating away, so there is no second, stricter path here to
        contrast this with — see tests/test_phase05_intent_veto.py for
        where that asymmetry went."""
        eco = _boot(tmp_path / mode, mode=mode)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        assert eco.analytics.metrics["fallbacks"] == 1
        assert len(eco.action.executed) == 1
        analytics_out = [e for e in eco.bus.trace()
                         if e.event_id == event_id and e.source == "Analytics"][0]
        assert analytics_out.meta["analytics"]["degraded"] is True

    @pytest.mark.parametrize("mode", ["boom", "prose", "missing_field"])
    def test_a_degraded_event_still_reaches_the_human(self, tmp_path, mode):
        """An outage changes the quality of the thinking, not the shape of
        the trace — and never leaks a stack trace into the persona's
        mouth."""
        eco = _boot(tmp_path / mode, mode=mode)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        spoken = _spoken(eco, event_id)
        assert spoken
        assert "CompletionError" not in spoken[0]

    def test_strict_mode_surfaces_the_failure_instead(self, tmp_path):
        eco = _boot(tmp_path, mode="prose", strict=True)
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
        """Flat cost as history grows (§1) depends on the live prompt
        staying bounded. The working window is capped, so the prompt
        plateaus rather than climbing."""
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
        assert "Reply with a single JSON object" in self._prompt_for(eco).system

    def test_the_fixed_overhead_stays_condensed(self, tmp_path):
        """2026-08-23: the persona/wording rule used to be restated in
        system_instruction, RESPONSE_CONTRACT, and TASK_BRIEFS — three
        copies of the same two points on every single call, ~360 words of
        pure scaffolding against a two-word test event. Cut to ~90 words
        (contract.py, manifest system_instruction) with no behavior
        change. This is a budget, not an exact match — pins the order of
        magnitude so it can't silently balloon back up, without being so
        tight that any rewording trips it."""
        eco = _boot(tmp_path)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        system = self._prompt_for(eco).system
        assert len(system.split()) < 150, (
            f"system prompt overhead grew to {len(system.split())} words — "
            f"was ~90 after the 2026-08-23 condensing; check for reintroduced "
            f"duplication across system_instruction/RESPONSE_CONTRACT/TASK_BRIEFS")


# ---------------------------------------------------------------------------
# Vendor independence (§10.2)
# ---------------------------------------------------------------------------

class TestVendorIndependence:
    def test_analytics_names_no_vendor_and_no_model(self):
        """The substrate class is the only thing the agent knows about."""
        import agents.analytics.live as live_mod
        import agents.analytics.contract as contract_mod

        for mod in (live_mod, contract_mod):
            source = Path(mod.__file__).read_text().lower()
            for banned in ("claude-", "gpt-", "haiku", "sonnet", "opus",
                           "llama", "mistral", "api.anthropic.com", "sk-ant"):
                assert banned not in source, (
                    f"{mod.__name__} names '{banned}' — vendors belong in the "
                    f"manifest's substrate table (§10.2)")

    def test_swapping_the_vendor_is_a_manifest_edit(self, tmp_path):
        """The same agent, the same code path, a different provider."""
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"   # see _manifest()'s comment above
        manifest["substrates"]["deep-reasoning"] = {
            "provider": "echo", "model": "some-other-vendor-model",
            "options": {"script": [_reply_correct("")]},
        }
        manifest["roles"]["analytics"]["mock"] = False
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
        assert eco.analytics.tier == "mock"

    def test_mock_flag_false_selects_the_live_tier(self, tmp_path):
        eco = Recovery(str(_manifest(tmp_path))).bootstrap()
        assert eco.analytics.tier == "live"
        assert eco.analytics.substrate.substrate_class == "deep-reasoning"

    def test_an_unusable_substrate_stops_the_bootstrap(self, tmp_path, monkeypatch):
        """§9.1 step 6 — offline, so it costs nothing and works with every
        endpoint down. This is what Daniel sees before adding a key."""
        monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
        monkeypatch.delenv("OPENAI_API_KEY", raising=False)
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"   # else this test's shipped-manifest
                                              # budget_tier (e.g. "minimal") would
                                              # force analytics.mock back to True
                                              # before the credential check ever runs
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)

        with pytest.raises(BootstrapError, match="substrate is not usable"):
            Recovery(str(path)).bootstrap()

    def test_the_shipped_manifest_declares_analytics_real(self):
        """Analytics has been real since Phase 0.2. If this flips to true
        by accident, that stops being true without anyone noticing."""
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        assert manifest["roles"]["analytics"]["mock"] is False
        assert manifest["roles"]["analytics"]["substrate"] == "deep-reasoning"
        assert manifest["phase"] == 0.5


# ---------------------------------------------------------------------------
# Shared parsing helper
# ---------------------------------------------------------------------------

class TestJsonExtraction:
    @pytest.mark.parametrize("text", [
        '{"recommendation": "x"}',
        'Sure:\n```json\n{"recommendation": "x"}\n```',
        'prose {"recommendation": "x"} more prose',
        '{"recommendation": "he said {this} and }that{"}',
    ])
    def test_finds_the_object_in_realistic_noise(self, text):
        assert extract_json_object(text)["recommendation"]

    @pytest.mark.parametrize("text", ["", "no json here", "{unbalanced", "[1,2,3]"])
    def test_returns_none_when_there_is_no_object(self, text):
        assert extract_json_object(text) is None
