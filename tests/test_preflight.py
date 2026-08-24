"""
tools/preflight.py — offline coverage (§9.1, §10.2).

The one property worth protecting: preflight has to report on the SAME
roles.* Recovery will actually boot with, not the manifest's raw, literal
values. Recovery.parse_manifest() applies budget_tier before it looks at
roles.* (budget/tiers.py); preflight is a separate entry point and had
silently skipped that step — it would report "analytics -> deep-reasoning
-> gpt-5.4-nano" for a manifest whose budget_tier was actually "minimal"
(analytics mocked, zero calls, zero cost), because it read
roles.analytics.substrate straight off the YAML instead of the
tier-resolved manifest. Offline and free — no network call, no token
spent, matching preflight's own non-`--live` mode.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from tools import preflight

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"


def _manifest(tmp_path: Path, budget_tier: str) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = budget_tier
    path = tmp_path / "m.yaml"
    with open(path, "w") as f:
        yaml.safe_dump(manifest, f)
    return path


class TestPreflightAppliesTheTier:
    def test_minimal_tier_reports_analytics_as_mocked(self, tmp_path, capsys):
        path = _manifest(tmp_path, "minimal")
        code = preflight.main(["--manifest", str(path)])
        out = capsys.readouterr().out

        # Minimal assigns Analytics no substrate at all (budget/tiers.py's
        # apply_tier() clears any leftover value rather than let it sit
        # there implying an identity the tier never claimed) — preflight
        # must say "mocked" plainly, not report a stale substrate/model
        # name, and must not fail preflight over it.
        assert "analytics             mocked" in out
        assert "role is fully mocked" in out
        assert "deep-reasoning" not in out
        assert "gpt-5.4-nano" not in out
        assert code == 0

    def test_default_tier_reports_analytics_as_required(self, tmp_path, capsys):
        path = _manifest(tmp_path, "default")
        preflight.main(["--manifest", str(path)])
        out = capsys.readouterr().out

        assert "analytics" in out
        assert "(role is mocked)" not in out.split("analytics", 1)[1].split("\n")[0]

    def test_the_boot_log_line_names_the_active_tier(self, tmp_path, capsys):
        path = _manifest(tmp_path, "minimal")
        preflight.main(["--manifest", str(path)])
        out = capsys.readouterr().out

        assert "minimal" in out

    def test_an_unknown_tier_fails_closed_rather_than_crashing(self, tmp_path, capsys):
        path = _manifest(tmp_path, "not-a-real-tier")
        code = preflight.main(["--manifest", str(path)])

        assert code == 1
