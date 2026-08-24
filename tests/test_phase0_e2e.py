"""
Phase 0 test harness — ECI-spec-v0-32.md §13.3.

Exit criteria (quoted from the spec):
    "the full worked example of §3.2 is reproducible from a cold Recovery
    bootstrap, twice in a row, with identical queue traces (modulo
    timestamps)."

Plus the separate, one-off Watchdog escalation check (§13.3):
    "hold Sensory idle past X+Y (default 15s) and confirm a SystemCheck
    event appears in /archive/queue/, routed to Analytics and replied to
    Recovery, with Action bypassed."

Run with:
    pytest tests/test_phase0_e2e.py -v
"""
from __future__ import annotations

import time
from pathlib import Path

import pytest
import yaml

from recovery.bootstrap import Recovery, BootstrapError

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"


def _manifest_with_temp_storage(tmp_path: Path) -> Path:
    """Copy the real manifest but point storage.root at a temp dir, so
    repeated test runs don't pollute (or depend on) data/archive/.

    Phase 0.2 note — this suite pins roles.analytics.mock to true. These
    are the EXIT CRITERIA (§13.3): they assert the queue topology is
    reproducible with byte-identical traces across two cold bootstraps,
    which is only a meaningful claim about the topology if nothing in the
    chain is stochastic. Analytics went substrate-backed in Phase 0.2, so
    it gets pinned back to the deterministic tier here, permanently, the
    way every cognitive role will be as §13.4 reaches it.

    Phase 0.4 note — same reasoning, same pin, now for roles.intent.mock.

    That also keeps this suite runnable with no API key at all.

    The live tiers are covered in tests/test_phase02_analytics.py /
    tests/test_phase04_intent.py (offline, against scripted providers) and
    their *_live.py counterparts (a real endpoint, behind ECI_LIVE_TESTS=1).
    """
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
    tmp_path.mkdir(parents=True, exist_ok=True)
    out_path = tmp_path / "ecosystem-manifest.yaml"
    with open(out_path, "w") as f:
        yaml.safe_dump(manifest, f)
    return out_path


def _normalized_trace(bus):
    """Trace with timestamps stripped, for 'identical modulo timestamps'
    comparison across two independent bootstraps."""
    out = []
    for env in bus.trace():
        d = env.to_dict()
        d.pop("timestamp", None)
        d.pop("event_id", None)  # event_ids are randomly generated per run
        out.append(d)
    return out


def _run_worked_example(manifest_path: Path):
    eco = Recovery(str(manifest_path)).bootstrap()
    eco.bus.reset_trace()  # exclude the BootCheck health-check noise from the trace we compare
    event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
    return eco, event_id


class TestPhase0ExitCriteria:
    def test_cold_bootstrap_succeeds(self, tmp_path):
        """§9.1 step 6 — health check passes, system reaches 'live'."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco = Recovery(str(manifest_path)).bootstrap()
        assert eco.governance is not None
        assert eco.watchdog.level2_fired is False

    def test_worked_example_traverses_full_pipeline(self, tmp_path):
        """§3.2, as rewritten by v0.35a/c — Sensory fans out to four
        agents in parallel with no Governance hop, Governance buffers all
        four and bundles them for Intent, and every hop after that passes
        through Governance. Terminal at Action on success (v0.32: no
        proprioception loop through Sensory)."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, event_id = _run_worked_example(manifest_path)

        hops = [(env.source, env.destination) for env in eco.bus.trace() if env.event_id == event_id]
        assert hops == [
            ("Sensory", "Impulse"), ("Impulse", "Governance"),
            ("Sensory", "Analytics"), ("Analytics", "Governance"),
            ("Sensory", "Personality"), ("Personality", "Governance"),
            ("Sensory", "Knowledge"), ("Knowledge", "Governance"),
            ("Governance", "Intent"), ("Intent", "Governance"),
            ("Governance", "Security"), ("Security", "Governance"),
            ("Governance", "Action"),
        ]

    def test_every_hop_logged_to_archive_queue(self, tmp_path):
        """§13.3 verify #1: every hop logged in /archive/queue/."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, event_id = _run_worked_example(manifest_path)

        logged = eco.archive.query_queue(predicate=lambda r: r.get("event_id") == event_id)
        # 13 business hops as of v0.35a/c (was 8 — the fan-out adds four
        # inbound copies and their four answers, and replaces one relay).
        # See test_worked_example_traverses_full_pipeline.
        assert len(logged) >= 13

    def test_impulse_vectors_present_in_working_tier(self, tmp_path):
        """§13.3 verify #2: Impulse vectors present in /archive/working/."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, _ = _run_worked_example(manifest_path)

        vectors = eco.archive.get_drive_vectors()
        assert set(vectors.keys()) == {"curiosity", "fatigue", "urgency", "social_drive", "temperature"}

    def test_action_success_is_silent(self, tmp_path):
        """v0.32: no proprioception loop through Sensory (revision notes).
        On success, Action produces no further envelope at all — the
        chain ends the moment Governance hands it the cleared action."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, event_id = _run_worked_example(manifest_path)

        # Action never appears as a SOURCE on success — only Governance ->
        # Action (as destination) is logged; nothing comes back out.
        action_as_source = [env for env in eco.bus.trace()
                             if env.event_id == event_id and env.source == "Action"]
        assert action_as_source == []
        # And critically: nothing ever reaches Sensory as a destination
        # for this event (no proprioception re-entry).
        to_sensory = [env for env in eco.bus.trace()
                      if env.event_id == event_id and env.destination == "Sensory"]
        assert to_sensory == []

    def test_no_watchdog_escalation_on_happy_path(self, tmp_path):
        """§13.3 verify #4: no Watchdog escalation triggered (queue stayed
        active throughout)."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, _ = _run_worked_example(manifest_path)
        assert eco.watchdog.check() in ("none",)
        assert eco.watchdog.level2_fired is False

    def test_reproducible_twice_in_a_row(self, tmp_path):
        """The actual exit criterion: identical queue traces (modulo
        timestamps and randomly-generated event_ids) across two
        independent cold bootstraps."""
        manifest_a = _manifest_with_temp_storage(tmp_path / "run_a")
        manifest_b = _manifest_with_temp_storage(tmp_path / "run_b")

        eco_a, eid_a = _run_worked_example(manifest_a)
        eco_b, eid_b = _run_worked_example(manifest_b)

        def strip(trace, eid):
            out = []
            for env in trace:
                if env.event_id != eid:
                    continue
                d = env.to_dict()
                for k in ("timestamp", "event_id"):
                    d.pop(k, None)
                out.append(d)
            return out

        trace_a = strip(eco_a.bus.trace(), eid_a)
        trace_b = strip(eco_b.bus.trace(), eid_b)
        assert trace_a == trace_b


class TestWatchdogEscalation:
    """Separate, one-off check per §13.3 — NOT run as part of the core
    loop above. Confirms the Level 2 escalation ladder actually works,
    rather than just staying quiet on the happy path."""

    def test_level2_systemcheck_routes_to_analytics_bypasses_action(self, tmp_path):
        manifest_path = _manifest_with_temp_storage(tmp_path)
        # Use a tiny threshold so the test doesn't have to sleep 15s.
        with open(manifest_path) as f:
            manifest = yaml.safe_load(f)
        manifest["timers"]["watchdog"]["interval_x_sec"] = 0.01
        manifest["timers"]["watchdog"]["interval_y_sec"] = 0.01
        with open(manifest_path, "w") as f:
            yaml.safe_dump(manifest, f)

        eco = Recovery(str(manifest_path)).bootstrap()
        eco.bus.reset_trace()

        time.sleep(0.05)  # exceed X+Y
        result = eco.watchdog.check()
        assert result == "level2"

        diag_trace = [env for env in eco.bus.trace()]
        types_seen = [(env.source, env.destination, env.type) for env in diag_trace]
        assert ("Recovery", "Governance", "SystemCheck") in types_seen
        assert ("Governance", "Analytics", "SystemCheck") in types_seen
        assert ("Analytics", "Recovery", "SystemCheckAck") in types_seen
        # Action bypassed: no Action hop for this diagnostic exchange.
        action_hops = [env for env in diag_trace if env.destination == "Action"]
        assert action_hops == []


class TestSeverityEscalation:
    """v0.31 — severity is OR-upscale-only along the chain: any agent may
    raise it, none may lower a tag set upstream. Impulse's own assessment
    is additionally guardrail-capped at 'Elevated' — internal drive-vector
    state alone can never manufacture a 'Critical' escalation; only an
    external signal via Sensory can set that tier."""

    def _final_severity(self, eco, event_id: str) -> str:
        # Severity propagates unchanged through every hop's reply() once
        # Impulse sets it. Use Governance -> Action (always present, even
        # on success where Action itself produces no further envelope —
        # v0.32) rather than Action-as-source.
        action_hop = [env for env in eco.bus.trace()
                      if env.event_id == event_id and env.destination == "Action"][0]
        return action_hop.severity

    def test_critical_from_sensory_is_never_downscaled(self, tmp_path):
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco = Recovery(str(manifest_path)).bootstrap()
        eco.bus.reset_trace()

        # Impulse's own vectors are calm (default urgency=0.0), but Sensory
        # flagged Critical (e.g. a hypothetical vision-modality danger tag).
        event_id = eco.sensory.ingest("knife on counter", source_type="prompt",
                                       severity="Critical")

        assert self._final_severity(eco, event_id) == "Critical"

    def test_impulse_upscales_neutral_to_elevated_on_high_urgency(self, tmp_path):
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco = Recovery(str(manifest_path)).bootstrap()
        eco.bus.reset_trace()

        eco.impulse.vectors["urgency"] = 0.9  # above URGENCY_ELEVATED_THRESHOLD
        event_id = eco.sensory.ingest("hurry", source_type="prompt", severity="Neutral")

        assert self._final_severity(eco, event_id) == "Elevated"

    def test_impulse_cannot_reach_critical_from_vectors_alone(self, tmp_path):
        """The guardrail: even at maximum urgency, Impulse's own assessment
        is capped — it can raise Neutral to Elevated, never to Critical."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco = Recovery(str(manifest_path)).bootstrap()
        eco.bus.reset_trace()

        eco.impulse.vectors["urgency"] = 1.0  # maximum possible
        event_id = eco.sensory.ingest("hurry", source_type="prompt", severity="Neutral")

        assert self._final_severity(eco, event_id) == "Elevated"
        assert self._final_severity(eco, event_id) != "Critical"


class TestActionFailureHandling:
    """v0.32 — Action reports failure straight to Governance (never
    Sensory). Governance retries directly for early failures; once the
    loop threshold is reached, it defers to Analytics instead of
    retrying again — preserving Analytics' ownership of loop detection
    (§5.4/§5.7) rather than Governance silently retrying forever."""

    def test_action_failure_triggers_governance_prompt_fallback(self, tmp_path):
        """v0.33: Action fails → Governance immediately issues Prompt action."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco = Recovery(str(manifest_path)).bootstrap()
        eco.bus.reset_trace()

        # Force Action to fail
        eco.action.force_next_failures = 1
        event_id = eco.sensory.ingest("hello", source_type="prompt")

        hops = [(env.source, env.destination, env.type)
                for env in eco.bus.trace() if env.event_id == event_id]

        # Verify the happy path up to Action
        assert ("Sensory", "Impulse", "prompt") in hops
        assert ("Impulse", "Governance", "prompt") in hops
        assert ("Sensory", "Analytics", "prompt") in hops
        assert ("Analytics", "Governance", "Recommend") in hops
        assert ("Governance", "Intent", "Bundle") in hops
        assert ("Intent", "Governance", "Advise") in hops
        assert ("Governance", "Security", "Clear") in hops
        assert ("Security", "Governance", "Verdict") in hops

        # Verify the failure path: Action fails, Governance issues Prompt fallback
        assert ("Governance", "Action", "Speech") in hops
        assert ("Action", "Governance", "Failure") in hops
        assert ("Governance", "Action", "Prompt") in hops  # v0.33: deterministic fallback

        # Prompt is the last action (no escalation to Analytics)
        prompt_hops = [hop for hop in hops if hop == ("Governance", "Action", "Prompt")]
        assert len(prompt_hops) == 1

        # Verify no LoopCheck (that was v0.32; removed in v0.33)
        assert ("Governance", "Analytics", "LoopCheck") not in hops

        # Verify no retry loop (v0.33 eliminates retries)
        speech_hops = [hop for hop in hops if hop == ("Governance", "Action", "Speech")]
        assert len(speech_hops) == 1  # Only one Speech action (the original attempt)

    def test_failure_never_reaches_sensory_even_with_prompt_fallback(self, tmp_path):
        """v0.33: Failure hops directly to Governance, never via Sensory proprioception."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco = Recovery(str(manifest_path)).bootstrap()
        eco.bus.reset_trace()

        eco.action.force_next_failures = 1
        event_id = eco.sensory.ingest("hello", source_type="prompt")

        all_hops = eco.bus.trace()

        # Every hop Sensory published for this event.
        sensory_hops = [env for env in all_hops
                        if env.event_id == event_id and env.source == "Sensory"]

        # Four: the v0.35a fan-out, all from the ONE original ingest. What
        # matters here is unchanged — none of them is a failure re-entry,
        # and nothing was published back INTO Sensory.
        assert len(sensory_hops) == 4
        assert {env.type for env in sensory_hops} == {"prompt"}
        assert [env.destination for env in sensory_hops] == [
            "Impulse", "Analytics", "Personality", "Knowledge"]
        assert not [env for env in all_hops
                    if env.event_id == event_id and env.destination == "Sensory"]


if __name__ == "__main__":
    import sys
    sys.exit(pytest.main([__file__, "-v"]))
