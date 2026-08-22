"""
Phase 0 test harness — ECI-spec-v0-30.md §13.3.

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
    repeated test runs don't pollute (or depend on) data/archive/."""
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
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
        """§3.2 — Sensory+Impulse -> Governance -> Analytics -> Intent ->
        Governance -> Security -> Governance -> Action -> Sensory."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, event_id = _run_worked_example(manifest_path)

        hops = [(env.source, env.destination) for env in eco.bus.trace() if env.event_id == event_id]
        # Sensory fans out to Impulse and Governance in parallel (§3.2),
        # so both legitimately appear as separate hops; the rest of the
        # pipeline follows the worked example exactly.
        assert hops == [
            ("Sensory", "Impulse"), ("Impulse", "Governance"), ("Sensory", "Governance"),
            ("Governance", "Analytics"), ("Analytics", "Intent"), ("Intent", "Governance"),
            ("Governance", "Security"), ("Security", "Governance"),
            ("Governance", "Action"), ("Action", "Sensory"),
        ]

    def test_every_hop_logged_to_archive_queue(self, tmp_path):
        """§13.3 verify #1: every hop logged in /archive/queue/."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, event_id = _run_worked_example(manifest_path)

        logged = eco.archive.query_queue(predicate=lambda r: r.get("event_id") == event_id)
        # 10 business hops: Sensory->Impulse, Impulse->Governance,
        # Sensory->Governance, ...through to Action->Sensory (proprioception).
        assert len(logged) >= 10

    def test_impulse_vectors_present_in_working_tier(self, tmp_path):
        """§13.3 verify #2: Impulse vectors present in /archive/working/."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, _ = _run_worked_example(manifest_path)

        vectors = eco.archive.get_drive_vectors()
        assert set(vectors.keys()) == {"curiosity", "fatigue", "urgency", "social_drive", "temperature"}

    def test_action_outcome_reenters_via_sensory(self, tmp_path):
        """§13.3 verify #3: Action's mock output re-enters via Sensory as
        an outcome event (proprioception check, §4)."""
        manifest_path = _manifest_with_temp_storage(tmp_path)
        eco, event_id = _run_worked_example(manifest_path)

        outcomes = [env for env in eco.bus.trace()
                    if env.event_id == event_id and env.source == "Action"
                    and env.destination == "Sensory"]
        assert len(outcomes) == 1
        assert outcomes[0].type == "Outcome"

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


if __name__ == "__main__":
    import sys
    sys.exit(pytest.main([__file__, "-v"]))
