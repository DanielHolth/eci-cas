"""
Phase 0.3 Impulse — unit coverage for the two things that changed from the
Phase 0 mock (drift, weighted appraisal) and the one thing that is
structurally guaranteed not to (the Elevated severity ceiling).

The Phase 0 exit-criteria reproducibility test (tests/test_phase0_e2e.py,
test_reproducible_twice_in_a_row) already covers the "no observable drift
when nothing has displaced a vector" property end-to-end, byte-for-byte.
This file goes narrower and deeper: it unit-tests agents/impulse/agent.py
directly (bus + a throwaway ArchiveStore, no full bootstrap) so drift,
appraisal, and the manifest-override refusals in recovery.bootstrap are
each exercised in isolation.

Run with:
    pytest tests/test_phase03_impulse.py -v
"""
from __future__ import annotations

import time

import pytest

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from agents.archive.store import ArchiveStore
from agents.impulse.agent import (
    Impulse,
    DEFAULT_VECTORS,
    DEFAULT_DRIFT_TAU_SEC,
    URGENCY_ELEVATED_THRESHOLD,
    IMPULSE_SEVERITY_CEILING,
    REACTION_VOCABULARY,
)


@pytest.fixture
def archive(tmp_path):
    return ArchiveStore(root=str(tmp_path / "archive"))


@pytest.fixture
def bus(archive):
    return EmbeddedBus(archive=archive)


def _ingest(bus, content="hello", severity="Neutral"):
    """Publish directly to events.impulse the way Sensory.ingest() does —
    single hop, v0.31 strict relay."""
    env = Envelope(source="Sensory", destination="Impulse", type="Sense",
                    content=content, severity=severity, triggered_by="sensory")
    bus.publish("events.impulse", env)


def _capture_governance(bus):
    captured = []
    bus.subscribe("events.governance", captured.append)
    return captured


# ---- Drift: no-op at baseline -------------------------------------------

class TestDriftNoOpAtBaseline:
    def test_vectors_untouched_when_nothing_has_displaced_them(self, bus, archive):
        impulse = Impulse(bus, archive)
        before = dict(impulse.vectors)
        captured = _capture_governance(bus)

        _ingest(bus)

        assert impulse.vectors == before   # exact, bit-for-bit — see module docstring
        assert captured[0].meta["drive_vectors"] == before

    def test_no_op_holds_even_with_elapsed_wall_clock_time(self, bus, archive):
        """Drift is gated on (value != baseline), not on elapsed time being
        small — so a vector sitting at rest should still be untouched even
        if real time has passed between construction and the first event."""
        impulse = Impulse(bus, archive)
        before = dict(impulse.vectors)
        time.sleep(0.05)

        _ingest(bus)

        assert impulse.vectors == before


# ---- Drift: observable once displaced ------------------------------------

class TestDriftWhenDisplaced:
    def test_displaced_vector_relaxes_toward_baseline_over_time(self, bus, archive):
        impulse = Impulse(bus, archive, drift_tau_sec={"urgency": 0.05})
        impulse.vectors["urgency"] = 1.0
        # Force elapsed time to be measured from here, not from __init__.
        impulse._last_update = time.monotonic()
        time.sleep(0.15)   # several tau — should have relaxed substantially

        _ingest(bus)

        assert 0.0 <= impulse.vectors["urgency"] < 0.6
        assert impulse.vectors["urgency"] != 1.0

    def test_displaced_vector_never_overshoots_past_baseline(self, bus, archive):
        """Exponential decay toward baseline should never cross it."""
        impulse = Impulse(bus, archive, drift_tau_sec={"urgency": 0.01})
        impulse.vectors["urgency"] = 0.9
        impulse._last_update = time.monotonic()
        time.sleep(0.5)   # many tau — should be very close to baseline (0.0)

        _ingest(bus)

        assert impulse.vectors["urgency"] >= 0.0
        assert impulse.vectors["urgency"] < 0.05

    def test_drift_updates_archive_only_when_something_changed(self, bus, archive):
        impulse = Impulse(bus, archive, drift_tau_sec={"urgency": 0.05})
        impulse.vectors["urgency"] = 1.0
        impulse._last_update = time.monotonic()
        time.sleep(0.15)

        _ingest(bus)

        stored = archive.get_drive_vectors()
        assert stored["urgency"] == impulse.vectors["urgency"]

    def test_manifest_drift_tau_sec_override_is_applied(self, bus, archive):
        custom_tau = {"fatigue": 12345.0}
        impulse = Impulse(bus, archive, drift_tau_sec=custom_tau)

        assert impulse.drift_tau_sec["fatigue"] == 12345.0
        # Everything not overridden keeps the code default.
        assert impulse.drift_tau_sec["urgency"] == DEFAULT_DRIFT_TAU_SEC["urgency"]


# ---- Appraisal engine ------------------------------------------------------

class TestAppraisalEngine:
    def test_axes_are_clamped_to_unit_range(self, bus, archive):
        impulse = Impulse(bus, archive)
        impulse.vectors.update({
            "urgency": 1.0, "fatigue": 1.0, "social_drive": 1.0,
            "temperature": 1.0, "curiosity": 1.0,
        })
        axes = impulse._axes()
        for value in axes.values():
            assert 0.0 <= value <= 1.0

    def test_high_curiosity_low_fatigue_yields_engagement_reflex(self, bus, archive):
        impulse = Impulse(bus, archive)
        impulse.vectors.update({"curiosity": 0.95, "fatigue": 0.0,
                                 "urgency": 0.0, "social_drive": 0.0, "temperature": 0.0})
        reflex = impulse._reflex()
        assert reflex == REACTION_VOCABULARY["engagement"]["high"]

    def test_high_urgency_low_fatigue_yields_alertness_reflex(self, bus, archive):
        impulse = Impulse(bus, archive)
        impulse.vectors.update({"urgency": 0.95, "fatigue": 0.0,
                                 "curiosity": 0.0, "social_drive": 0.0, "temperature": 0.0})
        reflex = impulse._reflex()
        assert reflex == REACTION_VOCABULARY["alertness"]["high"]

    def test_reflex_is_present_in_outgoing_meta(self, bus, archive):
        impulse = Impulse(bus, archive)
        captured = _capture_governance(bus)

        _ingest(bus)

        assert "reflex" in captured[0].meta
        assert captured[0].meta["reflex"] in {
            text for buckets in REACTION_VOCABULARY.values() for text in buckets.values()
        }


# ---- Severity ceiling (hard invariant) ------------------------------------

class TestSeverityCeiling:
    def test_high_alertness_raises_to_elevated_not_higher(self, bus, archive):
        impulse = Impulse(bus, archive)
        impulse.vectors["urgency"] = 1.0
        captured = _capture_governance(bus)

        _ingest(bus, severity="Neutral")

        assert captured[0].severity == IMPULSE_SEVERITY_CEILING
        assert captured[0].severity != "Critical"

    def test_impulse_never_lowers_an_incoming_critical_tag(self, bus, archive):
        """OR-upscale-only: Impulse's own read is capped, but it must never
        downscale a severity Sensory already set."""
        impulse = Impulse(bus, archive)
        captured = _capture_governance(bus)

        _ingest(bus, severity="Critical")

        assert captured[0].severity == "Critical"

    def test_low_alertness_does_not_raise_severity(self, bus, archive):
        impulse = Impulse(bus, archive)
        captured = _capture_governance(bus)

        _ingest(bus, severity="Neutral")

        assert captured[0].severity == "Neutral"

    def test_threshold_is_manifest_tunable(self, bus, archive):
        impulse = Impulse(bus, archive, urgency_elevated_threshold=0.05)
        impulse.vectors["urgency"] = 0.1
        captured = _capture_governance(bus)

        _ingest(bus, severity="Neutral")

        assert captured[0].severity == IMPULSE_SEVERITY_CEILING


# ---- Verbatim relay (unchanged from the mock) ------------------------------

class TestVerbatimRelay:
    def test_content_is_relayed_unchanged_not_the_reflex_text(self, bus, archive):
        impulse = Impulse(bus, archive)
        captured = _capture_governance(bus)

        _ingest(bus, content="the original human words")

        assert captured[0].content == "the original human words"
        assert captured[0].content != captured[0].meta["reflex"]

    def test_destination_is_always_governance(self, bus, archive):
        impulse = Impulse(bus, archive)
        captured = _capture_governance(bus)

        _ingest(bus)

        assert captured[0].destination == "Governance"
        assert captured[0].source == "Impulse"


# ---- Feedback path (§4.1 reward path) --------------------------------------

class TestApplyFeedback:
    def test_feedback_shifts_the_named_vector_immediately(self, bus, archive):
        impulse = Impulse(bus, archive)
        before = impulse.vectors["social_drive"]

        impulse.apply_feedback(valence=0.2, driver="social_drive")

        assert impulse.vectors["social_drive"] == pytest.approx(before + 0.2)
        assert archive.get_drive_vectors()["social_drive"] == pytest.approx(before + 0.2)

    def test_feedback_clamps_to_unit_range(self, bus, archive):
        impulse = Impulse(bus, archive)
        impulse.vectors["urgency"] = 0.95

        impulse.apply_feedback(valence=0.5, driver="urgency")

        assert impulse.vectors["urgency"] == 1.0

    def test_feedback_on_unknown_driver_is_a_silent_no_op(self, bus, archive):
        impulse = Impulse(bus, archive)
        before = dict(impulse.vectors)

        impulse.apply_feedback(valence=0.5, driver="not_a_real_vector")

        assert impulse.vectors == before


# ---- Manifest provisioning: mock / ceiling override refusal ---------------

class TestBootstrapProvisioning:
    def test_mock_true_in_manifest_is_warned_and_ignored(self, bus, archive, capsys):
        from recovery.bootstrap import Recovery

        manifest = {"roles": {"impulse": {"mock": True}}}
        impulse = Recovery(None)._provision_impulse(bus, archive, manifest)

        out = capsys.readouterr().err + capsys.readouterr().out
        assert isinstance(impulse, Impulse)

    def test_ceiling_override_in_manifest_is_warned_and_not_obeyed(self, bus, archive):
        from recovery.bootstrap import Recovery

        manifest = {"roles": {"impulse": {
            "mock": False,
            "severity": {"ceiling": "Critical"},
        }}}
        impulse = Recovery(None)._provision_impulse(bus, archive, manifest)
        impulse.vectors["urgency"] = 1.0
        captured = _capture_governance(bus)
        _ingest(bus, severity="Neutral")

        assert captured[0].severity == IMPULSE_SEVERITY_CEILING
        assert captured[0].severity != "Critical"
