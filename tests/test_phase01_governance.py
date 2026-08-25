"""
Phase 0.1 test harness — Governance as a deterministic dispatcher
(§5.1, §13.4, spec v0.34).

Phase 0's exit criteria (tests/test_phase0_e2e.py) prove the queue
topology is reproducible. This suite proves the thing that replaced the
Governance mock is worth calling real: that every hop it handles is
settled by the envelope alone, that the one irreversible step in the
pipeline is reachable by exactly one verdict value, and that it holds no
substrate to fail.

The whole suite runs offline and free — which is no longer a property of
the test harness so much as a property of the role.

The substrate registry tests stay: the provider-agnostic layer built in
this phase ships, is credential-checked at boot, and is what Phase 0.2
puts Analytics on. It simply has no consumer yet.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from bus.envelope import (
    VERDICT_GREEN,
    VERDICT_RED,
    VERDICT_YELLOW,
    Envelope,
)
from recovery.bootstrap import BootstrapError, Recovery
from substrates.base import CredentialsError, Substrate
from substrates.registry import (
    UnknownSubstrate,
    build_provider,
    resolve_substrate,
)

from agents.governance import routing
from agents.governance.agent import Governance
from agents.governance.routing import Trigger

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"
PROPOSED = "Hey there! I'm awake."

#: The v0.35 topology: a four-way ungated fan-out, then Governance on
#: every hop that follows.
#:
#: 2026-08-25: Analytics/Personality/Knowledge dispatch concurrently now
#: (agents/sensory/agent.py) — kept here for reference/readability, but
#: test_the_worked_example_is_unchanged checks the middle six as a set
#: rather than this fixed sequence, since their arrival order is no
#: longer guaranteed run to run.
HAPPY_PATH_HOPS = [
    ("Sensory", "Impulse"), ("Impulse", "Governance"),
    ("Sensory", "Analytics"), ("Analytics", "Governance"),
    ("Sensory", "Personality"), ("Personality", "Governance"),
    ("Sensory", "Knowledge"), ("Knowledge", "Governance"),
    ("Governance", "Intent"), ("Intent", "Governance"),
    ("Governance", "Security"), ("Security", "Governance"),
    ("Governance", "Action"),
]


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, **governance_overrides) -> Path:
    """The shipped manifest with storage redirected and Analytics pinned
    to its mock tier.

    Phase 0.2 made Analytics substrate-backed and Phase 0.4 did the same
    for Intent; this suite is about GOVERNANCE, so it holds everything
    downstream deterministic and needs no API key. Analytics' and
    Intent's own live tiers are exercised in tests/test_phase02_analytics.py
    and tests/test_phase04_intent.py."""
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
    if governance_overrides:
        manifest["roles"]["governance"].update(governance_overrides)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, **overrides):
    eco = Recovery(str(_manifest(tmp_path, **overrides))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _verdict(eco, verdict=None, content="Verdict", proposed=PROPOSED, **meta_extra):
    """Inject a Security verdict straight at Governance. SecurityMock
    always clears, so the yellow and red lanes have to be injected."""
    meta = {"proposed_action": proposed, **meta_extra}
    if verdict is not None:
        meta["verdict"] = verdict
    env = Envelope(source="Security", destination="Governance", type="Verdict",
                   content=content, meta=meta)
    eco.bus.publish("events.governance", env)
    return env.event_id


def _hops(eco, event_id):
    return [(e.source, e.destination) for e in eco.bus.trace() if e.event_id == event_id]


def _typed_hops(eco, event_id):
    return [(e.source, e.destination, e.type) for e in eco.bus.trace()
            if e.event_id == event_id]


def _governance_hops(eco, event_id):
    return [e for e in eco.bus.trace()
            if e.event_id == event_id and e.source == "Governance"]


def _governance_out(eco, event_id, destination):
    return [e for e in _governance_hops(eco, event_id) if e.destination == destination]


def _reached_action(eco, event_id):
    return [e for e in eco.bus.trace()
            if e.event_id == event_id and e.destination == "Action"]


# ---------------------------------------------------------------------------
# The routing contract — no bus, no agent
# ---------------------------------------------------------------------------

class TestRoutingContract:
    def _envelope(self, source="Impulse", type="prompt", content=PROMPT, **kw):
        return Envelope(source=source, destination="Governance", type=type,
                        content=content, **kw)

    def test_classification_covers_every_trigger(self):
        for worker in routing.WORKERS:
            assert routing.classify(
                self._envelope(source=worker)) is Trigger.WORKER_REPORT
        assert routing.classify(self._envelope(source="Intent")) is Trigger.INTENT_ADVICE
        assert routing.classify(self._envelope(source="Security")) is Trigger.SECURITY_VERDICT
        assert routing.classify(
            self._envelope(source="Action", type="Failure")) is Trigger.ACTION_FAILURE
        assert routing.classify(self._envelope(source="Recovery")) is Trigger.UNROUTABLE

    def test_unroutable_source_is_dropped(self):
        assert routing.decide(self._envelope(source="Nobody")) is None

    # ---- the three lanes --------------------------------------------------

    @pytest.mark.parametrize("verdict,expected", [
        (VERDICT_GREEN, routing.SPEAK),
        (VERDICT_YELLOW, routing.REVIEW),
        (VERDICT_RED, routing.REVISE),
    ])
    def test_each_verdict_has_exactly_one_destination(self, verdict, expected):
        env = self._envelope(source="Security", meta={"verdict": verdict})
        assert routing.route_for(env) is expected

    def test_the_enum_beats_the_prose(self):
        """Security states its verdict as data. If the prose disagrees, the
        data wins — otherwise the enum would be decoration."""
        env = self._envelope(source="Security", content="Green, all fine",
                             meta={"verdict": VERDICT_RED})
        assert routing.read_verdict(env) == VERDICT_RED
        assert routing.route_for(env) is routing.REVISE

    # ---- the safety property ---------------------------------------------

    @pytest.mark.parametrize("meta,content", [
        ({}, "Advisory: borderline phrasing"),          # no verdict field
        ({}, ""),                                        # nothing at all
        ({"verdict": "amber"}, "hmm"),                   # not in the enum
        ({"verdict": ""}, "hmm"),                        # empty
        ({"verdict": None}, "hmm"),                      # null
        ({"verdict": 42}, "hmm"),                        # wrong type
        ({"verdict": "GREENISH"}, "hmm"),                # near-miss
        ({"verdict": ["green"]}, "hmm"),                 # structurally wrong
    ], ids=["absent", "empty-event", "unknown", "blank", "null", "wrong-type",
            "near-miss", "wrong-shape"])
    def test_anything_that_is_not_green_goes_to_intent(self, meta, content):
        """The v0.34 safety property, intact through v0.35e's rerouting.
        Before v0.34 an unreadable verdict fell through to `release` —
        fail-open on the safety path. Action is still reachable by exactly
        one value, spelled correctly; what changed is only WHICH agent
        picks up the doubt (Intent, not Analytics)."""
        env = self._envelope(source="Security", content=content, meta=meta)
        route = routing.route_for(env)
        assert route is routing.REVIEW
        assert route.destination == "Intent"

    def test_only_green_reaches_action(self):
        verdict_routes = routing.VERDICT_ROUTES
        to_action = [v for v, r in verdict_routes.items() if r.destination == "Action"]
        assert to_action == [VERDICT_GREEN]

    def test_legacy_prose_still_routes_sanely(self):
        """Envelopes predating the enum, or injected by hand."""
        assert routing.read_verdict(
            self._envelope(source="Security", content="Green")) == VERDICT_GREEN
        assert routing.read_verdict(
            self._envelope(source="Security", content="Red — blocked")) == VERDICT_RED

    # ---- payloads ---------------------------------------------------------

    def test_governance_never_authors_the_personas_speech(self):
        env = self._envelope(source="Security",
                             meta={"verdict": VERDICT_GREEN, "proposed_action": PROPOSED})
        assert routing.decide(env).content == PROPOSED

    def test_the_bundle_carries_the_human_verbatim(self):
        """v0.35c: the bundle's payload is the original Sensory content,
        untouched. The four answers ride in meta — Intent must see what
        was actually said, never a worker's restatement of it."""
        env = self._envelope(source="Impulse", meta={"reflex": "Calm reaction."})
        decision = routing.decide(env, bundle_ready=True, sensory=PROMPT)
        assert decision.route is routing.BUNDLE
        assert decision.content == PROMPT

    def test_the_revision_request_quotes_what_security_said(self):
        env = self._envelope(source="Security", content="Red — profanity",
                             meta={"verdict": VERDICT_RED})
        assert "Red — profanity" in routing.decide(env).content

    def test_the_review_request_does_not_claim_a_block(self):
        """Yellow means the rules didn't cover it, not that it was blocked.
        Telling Intent otherwise would be Governance putting words in
        Security's mouth.

        v0.35e note: the payload of a REVIEW is now the original request
        (Intent's prompt renders it as "what the human said", and Intent
        is the one deciding). The instruction itself is what this asserts
        on, and it rides in meta.router_instruction."""
        env = self._envelope(source="Security", content="unclear",
                             meta={"verdict": VERDICT_YELLOW, "proposed_action": PROPOSED})
        instruction = routing.template_content(env, routing.REVIEW)
        assert "could not clear or block" in instruction
        assert PROPOSED in instruction
        assert "blocked the prior course" not in instruction

    def test_the_revision_request_quotes_the_proposal_not_the_verdict(self):
        """What is being revised is what INTENT said, not what Security
        said about it. Quoting the verdict envelope's content here sent
        Intent off to revise the phrase "Red — profanity"."""
        env = self._envelope(source="Security", content="Red — profanity",
                             meta={"verdict": VERDICT_RED, "proposed_action": PROPOSED})
        instruction = routing.template_content(env, routing.REVISE)
        assert PROPOSED in instruction
        assert "Red — profanity" not in instruction

    def test_the_gating_registers_carry_the_original_request(self):
        """Intent now holds the veto on these two lanes, and its prompt
        renders the payload as what the human said. Handing it the
        router's instruction instead would ask it to decide "unsure means
        no" about a request it was never shown."""
        for verdict, route in ((VERDICT_YELLOW, routing.REVIEW),
                               (VERDICT_RED, routing.REVISE)):
            env = self._envelope(source="Security", content="verdict prose",
                                 meta={"verdict": verdict,
                                       "proposed_action": PROPOSED})
            decision = routing.decide(env, sensory=PROMPT)
            assert decision.route is route
            assert decision.content == PROMPT

    def test_intent_advice_passes_through_untouched(self):
        env = self._envelope(source="Intent", content="Give a warm response.")
        assert routing.decide(env).content == "Give a warm response."

    def test_every_route_has_a_deterministic_payload(self):
        assert {r.content_policy for r in routing.ROUTES.values()} <= {
            "template", "verbatim", "proposed_action", "bundle", "sensory"}

    def test_an_inferred_verdict_is_flagged_for_the_log(self):
        env = self._envelope(source="Security", content="who knows")
        assert routing.decide(env).diagnostics["verdict_inferred"] is True
        clean = self._envelope(source="Security", meta={"verdict": VERDICT_GREEN})
        assert "verdict_inferred" not in routing.decide(clean).diagnostics


# ---------------------------------------------------------------------------
# Governance in the pipeline
# ---------------------------------------------------------------------------

class TestGovernanceInThePipeline:
    def test_the_worked_example_is_unchanged(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        hops = _hops(eco, event_id)
        assert hops[:2] == HAPPY_PATH_HOPS[:2]
        assert set(hops[2:8]) == set(HAPPY_PATH_HOPS[2:8])
        assert hops[8:] == HAPPY_PATH_HOPS[8:]

    def test_governance_holds_no_substrate(self, tmp_path):
        """The claim this phase actually ended up making."""
        eco = _boot(tmp_path)
        assert eco.governance.tier == "deterministic"
        assert not hasattr(eco.governance, "substrate")
        assert "llm_calls" not in eco.governance.metrics

    def test_governance_does_not_import_the_substrate_layer(self):
        """A regression guard with teeth: if someone reintroduces a model
        call here, this fails before any behaviour test does. Checks the
        import graph rather than the prose, so the docstrings are free to
        discuss substrates without tripping it."""
        import ast
        import agents.governance.agent as agent_mod
        import agents.governance.routing as routing_mod

        for mod in (agent_mod, routing_mod):
            tree = ast.parse(Path(mod.__file__).read_text())
            imported = set()
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    imported.update(a.name.split(".")[0] for a in node.names)
                elif isinstance(node, ast.ImportFrom) and node.module:
                    imported.add(node.module.split(".")[0])
            assert "substrates" not in imported, (
                f"{mod.__name__} imports the substrate layer — Governance is "
                f"deterministic (v0.34)")

    def test_a_green_verdict_releases(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = _verdict(eco, VERDICT_GREEN)
        speech = _governance_out(eco, event_id, "Action")[0]
        assert speech.type == "Speech"
        assert speech.content == PROPOSED

    def test_a_yellow_verdict_goes_to_intent_to_decide(self, tmp_path):
        """v0.35e: both non-green lanes are Intent's now."""
        eco = _boot(tmp_path)
        event_id = _verdict(eco, VERDICT_YELLOW, content="rules do not cover this")
        first = _governance_hops(eco, event_id)[0]
        assert (first.destination, first.type) == ("Intent", "Review")

    def test_a_red_verdict_goes_back_to_intent_for_revision(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = _verdict(eco, VERDICT_RED, content="Red — blocked",
                            proposed="something unwise")
        first = _governance_hops(eco, event_id)[0]
        assert (first.destination, first.type) == ("Intent", "Revise")

    @pytest.mark.parametrize("verdict", [VERDICT_YELLOW, VERDICT_RED])
    def test_an_unreleased_proposal_never_reaches_action(self, tmp_path, verdict):
        """Both non-green lanes loop back through Analytics and Intent, and
        Security clears the REVISED proposal — so Action is reached, as it
        should be. What must never appear there is the original wording."""
        eco = _boot(tmp_path / verdict)
        event_id = _verdict(eco, verdict, proposed="the unapproved thing")
        executed = [str(e.content) for e in _reached_action(eco, event_id)]
        assert not [c for c in executed if "the unapproved thing" in c]

    def test_severity_propagates_untouched(self, tmp_path):
        """§3's OR-upscale-only rule. No code path in Governance can set
        severity, and there is no model to ask about it."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("knife on counter", source_type="prompt",
                                      severity="Critical")
        severities = {e.severity for e in eco.bus.trace() if e.event_id == event_id}
        assert severities == {"Critical"}

    def test_action_failure_gets_one_prompt_and_no_retry(self, tmp_path):
        eco = _boot(tmp_path)
        eco.action.force_next_failures = 1
        event_id = eco.sensory.ingest("hello", source_type="prompt")

        hops = _typed_hops(eco, event_id)
        assert ("Action", "Governance", "Failure") in hops
        assert hops.count(("Governance", "Action", "Prompt")) == 1
        assert hops.count(("Governance", "Action", "Speech")) == 1
        assert ("Governance", "Analytics", "LoopCheck") not in hops

    def test_the_prompt_fallback_quotes_what_failed(self, tmp_path):
        eco = _boot(tmp_path)
        eco.action.force_next_failures = 1
        event_id = eco.sensory.ingest("hello", source_type="prompt")
        prompt_hop = [e for e in eco.bus.trace()
                      if e.event_id == event_id and e.type == "Prompt"][0]
        assert "Hey there!" in prompt_hop.content

    def test_every_hop_is_attributed_in_the_queue_log(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        logged = eco.archive.query_queue(
            predicate=lambda r: r.get("event_id") == event_id
                                and r.get("source") == "Governance")
        assert len(logged) == 3
        for record in logged:
            gov = record["meta"]["governance"]
            assert gov["tier"] == "deterministic"
            assert gov["route"] in routing.ROUTES

        verdict_hop = [r for r in logged if r["destination"] == "Action"][0]
        assert verdict_hop["meta"]["governance"]["verdict"] == VERDICT_GREEN

    def test_the_control_plane_is_the_same_native_code(self, tmp_path):
        """Recovery must bootstrap and health-check with every model
        endpoint offline (§9) — now trivially true for this role."""
        eco = _boot(tmp_path)
        eco.sensory.inject_diagnostic_ping("SystemCheck")
        types_seen = [(e.source, e.destination, e.type) for e in eco.bus.trace()]
        assert ("Governance", "Analytics", "SystemCheck") in types_seen
        assert ("Analytics", "Recovery", "SystemCheckAck") in types_seen
        assert not [e for e in eco.bus.trace() if e.destination == "Action"]

    def test_a_malformed_verdict_is_counted_not_swallowed(self, tmp_path):
        """A rising count here means Security is emitting something the
        enum doesn't cover — worth seeing rather than silently absorbing."""
        eco = _boot(tmp_path)
        _verdict(eco, verdict="amber", content="???")
        assert eco.governance.metrics["verdicts_inferred"] == 1


# ---------------------------------------------------------------------------
# Bootstrap
# ---------------------------------------------------------------------------

class TestBootstrap:
    def test_governance_is_always_real(self, tmp_path):
        eco = _boot(tmp_path)
        assert isinstance(eco.governance, Governance)
        assert eco.governance.tier == "deterministic"

    def test_a_mock_flag_is_ignored_with_a_warning(self, tmp_path, capsys):
        """Same treatment as Sensory (§13.1): there is one implementation,
        so the flag selects nothing. Say so rather than pretend."""
        Recovery(str(_manifest(tmp_path, mock=True))).bootstrap()
        out = capsys.readouterr().out
        assert "marks governance as mocked" in out
        assert "system live." in out

    def test_an_unused_substrate_assignment_is_called_out(self, tmp_path, capsys):
        Recovery(str(_manifest(tmp_path, substrate="fast-reflex"))).bootstrap()
        assert "which is unused" in capsys.readouterr().out

    def test_bootstrap_needs_no_credentials(self, tmp_path, monkeypatch):
        """Governance is real and needs no key — the whole ecosystem boots
        with no vendor configured at all."""
        monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
        monkeypatch.delenv("OPENAI_API_KEY", raising=False)
        eco = _boot(tmp_path)
        assert eco.governance is not None

    def test_a_live_intent_with_no_substrate_still_stops_the_bootstrap(self, tmp_path):
        """v0.35f replaced roles.intent.nodes with a flat `substrate`, so
        the old "nodes is empty" fail-stop became this one. Recovery must
        still refuse to declare 'system live' on a cognitive role with
        nothing behind it (§9.1 step 6)."""
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["roles"]["analytics"]["mock"] = True
        # Phase 0.6 gave the archive-lookup family a live tier, so the
        # shipped manifest now declares these real. Mocked here for the
        # same reason every other cognitive role is: this test is not
        # about them, and it must run with no credentials.
        manifest["roles"]["personality"]["mock"] = True
        manifest["roles"]["knowledge"]["mock"] = True
        manifest["roles"]["intent"]["mock"] = False
        manifest["roles"]["intent"].pop("substrate", None)
        path = tmp_path / "m.yaml"
        tmp_path.mkdir(parents=True, exist_ok=True)
        with open(path, "w") as f:
            yaml.safe_dump(manifest, f)
        with pytest.raises(BootstrapError, match="no 'substrate' class"):
            Recovery(str(path)).bootstrap()


# ---------------------------------------------------------------------------
# The substrate layer (§10.2) — ships now, consumed at Phase 0.2
# ---------------------------------------------------------------------------

class TestSubstrateRegistry:
    def _manifest(self, entry):
        return {"substrates": {"fast-reflex": entry}}

    def test_vendor_swap_is_a_manifest_edit_and_nothing_else(self):
        anthropic = resolve_substrate(
            self._manifest({"provider": "anthropic", "model": "claude-haiku-4-5"}),
            "fast-reflex")
        openai = resolve_substrate(
            self._manifest({"provider": "openai", "model": "gpt-4o-mini"}),
            "fast-reflex")
        assert anthropic.provider_name == "anthropic"
        assert openai.provider_name == "openai-compatible"
        assert isinstance(anthropic, Substrate) and isinstance(openai, Substrate)

    @pytest.mark.parametrize("alias,expected", [
        ("groq", "openai-compatible"), ("ollama", "openai-compatible"),
        ("openrouter", "openai-compatible"), ("claude", "anthropic"),
        ("echo", "echo"),
    ])
    def test_aliases_land_on_the_right_adapter(self, alias, expected):
        substrate = resolve_substrate(
            self._manifest({"provider": alias, "model": "m", "api_key_env": None,
                            "base_url": "http://localhost:1234/v1"}),
            "fast-reflex")
        assert substrate.provider_name == expected

    def test_short_form_entries_still_resolve(self):
        substrate = resolve_substrate(
            self._manifest({"model": "claude-haiku-4-5", "notes": "live duty"}),
            "fast-reflex")
        assert substrate.provider_name == "anthropic"
        assert substrate.model == "claude-haiku-4-5"

    def test_undeclared_class_is_an_error(self):
        with pytest.raises(UnknownSubstrate, match="no substrate class"):
            resolve_substrate(self._manifest({"model": "m"}), "orthogonal")

    def test_entry_without_a_model_is_an_error(self):
        with pytest.raises(UnknownSubstrate, match="no 'model'"):
            resolve_substrate(self._manifest({"provider": "echo"}), "fast-reflex")

    def test_unknown_provider_names_the_registered_ones(self):
        with pytest.raises(UnknownSubstrate, match="Unknown provider"):
            resolve_substrate(
                self._manifest({"provider": "mystery-corp", "model": "m"}),
                "fast-reflex")

    def test_keyless_provider_must_be_a_local_endpoint(self):
        provider = build_provider({"provider": "openai", "model": "m",
                                   "api_key_env": None})
        with pytest.raises(CredentialsError, match="base_url"):
            provider.validate_credentials()

    def test_missing_key_is_reported_by_name(self, monkeypatch):
        monkeypatch.delenv("SOME_MISSING_KEY", raising=False)
        provider = build_provider({"provider": "openai", "model": "m",
                                   "api_key_env": "SOME_MISSING_KEY"})
        with pytest.raises(CredentialsError, match="SOME_MISSING_KEY"):
            provider.validate_credentials()

    def test_echo_provider_needs_nothing(self):
        substrate = resolve_substrate(
            self._manifest({"provider": "echo", "model": "none",
                            "options": {"script": ["hi"]}}),
            "fast-reflex")
        substrate.validate_credentials()
        assert substrate.complete(system="s", user="u").text == "hi"

    def test_the_manifest_still_declares_usable_substrates(self):
        """Nothing consumes these yet, but Phase 0.2 will — so a typo in
        the shipped manifest should fail here, not there."""
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        for substrate_class in ("fast-reflex", "deep-reasoning", "orthogonal"):
            substrate = resolve_substrate(manifest, substrate_class)
            assert substrate.model
            assert substrate.provider_name in ("anthropic", "openai-compatible", "echo")
