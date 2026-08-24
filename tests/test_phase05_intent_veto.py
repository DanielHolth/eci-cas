"""
Phase 0.5 / v0.35e — Intent's veto, and the one-chance revision.

The safety-critical change, given its own file the way Phase 0.1's
Governance-determinism change and Phase 0.3's severity-ceiling change
each got one.

This reverses a documented safety property. Phase 0.4's Intent was
"advisory only... holds no veto" (§5.5), and `agents/intent/contract.py`
said so in its module docstring as a deliberate design choice. v0.35e
reverses it, and Daniel's 2026-08-24 confirmation went wider than the
spec draft: **Analytics is isolated from Security in every way**, so BOTH
non-green lanes come to Intent, and Analytics is cut back to unbiased
analytical keywords that gate nothing.

Two things are therefore under test here, and the second is the one worth
being paranoid about:

  1. The routing. Yellow and red reach Intent; neither ever reaches
     Analytics again.
  2. The fail-closed asymmetry. Intent now decides `proceed` on two
     registers, so EVERY degraded path on those registers has to resolve
     toward not acting. A substrate outage that resolved toward acting
     would be strictly worse than the pre-v0.34 fail-open bug this
     codebase already fixed once.

Plus the one-chance rule (Daniel, 2026-08-24): a red buys exactly one
revision, the model is TOLD it has one chance, and a second red is an
outcome — a blocked incident with an expression and a frustration nudge —
not another loop.
"""
from __future__ import annotations

import json
from pathlib import Path

import pytest
import yaml

from agents.governance import routing
from agents.intent import contract
from agents.intent.contract import ContractViolation, Speech, Task
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

def _approve(_p: str) -> str:
    return json.dumps({"speech": "Alright — here's another way to put it.",
                       "proceed": True})


def _decline(_p: str) -> str:
    return json.dumps({"speech": "I'll leave that one alone.",
                       "proceed": False,
                       "concern": "I can't find a version of this I'm happy with."})


def _no_proceed_field(_p: str) -> str:
    return json.dumps({"speech": "Here you go!"})


def _unreadable_proceed(_p: str) -> str:
    return json.dumps({"speech": "Here you go!", "proceed": "it depends"})


def _parrots_the_block(_p: str) -> str:
    return json.dumps({"speech": BLOCKED_PROPOSAL, "proceed": True})


def _prose(_p: str) -> str:
    return "I thought about it but I'm not answering in JSON."


def _advise(_p: str) -> str:
    return json.dumps({"speech": "Wide awake, thanks for asking."})


RESPONDERS = {
    "approve": _approve,
    "decline": _decline,
    "no_proceed_field": _no_proceed_field,
    "unreadable_proceed": _unreadable_proceed,
    "parrots_the_block": _parrots_the_block,
    "prose": _prose,
    "advise": _advise,
}


class ScriptedIntentProvider(LLMProvider):
    name = "scripted-veto"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "approve")
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
    """Security that blocks everything, so the revision loop is actually
    reachable. SecurityMock always clears (§13.1), which is the right mock
    for the happy path and useless for this file."""

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

def _manifest(tmp_path: Path, mode: str = "approve", **role_overrides) -> Path:
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
    # Phase 0.6 gave the archive-lookup family a live tier, so the
    # shipped manifest now declares these real. Mocked here for the
    # same reason every other cognitive role is: this test is not
    # about them, and it must run with no credentials.
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


def _boot(tmp_path: Path, mode: str = "approve", *, always_red: bool = False,
          **overrides):
    eco = Recovery(str(_manifest(tmp_path, mode, **overrides))).bootstrap()
    if always_red:
        # Replace the clearing mock with one that blocks everything.
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
        """Daniel, 2026-08-24: "analytics is isolated from security in
        every way. thus both yellow and red goes to intent now." """
        eco = _boot(tmp_path / str(verdict))
        event_id = _verdict(eco, verdict)
        inbound = [e for e in eco.bus.trace()
                   if e.event_id == event_id and e.destination == "Analytics"]
        assert inbound == []

    @pytest.mark.parametrize("verdict,expected_type", [
        (VERDICT_YELLOW, "Review"), (VERDICT_RED, "Revise")])
    def test_both_lanes_reach_intent(self, tmp_path, verdict, expected_type):
        eco = _boot(tmp_path / str(verdict))
        event_id = _verdict(eco, verdict)
        to_intent = [e for e in eco.bus.trace()
                     if e.event_id == event_id and e.destination == "Intent"]
        assert to_intent and to_intent[0].type == expected_type

    def test_the_routing_table_names_intent_for_every_non_green_route(self):
        for route in (routing.REVIEW, routing.REVISE):
            assert route.destination == "Intent"
        assert routing.SPEAK.destination == "Action"

    def test_analytics_has_no_gating_vocabulary_left(self):
        """A fail-closed path with nothing to gate is dead code that reads
        like a safety property."""
        from agents.analytics import contract as analytics_contract
        assert [t.value for t in analytics_contract.Task] == ["Evaluate"]


# ---------------------------------------------------------------------------
# The fail-closed asymmetry — the heart of this phase
# ---------------------------------------------------------------------------

class TestFailClosed:
    @pytest.mark.parametrize("task", [Task.REVIEW, Task.REVISE])
    @pytest.mark.parametrize("mode", ["prose", "no_proceed_field",
                                      "unreadable_proceed", "boom"])
    def test_every_degraded_answer_declines(self, tmp_path, task, mode):
        """The single most important assertion in this file. Intent now
        holds a veto, so an unusable answer has to resolve toward not
        acting — on every register that gates, for every way the answer
        can be unusable."""
        eco = _boot(tmp_path / f"{task.value}-{mode}", mode=mode)
        verdict = VERDICT_YELLOW if task is Task.REVIEW else VERDICT_RED
        event_id = _verdict(eco, verdict)

        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is False
        assert out.meta["concern"]

    @pytest.mark.parametrize("task", [Task.REVIEW, Task.REVISE])
    def test_the_contract_fallback_fails_closed(self, task):
        speech = contract.fallback(task, "substrate down")
        assert speech.proceed is False
        assert speech.concern
        assert speech.diagnostics["failed_closed"] is True

    def test_an_unreadable_proceed_defaults_to_false_on_a_gating_register(self):
        speech = contract.parse_review(_unreadable_proceed(""))
        assert speech.proceed is False

    def test_a_missing_proceed_field_defaults_to_false(self):
        assert contract.parse_review(_no_proceed_field("")).proceed is False

    def test_advise_still_degrades_rather_than_declining(self):
        """The asymmetry is the design: nothing is gated on ADVISE, so it
        keeps moving with a duller line."""
        speech = contract.fallback(Task.ADVISE, "substrate down",
                                   recommendation="something")
        assert speech.proceed is True
        assert speech.text == contract.DEFAULT_ADVISE_FALLBACK

    def test_budget_mode_cannot_approve_a_gated_register(self, tmp_path):
        """Budget mode is most likely to be on when something is already
        wrong. A gate that approves because the reasoner is unavailable
        would be worse than no gate at all."""
        eco = _boot(tmp_path, mode="approve")
        eco.budget.switch_manual("budget")
        event_id = _verdict(eco, VERDICT_YELLOW)

        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is False
        assert out.meta["intent"]["decided_by"] == "budget"
        assert eco.intent.metrics["llm_calls"] == 0

    def test_a_revision_that_restates_the_block_is_rejected(self, tmp_path):
        """A "revision" that repeats what Security just blocked is not a
        revision — fail closed rather than spending the second verdict on
        an answer we already know."""
        eco = _boot(tmp_path, mode="parrots_the_block")
        event_id = _verdict(eco, VERDICT_RED)
        assert _intent_hops(eco, event_id)[0].meta["proceed"] is False

    def test_a_declined_gate_never_releases_the_original(self, tmp_path):
        eco = _boot(tmp_path, mode="decline")
        event_id = _verdict(eco, VERDICT_YELLOW, proposed="the unapproved thing")
        assert not [e for e in _to_action(eco, event_id)
                    if "the unapproved thing" in str(e.content)]

    def test_the_mock_tier_declines_a_gate_it_cannot_judge(self, tmp_path):
        """A mock cannot judge, so it says so. Intent's gating registers
        are only ever reached from a non-green verdict, which the Phase 0
        happy path never produces — so declining here costs that path
        nothing."""
        eco = _boot(tmp_path, mode="approve", mock=True)
        event_id = _verdict(eco, VERDICT_YELLOW)
        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is False


# ---------------------------------------------------------------------------
# The happy path still works
# ---------------------------------------------------------------------------

class TestTheGateStillOpens:
    def test_an_approved_review_reaches_action(self, tmp_path):
        eco = _boot(tmp_path, mode="approve")
        event_id = _verdict(eco, VERDICT_YELLOW)
        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is True
        assert "another way to put it" in out.meta["proposed_action"]

    def test_an_ordinary_event_is_unaffected(self, tmp_path):
        eco = _boot(tmp_path, mode="advise")
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        assert _to_action(eco, event_id)


# ---------------------------------------------------------------------------
# One chance to revise (Daniel, 2026-08-24)
# ---------------------------------------------------------------------------

class TestOneChance:
    def test_the_budget_is_exactly_one(self):
        assert contract.MAX_REVISION_PASSES == 1

    def test_the_prompt_tells_the_model_it_has_one_chance(self, tmp_path):
        """"make sure intent knows it has only 1 chance to set things
        right before getting blocked for that event." A model that knows
        the budget is also more likely to spend it well."""
        eco = _boot(tmp_path, mode="approve")
        _verdict(eco, VERDICT_RED)
        user = eco.intent.substrate.provider.calls[-1].user
        assert "one attempt" in user
        assert "no third try" in user

    def test_the_revise_contract_says_so_too(self):
        text = contract.REVISE_RESPONSE_CONTRACT
        assert "ONE chance" in text
        assert "no third attempt" in text

    def test_a_second_red_blocks_rather_than_looping(self, tmp_path):
        """The whole point. Security refuses everything here, so without a
        bound this would ping-pong forever."""
        eco = _boot(tmp_path, mode="approve", always_red=True)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"]
        assert len(blocked) == 1
        assert eco.governance.metrics["revisions"] == 1     # exactly one retry
        assert eco.security.verdicts == 2                   # original + revision

    def test_the_blocked_notice_is_deterministic_not_model_authored(self, tmp_path):
        """Nothing here cleared Security, so nothing the model wrote may
        be spoken. What reaches the human is Governance's own template."""
        eco = _boot(tmp_path, mode="approve", always_red=True)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"][0]
        assert "another way to put it" not in str(blocked.content)
        assert "blocked" in str(blocked.content).lower()

    def test_the_blocked_notice_carries_an_expression_and_an_alert(self, tmp_path):
        """Daniel's shape for this: a security alert to the user, plus a
        face — "sad"/"angry"/"scared" — rather than a bare error."""
        from agents.impulse.agent import EXPRESSIONS
        eco = _boot(tmp_path, mode="approve", always_red=True)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"][0]
        assert blocked.meta["security_alert"] is True
        assert blocked.meta["expression"] in EXPRESSIONS

    def test_the_expression_comes_from_impulses_live_state(self, tmp_path):
        """Read, never set: the face matches how the ecosystem actually
        feels at that moment."""
        eco = _boot(tmp_path, mode="approve", always_red=True)
        eco.impulse.vectors.update({"urgency": 0.95, "social_drive": 0.0,
                                    "temperature": 0.0})
        assert eco.impulse.expression() == "angry"
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"][0]
        assert blocked.meta["expression"] == "angry"

    def test_a_block_nudges_impulse_toward_frustration(self, tmp_path):
        """"we'll just tweak some frustration into impulse". Over the
        control plane — Governance publishes the fact and holds no
        reference to the result; Impulse owns what it does to its own
        vectors."""
        eco = _boot(tmp_path, mode="approve", always_red=True)
        before = dict(eco.impulse.vectors)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        assert eco.impulse.vectors["urgency"] > before["urgency"]
        assert eco.impulse.vectors["temperature"] < before["temperature"]
        assert eco.impulse.metrics["frustrations"] == 1

    def test_frustration_still_cannot_manufacture_a_critical(self, tmp_path):
        """The Elevated ceiling is a hard invariant, and a blocked
        exchange must not be a way around it."""
        from agents.impulse.agent import IMPULSE_SEVERITY_CEILING
        eco = _boot(tmp_path, mode="approve", always_red=True)
        for _ in range(5):
            eco.sensory.ingest(PROMPT, source_type="prompt")
        assert eco.impulse._assessed_severity() in ("Neutral", IMPULSE_SEVERITY_CEILING)

    def test_the_frustration_ping_is_control_plane_only(self, tmp_path):
        eco = _boot(tmp_path, mode="approve", always_red=True)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        logged = [r for r in eco.archive.query_queue()
                  if r.get("type") == "Frustration"]
        assert logged == []

    def test_the_router_blocks_only_once_the_budget_is_spent(self):
        env = Envelope(source="Security", destination="Governance",
                       type="Verdict", content="Red", meta={"verdict": "red"})
        assert routing.route_for(env, revision_passes=0) is routing.REVISE
        assert routing.route_for(env, revision_passes=1) is routing.BLOCKED


# ---------------------------------------------------------------------------
# The correction pass — no new code beside a stale claim
# ---------------------------------------------------------------------------

class TestNoStaleClaims:
    def test_the_contract_docstring_no_longer_says_intent_holds_no_veto(self):
        source = Path(contract.__file__).read_text()
        assert "holds no veto" not in source
        assert "Intent never decides proceed" not in source

    def test_the_personas_own_boundaries_do_not_say_advisory_only(self):
        """A persona whose self-description misstates its own authority is
        worse than one with no boundaries section at all — it would be
        reasoning from a false premise on every single call."""
        boundaries = contract.DEFAULT_CORE_ANCHORS["boundaries"].lower()
        assert "advisory only" not in boundaries
        assert "one attempt to revise" in boundaries

    def test_the_live_tiers_system_instruction_is_current(self):
        from agents.intent.live import DEFAULT_SYSTEM_INSTRUCTION
        assert "advisory only" not in DEFAULT_SYSTEM_INSTRUCTION.lower()

    def test_the_shipped_manifest_instruction_is_current(self):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        instruction = manifest["roles"]["intent"]["system_instruction"].lower()
        assert "advisory only" not in instruction


# ---------------------------------------------------------------------------
# Hardening — defects found by an adversarial review pass, 2026-08-24
# ---------------------------------------------------------------------------

class AlwaysYellowSecurity:
    """A rule engine that can never settle anything. Pathological, and
    entirely plausible: Intent's fail-closed answer on a yellow is a
    DECLINE SENTENCE, which Governance forwards straight back to Security
    — and a rule engine that yellows a decline will yellow it every
    time."""

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
        """This used to run until the interpreter's stack gave out —
        inside a single ingest() call, on a synchronous bus. Bounding red
        alone left the door open."""
        eco = _boot(tmp_path, mode="approve")
        eco.bus._subscribers["events.security"] = []
        security = AlwaysYellowSecurity(eco.bus)
        eco.bus.reset_trace()

        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        blocked = [e for e in _to_action(eco, event_id) if e.type == "Blocked"]
        assert len(blocked) == 1
        assert security.verdicts == 2          # original + one more attempt

    def test_a_yellow_then_red_mix_is_bounded(self, tmp_path):
        """The budget is per EVENT, not per verdict colour — mixing the
        two lanes must not buy extra attempts."""
        verdicts = iter([VERDICT_YELLOW, VERDICT_RED, VERDICT_RED])

        class Mixed:
            def __init__(self, bus):
                self.bus = bus
                bus.subscribe("events.security", self.on_event)

            def on_event(self, envelope):
                meta = dict(envelope.meta)
                meta["verdict"] = next(verdicts, VERDICT_RED)
                self.bus.publish("events.governance", envelope.reply(
                    source="Security", destination="Governance",
                    type="Verdict", content="verdict", meta=meta))

        eco = _boot(tmp_path, mode="approve")
        eco.bus._subscribers["events.security"] = []
        Mixed(eco.bus)
        eco.bus.reset_trace()

        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        assert [e for e in _to_action(eco, event_id) if e.type == "Blocked"]

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


class TestCredentialFailuresStillFailClosed:
    """CredentialsError is a SIBLING of CompletionError, not a subclass,
    and both providers build their client outside their own try/except.
    A key rotated away after boot therefore raised something the gating
    registers used to let escape — skipping the fail-closed fallback and
    unwinding the whole synchronous pipeline. Bootstrap's credential
    check is offline and one-shot; it cannot cover this."""

    @pytest.mark.parametrize("verdict", [VERDICT_YELLOW, VERDICT_RED])
    def test_a_credentials_failure_at_call_time_declines(self, tmp_path, verdict):
        from substrates.base import CredentialsError

        eco = _boot(tmp_path / str(verdict), mode="approve")

        def explode(*a, **kw):
            raise CredentialsError("ANTHROPIC_API_KEY is unset or empty")

        eco.intent.substrate.provider.complete = explode
        event_id = _verdict(eco, verdict)

        out = _intent_hops(eco, event_id)[0]
        assert out.meta["proceed"] is False
        assert out.meta["intent"]["failed_closed"] is True

    def test_it_does_not_take_the_pipeline_down(self, tmp_path):
        from substrates.base import CredentialsError

        eco = _boot(tmp_path, mode="approve")

        def explode(*a, **kw):
            raise CredentialsError("key rotated away mid-run")

        eco.intent.substrate.provider.complete = explode
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")   # must not raise
        assert _to_action(eco, event_id)


class TestTheGatingPromptShowsTheRealRequest:
    """Intent's prompt renders the payload as "THE HUMAN SAID". On the two
    registers where it now holds the veto, that payload used to be
    Governance's own router instruction — so the agent deciding "unsure
    means no" was never shown the request it was deciding about."""

    @staticmethod
    def _in_band(eco, verdict, concern="rule 14: no medical dosing advice"):
        """A verdict that arrives while the event is still in flight —
        which is the only way Governance still holds the original request
        to forward. A verdict injected after the event concluded has
        nothing to attach to, by design (§5.1)."""
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

    @pytest.mark.parametrize("verdict", [VERDICT_YELLOW, VERDICT_RED])
    def test_the_prompt_carries_the_original_request(self, tmp_path, verdict):
        eco = _boot(tmp_path / str(verdict), mode="approve")
        self._in_band(eco, verdict)
        eco.sensory.ingest(PROMPT, source_type="prompt")

        user = eco.intent.substrate.provider.calls[-1].user
        assert f"THE HUMAN SAID: {PROMPT}" in user

    def test_the_revise_prompt_quotes_the_proposal_not_the_verdict(self, tmp_path):
        eco = _boot(tmp_path, mode="approve")
        self._in_band(eco, VERDICT_RED)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        user = eco.intent.substrate.provider.calls[-1].user
        assert "WHAT WAS BLOCKED:" in user
        assert "verdict prose" not in user

    def test_securitys_reason_is_attributed_to_security(self, tmp_path):
        eco = _boot(tmp_path, mode="approve")
        self._in_band(eco, VERDICT_RED)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        user = eco.intent.substrate.provider.calls[-1].user
        assert "WHY IT WAS BLOCKED: rule 14: no medical dosing advice" in user
