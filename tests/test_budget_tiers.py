"""
Budget tiers (Phase 0.2.2, docs/budget-tiers-appendix.md).

Offline and free, same posture as test_budget_mode.py: these pin down
resolution logic (what a tier NAME turns into), not a real substrate call.
The property worth protecting is "one line, then restart" — a tier has to
win outright over whatever roles.* already said, or switching tiers
wouldn't actually switch anything.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from budget import tiers as budget_tiers
from recovery.bootstrap import BootstrapError, Recovery

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"


def _load_manifest() -> dict:
    with open(MANIFEST_PATH) as f:
        return yaml.safe_load(f)


def _with_tier(manifest: dict, tier) -> dict:
    out = dict(manifest)
    if tier is None:
        out.pop("budget_tier", None)
    else:
        out["budget_tier"] = tier
    return out


# ---------------------------------------------------------------------------
# apply_tier: the no-op cases
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("tier", [None, "custom", "CUSTOM", "  custom  ", "default"])
def test_absent_or_custom_or_default_is_a_noop(tier):
    manifest = _with_tier(_load_manifest(), tier)
    before = yaml.dump(manifest, sort_keys=True)
    out = budget_tiers.apply_tier(manifest)
    assert yaml.dump(out, sort_keys=True) == before


def test_unknown_tier_raises():
    manifest = _with_tier(_load_manifest(), "ultra-deluxe")
    with pytest.raises(budget_tiers.UnknownTier):
        budget_tiers.apply_tier(manifest)


# ---------------------------------------------------------------------------
# Each named tier resolves to what the appendix says it should
# ---------------------------------------------------------------------------

def test_minimal_mocks_analytics_and_points_everything_else_fast_local():
    manifest = _with_tier(_load_manifest(), "minimal")
    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is True
    assert out["roles"]["intent"]["substrate"] == budget_tiers.FAST_LOCAL_CLASS
    # Consolidator/Reflection get the async SLOW_LOCAL_CLASS, not the
    # live-path FAST_LOCAL_CLASS — dispatch #5's fast/slow split holds
    # even inside Minimal's all-local, $0 tier.
    assert out["roles"]["consolidator"]["substrate"] == budget_tiers.SLOW_LOCAL_CLASS
    assert out["roles"]["reflection"]["substrate"] == budget_tiers.SLOW_LOCAL_CLASS
    assert out["roles"]["intent"]["context_events"] == budget_tiers.CONTEXT_EVENTS["minimal"]


def test_budget_tier_runs_analytics_live_on_fast_low_substrate():
    manifest = _with_tier(_load_manifest(), "budget")
    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is False
    assert out["roles"]["analytics"]["substrate"] == budget_tiers.FAST_LOW_CLASS
    assert out["roles"]["intent"]["substrate"] == budget_tiers.FAST_LOW_CLASS
    # Budget tier's consolidation/reflection are the async SLOW_LOW_CLASS —
    # a Mistral fast lane for the live path, an OpenAI slow lane off it.
    assert out["roles"]["consolidator"]["substrate"] == budget_tiers.SLOW_LOW_CLASS
    assert out["roles"]["reflection"]["substrate"] == budget_tiers.SLOW_LOW_CLASS


def test_default_tier_is_a_noop_because_the_shipped_manifest_already_is_default():
    """The Default combination (Analytics/Personality/Knowledge on
    fast-low, Intent on fast-medium, Consolidator/Reflection on
    slow-medium) is what the manifest already ships with — so "apply
    Default" and "change nothing" have to be the same operation, or a
    test/operator overriding roles.analytics.mock for a cheap run would
    get silently overwritten back to live on every bootstrap (see
    budget/tiers.py's _NOOP_TIERS)."""
    manifest = _with_tier(_load_manifest(), "default")
    out = budget_tiers.apply_tier(manifest)

    assert out is manifest
    assert out["roles"]["analytics"]["mock"] is False
    assert out["roles"]["analytics"]["substrate"] == budget_tiers.FAST_LOW_CLASS
    assert out["roles"]["intent"]["substrate"] == budget_tiers.FAST_MEDIUM_CLASS
    assert out["roles"]["consolidator"]["substrate"] == budget_tiers.SLOW_MEDIUM_CLASS


def test_super_tier_reserves_the_high_classes_for_intent_and_consolidation():
    manifest = _with_tier(_load_manifest(), "super")
    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is False
    # The fan-out/lookup roles stay on the cheap fast lane — only Intent
    # (live, but the persona's voice) and Consolidator/Reflection (async,
    # high-value) spend on the *-high classes.
    assert out["roles"]["analytics"]["substrate"] == budget_tiers.FAST_LOW_CLASS
    assert out["roles"]["intent"]["substrate"] == budget_tiers.FAST_HIGH_CLASS
    assert out["roles"]["consolidator"]["substrate"] == budget_tiers.SLOW_HIGH_CLASS
    assert out["roles"]["reflection"]["substrate"] == budget_tiers.SLOW_HIGH_CLASS


# ---------------------------------------------------------------------------
# A tier wins outright — it doesn't merge with whatever roles.* already had
# ---------------------------------------------------------------------------

def test_tier_overwrites_an_explicit_conflicting_role_setting():
    manifest = _load_manifest()
    manifest["budget_tier"] = "minimal"
    # An operator's stale/explicit choice that conflicts with the tier.
    manifest["roles"]["analytics"]["mock"] = False
    manifest["roles"]["analytics"]["substrate"] = "deep-reasoning"

    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is True, (
        "the tier must win outright, or switching tiers wouldn't actually "
        "switch anything the next time someone edits budget_tier"
    )


def test_every_tier_states_the_consolidator_mock_flag_explicitly():
    """Same stale-flag discipline the tiers already apply to
    roles.analytics.mock and roles.intent.mock: an operator's leftover
    `consolidator.mock: true` must not silently survive a tier switch and
    quietly stop long-term memory from ever being written."""
    for tier in ("minimal", "budget", "super"):
        manifest = _with_tier(_load_manifest(), tier)
        manifest["roles"]["consolidator"]["mock"] = True
        # Phase 0.6 gave the archive-lookup family a live tier, so the
        # shipped manifest now declares these real. Mocked here for the
        # same reason every other cognitive role is: this test is not
        # about them, and it must run with no credentials.
        manifest["roles"]["personality"]["mock"] = True
        manifest["roles"]["knowledge"]["mock"] = True
        out = budget_tiers.apply_tier(manifest)
        assert out["roles"]["consolidator"]["mock"] is False


def test_a_live_intent_with_no_substrate_still_stops_the_bootstrap(tmp_path):
    """The surviving half of the old "nodes is empty" fail-stop. v0.35f
    removed the node list, so the misconfiguration it guarded against is
    now simply "declared real, names no substrate" — and Recovery must
    still stop deterministically on it (§9.1 step 6) rather than booting
    a cognitive role with nothing behind it."""
    manifest = _load_manifest()
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
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
    with open(path, "w") as f:
        yaml.safe_dump(manifest, f)

    with pytest.raises(BootstrapError, match="no 'substrate' class"):
        Recovery(str(path)).bootstrap()


# ---------------------------------------------------------------------------
# Integration: Recovery applies the tier before anything else reads roles.*
# ---------------------------------------------------------------------------

def test_recovery_parse_manifest_applies_the_tier(tmp_path):
    manifest = _with_tier(_load_manifest(), "minimal")
    manifest["storage"]["root"] = str(tmp_path / "archive")
    out = tmp_path / "m.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)

    parsed = Recovery(str(out)).parse_manifest()
    assert parsed["roles"]["analytics"]["mock"] is True


def test_minimal_tier_boots_with_no_credentials_at_all(tmp_path, monkeypatch):
    """The point of Minimal: it must reach 'system live' with zero keys
    set, because Analytics is mocked and nothing else in the pipeline
    calls a substrate yet."""
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)

    manifest = _with_tier(_load_manifest(), "minimal")
    manifest["storage"]["root"] = str(tmp_path / "archive")
    out = tmp_path / "m.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)

    ecosystem = Recovery(str(out)).bootstrap()
    assert ecosystem.analytics.tier == "mock"
