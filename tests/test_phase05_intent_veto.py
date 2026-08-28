"""
Phase 0.5 / v0.35e — Intent speaks, Security and Governance route.

Intent does ONE thing: speak. It never judges whether something should
proceed. Security decides verdicts; Governance routes:
  green  → SPEAK (to Action)
  yellow → REVISE (Intent speaks again with the concern visible)
  red    → BLOCKED (immediate, no LLM call)

A non-green verdict buys exactly ONE more attempt (MAX_REVISION_PASSES).
If the re-speak is also non-green, the event is BLOCKED — a deterministic
notice, not model-authored.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from agents.governance import routing
from agents.intent import contract
from bus.envelope import VERDICT_GREEN, VERDICT_RED, VERDICT_YELLOW, Envelope
from recovery.bootstrap import Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    LLMProvider,
)
from substrates.registry import register_provider

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"
BLOCKED_PROPOSAL = "the thing security did not like"


# ---------------------------------------------------------------------------
# A scripted substrate
# ---------------------------------------------------------------------------

def _plain_speech(_p: str) -> str:
    return "Wide awake, thanks for asking."


def _revised_speech(_p: str) -> str:
    return "Alright — here's another way to put it."


def _boom(_p: str) -> str:
    raise CompletionError("scripted outage")


RESPONDERS = {
    "speech": _plain_speech,
    "revised": _revised_speech,
}


class ScriptedIntentProvider(LLMProvider):
    name = "scripted-veto"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "speech")
        self.calls: list[CompletionRequest] = []

    def validate_credentials(self) -> None:
        return

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 120, "output_tokens": 40})


register_provider(ScriptedIntentProvider.name, ScriptedIntentProvider)


class AlwaysRedSecurity:
    def __init__(self, bus):
        self.bus = bus
        self.verdicts = 0
        bus.subscribe("events.security", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        self.verdicts += 1
        meta = dict(envelope.meta)
        meta["verdict"] = VERDICT_RED
        meta["security_concern"] = "that phrasing is against the rules"
        self.bus.publish("events.governance", envelope.reply(
            source="Security", destination="Governance", type="Verdict",
            content="Red — blocked", meta=meta))


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, mode: str = "speech", **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["veto-scripted"] = {
        "provider": ScriptedIntentProvider.name,
        "model": "scripted-veto-v1",
        "api_key_env": None,
        "max_tokens": 256,
        "options": {"mode": mode},
    }
    manifest["roles"]["analytics"]["mock"] = True
    manifest["roles"]["consolidator"]["mock"] = True
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["knowledge"]["mock"] = True
    manifest["roles"]["consolidator"]["synchronous"] = True
    manifest["roles"]["intent"]["mock"] = False
    manifest["roles"]["intent"]["substrate"] = "veto-scripted"
    manifest["roles"]["intent"].update(role_overrides)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, mode: str = "speech", *, always_red: bool = False,
          **overrides):
    eco = Recovery(str(_manifest(tmp_path, mode, **overrides))).bootstrap()
    if always_red:
        eco.bus._subscribers["events.security"] = []
        eco.security = AlwaysRedSecurity(eco.bus)
    eco.bus.reset_trace()
    return eco


def _verdict(eco, verdict, *, proposed=BLOCKED_PROPOSAL, event_id=None,
             concern="that phrasing is against the rules"):
    env = Envelope(source="Security", destination="Governance", type="Verdict",
                   content=f"{verdict} — from the rule engine",
                   meta={"verdict": verdict, "proposed_action": proposed,
                         "security_concern": concern},
                   **({"event_id": event_id} if event_id else {}))
    eco.bus.publish("events.governance", env)
    return env.event_id


def _intent_hops(eco, event_id):
    return [e for e in eco.bus.trace()
            if e.event_id == event_id and e.source == "Intent"]


def _to_action(eco, event_id):
    return [e for e in eco.bus.trace()
            if e.event_id == event_id and e.destination == "Action"]


# ---------------------------------------------------------------------------
# The routing reversal
# ---------------------------------------------------------------------------

class TestAnalyticsIsSevered:
    @pytest.mark.parametrize("verdict", [VERDICT_YELLOW, VERDICT_RED])
    def test_neither_non_green_lane_reaches_analytics(self, tmp_path, verdict):
        eco = _boot(tmp_path / str(verdict))
        event_id = _verdict(eco, verdict)
        inbound = [e for e in eco.bus.trace()
                   if e.event_id == event_id and e.destination == "Analytics"]
        assert inbound == []

    def test_yellow_routes_to_revise(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = _verdict(eco, VERDICT_YELLOW)
        to_intent = [e for e in eco.bus.trace()
                     if e.event_id == event_id and e.destination == "Intent"]
        assert to_intent and to_intent[0].type == "Revise"

    def test_red_routes_to_blocked(self):
        env = Envelope(source="Security", destination="Governance",
                       type="Verdict", content="Red", meta={"verdict": "red"})
        assert routing.route_for(env, revision_passes=0) is routing.BLOCKED


# ---------------------------------------------------------------------------
# Intent always speaks — Security/Governance decide blocking
# ---------------------------------------------------------------------------

class TestIntentAlwaysSpeaks:
    def test_intent_always_proceeds(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = _verdict(eco, VERDICT_YELLOW)
        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is True

    def test_an_ordinary_event_reaches_action(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        assert _to_action(eco, event_id)

    def test_budget_mode_still_produces_speech(self, tmp_path):
        eco = _boot(tmp_path)
        eco.budget.switch_manual("budget")
        event_id = _verdict(eco, VERDICT_YELLOW)
        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is True
        assert out.meta["intent"]["decided_by"] == "budget"
        assert eco.intent.metrics["llm_calls"] == 0


# ---------------------------------------------------------------------------
# One chance to revise (Daniel, 2026-08-24)
# ---------------------------------------------------------------------------

class TestOneChance:
    def test_the_budget_is_exactly_one(self):
        assert contract.MAX_REVISION_PASSES == 1

    def test_red_blocks_immediately_no_revision(self, tmp_path):
        eco = _boot(tmp_path, always_red=True)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"]
        assert len(blocked) == 1
        assert eco.governance.metrics["revisions"] == 0
        assert eco.security.verdicts == 1
        # The notice is deterministic, not model-authored.
        assert "blocked" in str(blocked[0].content).lower()

    def test_frustration_still_cannot_manufacture_a_critical(self, tmp_path):
        from agents.impulse.agent import IMPULSE_SEVERITY_CEILING
        eco = _boot(tmp_path, always_red=True)
        for _ in range(5):
            eco.sensory.ingest(PROMPT, source_type="prompt")
        assert eco.impulse._assessed_severity() in ("Neutral", IMPULSE_SEVERITY_CEILING)


# ---------------------------------------------------------------------------
# Loop bounding
# ---------------------------------------------------------------------------

class AlwaysYellowSecurity:
    def __init__(self, bus):
        self.bus = bus
        self.verdicts = 0
        bus.subscribe("events.security", self.on_event)

    def on_event(self, envelope: Envelope) -> None:
        self.verdicts += 1
        meta = dict(envelope.meta)
        meta["verdict"] = VERDICT_YELLOW
        self.bus.publish("events.governance", envelope.reply(
            source="Security", destination="Governance", type="Verdict",
            content="the rules do not settle this", meta=meta))


class TestTheLoopIsBounded:
    def test_yellow_is_bounded_too_not_just_red(self, tmp_path):
        eco = _boot(tmp_path)
        eco.bus._subscribers["events.security"] = []
        security = AlwaysYellowSecurity(eco.bus)
        eco.bus.reset_trace()

        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"]
        assert len(blocked) == 1
        assert security.verdicts == 2

    def test_the_router_blocks_any_non_green_once_the_budget_is_spent(self):
        for verdict in (VERDICT_YELLOW, VERDICT_RED):
            env = Envelope(source="Security", destination="Governance",
                           type="Verdict", content="x", meta={"verdict": verdict})
            assert routing.route_for(env, revision_passes=1) is routing.BLOCKED

    def test_green_still_clears_regardless_of_attempts_spent(self):
        env = Envelope(source="Security", destination="Governance",
                       type="Verdict", content="Green",
                       meta={"verdict": VERDICT_GREEN})
        assert routing.route_for(env, revision_passes=99) is routing.SPEAK


# ---------------------------------------------------------------------------
# Credential failures
# ---------------------------------------------------------------------------

class TestCredentialFailuresStillDegrade:
    def test_a_credentials_failure_degrades_gracefully(self, tmp_path):
        from substrates.base import CredentialsError

        eco = _boot(tmp_path)

        def explode(*a, **kw):
            raise CredentialsError("ANTHROPIC_API_KEY is unset or empty")

        eco.intent.substrate.provider.complete = explode
        event_id = _verdict(eco, VERDICT_YELLOW)

        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is True
        assert out.meta["intent"]["decided_by"] == "fallback"


# ---------------------------------------------------------------------------
# Security concern flows through to Intent's prompt
# ---------------------------------------------------------------------------

class TestSecurityConcernInPrompt:
    @staticmethod
    def _in_band(eco, verdict, concern="rule 14: no medical dosing advice"):
        class Scripted:
            def __init__(self, bus):
                self.bus = bus
                bus.subscribe("events.security", self.on_event)

            def on_event(self, envelope):
                meta = dict(envelope.meta)
                meta["verdict"] = verdict
                meta["security_concern"] = concern
                self.bus.publish("events.governance", envelope.reply(
                    source="Security", destination="Governance",
                    type="Verdict", content="verdict prose", meta=meta))

        eco.bus._subscribers["events.security"] = []
        Scripted(eco.bus)
        eco.bus.reset_trace()

    def test_the_prompt_carries_the_original_request(self, tmp_path):
        eco = _boot(tmp_path)
        self._in_band(eco, VERDICT_YELLOW)
        eco.sensory.ingest(PROMPT, source_type="prompt")

        user = eco.intent.substrate.provider.calls[-1].user
        assert f"THE HUMAN SAID: {PROMPT}" in user
