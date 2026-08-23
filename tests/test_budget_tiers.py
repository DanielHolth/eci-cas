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


def test_unknown_tier_stops_bootstrap_deterministically(tmp_path):
    manifest = _with_tier(_load_manifest(), "ultra-deluxe")
    manifest["storage"]["root"] = str(tmp_path / "archive")
    out = tmp_path / "m.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)

    with pytest.raises(BootstrapError):
        Recovery(str(out)).parse_manifest()


# ---------------------------------------------------------------------------
# Each named tier resolves to what the appendix says it should
# ---------------------------------------------------------------------------

def test_minimal_mocks_analytics_and_points_intent_local():
    manifest = _with_tier(_load_manifest(), "minimal")
    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is True
    nodes = out["roles"]["intent"]["nodes"]
    assert all(n["substrate"] == budget_tiers.LOCAL_CLASS for n in nodes)
    assert out["roles"]["intent"]["consolidation_substrate"] == budget_tiers.LOCAL_CLASS


def test_budget_tier_runs_analytics_live_on_local_substrate():
    manifest = _with_tier(_load_manifest(), "budget")
    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is False
    assert out["roles"]["analytics"]["substrate"] == budget_tiers.LOCAL_CLASS
    nodes = out["roles"]["intent"]["nodes"]
    assert all(n["substrate"] == budget_tiers.LOCAL_CLASS for n in nodes)
    # Budget tier's consolidation is the one row that's NOT local — a
    # once-a-day hosted call is still cheap enough to afford (appendix).
    assert out["roles"]["intent"]["consolidation_substrate"] == budget_tiers.FAST_CLASS


def test_default_tier_is_a_noop_because_the_shipped_manifest_already_is_default():
    """The appendix's Default combination (Analytics/Intent/Consolidation
    all on the cheap hosted model) is what the manifest already ships
    with — so "apply Default" and "change nothing" have to be the same
    operation, or a test/operator overriding roles.analytics.mock for a
    cheap run would get silently overwritten back to live on every
    bootstrap (see budget/tiers.py's _NOOP_TIERS)."""
    manifest = _with_tier(_load_manifest(), "default")
    out = budget_tiers.apply_tier(manifest)

    assert out is manifest
    assert out["roles"]["analytics"]["mock"] is False
    assert out["roles"]["analytics"]["substrate"] == budget_tiers.ANALYTICS_DEFAULT_CLASS


def test_super_tier_reserves_specialist_for_consolidation_only():
    manifest = _with_tier(_load_manifest(), "super")
    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["analytics"]["mock"] is False
    # The live pipeline stays on the cheap model — only consolidation
    # (rare, async, high-value) spends on the specialist.
    assert out["roles"]["analytics"]["substrate"] == budget_tiers.ANALYTICS_DEFAULT_CLASS
    nodes = out["roles"]["intent"]["nodes"]
    assert all(n["substrate"] == budget_tiers.FAST_CLASS for n in nodes)
    assert out["roles"]["intent"]["consolidation_substrate"] == budget_tiers.SPECIALIST_CLASS


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


def test_apply_tier_does_not_mutate_the_input():
    manifest = _with_tier(_load_manifest(), "minimal")
    original_mock = manifest["roles"]["analytics"]["mock"]
    budget_tiers.apply_tier(manifest)
    assert manifest["roles"]["analytics"]["mock"] == original_mock


def test_apply_tier_never_fabricates_an_intent_node():
    """An empty roles.intent.nodes is a real misconfiguration Recovery is
    supposed to catch and stop the bootstrap on (§9.1 step 6, "nodes is
    empty — need at least one node"). apply_tier() must only ANNOTATE
    existing nodes with a substrate, never synthesize one to fill a gap —
    doing so would silently swallow that fail-stop for every tier except
    custom/default, which is worse than the tier doing nothing at all."""
    manifest = _with_tier(_load_manifest(), "minimal")
    manifest["roles"]["intent"]["nodes"] = []

    out = budget_tiers.apply_tier(manifest)

    assert out["roles"]["intent"]["nodes"] == []


def test_a_tier_with_an_empty_node_list_still_stops_the_bootstrap(tmp_path):
    manifest = _load_manifest()
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "minimal"
    manifest["roles"]["intent"]["nodes"] = []
    path = tmp_path / "m.yaml"
    with open(path, "w") as f:
        yaml.safe_dump(manifest, f)

    with pytest.raises(BootstrapError, match="nodes is empty"):
        Recovery(str(path)).bootstrap()


# ---------------------------------------------------------------------------
# describe() — the boot log line
# ---------------------------------------------------------------------------

def test_describe_reports_resolved_substrates_not_just_the_name():
    manifest = budget_tiers.apply_tier(_with_tier(_load_manifest(), "super"))
    line = budget_tiers.describe(manifest)
    assert "super" in line
    assert budget_tiers.FAST_CLASS in line
    assert budget_tiers.SPECIALIST_CLASS in line


def test_describe_custom_says_so():
    manifest = _with_tier(_load_manifest(), "custom")
    assert budget_tiers.describe(manifest).startswith("custom")
    manifest_absent = _with_tier(_load_manifest(), None)
    assert budget_tiers.describe(manifest_absent).startswith("custom")


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
