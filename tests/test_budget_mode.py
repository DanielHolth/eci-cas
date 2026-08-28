"""
Budget mode — adaptive substrate throttling (Phase 0.2.1).

Offline and free. Every latch path is exercised without a key, because
the whole point of budget mode is what happens when the substrate is
unavailable — and "unavailable" is trivially easy to arrange offline.

What these tests are really pinning down is the asymmetry. Budget mode is
most likely to engage exactly when something is already wrong, so the
question that matters is not "does it switch" but "what does it let
through while it is switched". A gate that approves things because the
reasoner is missing would be worse than no gate at all.
"""
from __future__ import annotations

import json
from pathlib import Path

import pytest
import yaml

from budget.state import BUDGET, LIVE, BudgetManager, BudgetState, from_manifest
from bus.envelope import VERDICT_YELLOW, Envelope
from recovery.bootstrap import Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    FailureKind,
    LLMProvider,
    Substrate,
)
from substrates.registry import register_provider, resolve_substrate

MANIFEST_PATH = Path(__file__).parent.parent / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"
GOOD_ANSWER = json.dumps({"recommendation": "Ordinary greeting.", "proceed": True})


# ---------------------------------------------------------------------------
# A provider that fails on demand, with a chosen classification
# ---------------------------------------------------------------------------

class FlakyProvider(LLMProvider):
    name = "flaky"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.fail_kind = self.options.get("fail_kind")
        self.calls = 0

    def validate_credentials(self) -> None:
        return

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls += 1
        if self.fail_kind:
            raise CompletionError(f"scripted {self.fail_kind}",
                                  kind=FailureKind(self.fail_kind))
        return CompletionResponse(text=GOOD_ANSWER, model=model, provider=self.name,
                                  usage={"input_tokens": 1000, "output_tokens": 200})


register_provider(FlakyProvider.name, FlakyProvider)


def _manifest(tmp_path: Path, *, fail_kind=None, **budget_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    # This suite pins roles.analytics.mock=False directly below; budget_tier
    # must be a no-op or the SHIPPED manifest's live tier (an operator's
    # "minimal", say) would silently overwrite that back to True before
    # this fixture's own override took effect (budget/tiers.py).
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["deep-reasoning"] = {
        "provider": FlakyProvider.name, "model": "flaky-1", "api_key_env": None,
        "max_tokens": 256, "price_per_mtok": {"input": 1.0, "output": 5.0},
        "options": {"fail_kind": fail_kind},
    }
    manifest["roles"]["analytics"]["mock"] = False
    # This suite is about Analytics + budget mode; hold Intent
    # deterministic so it needs no credential of its own (Phase 0.4 note,
    # same reasoning as the analytics.mock pin above).
    manifest["roles"]["intent"]["mock"] = True
    # Phase 0.6 gave the archive-lookup family a live tier, so the
    # shipped manifest now declares these real. Mocked here for the
    # same reason every other cognitive role is: this test is not
    # about them, and it must run with no credentials.
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["knowledge"]["mock"] = True
    manifest["budget_mode"].update(budget_overrides)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "m.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, **kwargs):
    eco = Recovery(str(_manifest(tmp_path, **kwargs))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _yellow(eco, proposed="do the thing"):
    env = Envelope(source="Security", destination="Governance", type="Verdict",
                   content="rules do not settle this",
                   meta={"verdict": VERDICT_YELLOW, "proposed_action": proposed})
    eco.bus.publish("events.governance", env)
    return env.event_id


def _analytics_out(eco, event_id):
    return [e for e in eco.bus.trace()
            if e.event_id == event_id and e.source == "Analytics"][0]


def _spoken(eco, event_id):
    return [str(e.content) for e in eco.bus.trace()
            if e.event_id == event_id and e.destination == "Action"]


# ---------------------------------------------------------------------------
# The state machine, in isolation
# ---------------------------------------------------------------------------

class TestLatching:
    def test_it_starts_live(self):
        assert BudgetManager().state.mode == LIVE

    def test_one_transient_failure_does_not_latch(self):
        """The vendor SDK already retried this twice with backoff. One
        reaching us is noise, not a pattern."""
        m = BudgetManager(failure_threshold=3)
        m.record_failure(FailureKind.TRANSIENT, "429")
        assert m.state.mode == LIVE
        assert m.should_call_substrate()

    def test_a_run_of_transient_failures_latches(self):
        m = BudgetManager(failure_threshold=3)
        for _ in range(3):
            m.record_failure(FailureKind.TRANSIENT, "429 rate limited")
        assert m.state.mode == BUDGET
        assert m.state.reason == "transient"

    def test_a_success_breaks_the_run(self):
        """The counter is about consecutive failures, not lifetime totals —
        an intermittent substrate shouldn't latch after a slow accumulation."""
        m = BudgetManager(failure_threshold=3)
        m.record_failure(FailureKind.TRANSIENT, "429")
        m.record_failure(FailureKind.TRANSIENT, "429")
        m.record_success(usage={"input_tokens": 10, "output_tokens": 5})
        m.record_failure(FailureKind.TRANSIENT, "429")
        assert m.state.mode == LIVE
        assert m.state.consecutive_failures == 1
        assert m.state.total_failures == 3

    def test_one_terminal_failure_latches_immediately(self):
        """A bad key fails identically forever. Counting to three would
        just be three wasted calls."""
        m = BudgetManager(failure_threshold=3)
        m.record_failure(FailureKind.TERMINAL, "401 authentication_error")
        assert m.state.mode == BUDGET
        assert m.state.reason == "terminal"

    def test_latching_raises_an_alert_exactly_once(self):
        m = BudgetManager(failure_threshold=1)
        m.record_failure(FailureKind.TERMINAL, "401")
        alerts = m.drain_alerts()
        assert alerts and "budget mode" in alerts[0]
        assert m.drain_alerts() == [], "alerts should drain exactly once"
        m.record_failure(FailureKind.TERMINAL, "401")
        assert m.drain_alerts() == [], "further failures in budget mode do not re-alert"


class TestSpendCap:
    def test_spend_accumulates(self):
        m = BudgetManager(spend_cap_usd=1.0)
        m.record_success(usage={"input_tokens": 1000, "output_tokens": 200}, cost_usd=0.02)
        assert m.state.spend_usd == pytest.approx(0.02)
        assert m.state.tokens_in == 1000 and m.state.tokens_out == 200

    def test_crossing_the_cap_latches(self):
        m = BudgetManager(spend_cap_usd=0.10)
        for _ in range(5):
            m.record_success(cost_usd=0.03)
        assert m.state.mode == BUDGET
        assert m.state.reason == "spend_cap"
        assert any("cap" in a for a in m.drain_alerts())

    def test_the_halfway_warning_fires_once_and_does_not_latch(self):
        m = BudgetManager(spend_cap_usd=1.0, warn_at=0.5)
        m.record_success(cost_usd=0.6)
        assert m.state.mode == LIVE, "a warning must not stop anything"
        assert len(m.drain_alerts()) == 1
        m.record_success(cost_usd=0.1)
        assert m.drain_alerts() == []

    def test_no_cap_never_latches_on_spend(self):
        m = BudgetManager(spend_cap_usd=None)
        for _ in range(100):
            m.record_success(cost_usd=10.0)
        assert m.state.mode == LIVE

    def test_an_unpriced_substrate_reports_zero_rather_than_guessing(self):
        s = resolve_substrate({"substrates": {"x": {"provider": "echo", "model": "m"}}}, "x")
        assert s.has_prices is False
        assert s.estimate_cost({"input_tokens": 999999, "output_tokens": 999999}) == 0.0

    def test_cost_is_computed_from_manifest_prices(self):
        s = resolve_substrate({"substrates": {"x": {
            "provider": "echo", "model": "m",
            "price_per_mtok": {"input": 1.0, "output": 5.0}}}}, "x")
        # 1M in at $1 + 1M out at $5
        assert s.estimate_cost({"input_tokens": 1_000_000,
                                "output_tokens": 1_000_000}) == pytest.approx(6.0)


class TestManualControl:
    def test_manual_switch_to_budget_and_back(self):
        m = BudgetManager()
        assert "budget mode" in m.switch_manual("budget")
        assert m.state.mode == BUDGET
        assert "live mode" in m.switch_manual("live")
        assert m.state.mode == LIVE

    def test_switching_to_live_clears_the_failure_run(self):
        m = BudgetManager(failure_threshold=2)
        m.record_failure(FailureKind.TRANSIENT, "429")
        m.record_failure(FailureKind.TRANSIENT, "429")
        m.switch_manual("live")
        assert m.state.mode == LIVE
        assert m.state.consecutive_failures == 0

    def test_manual_live_overrides_a_spend_cap_latch_but_says_so(self):
        """The cap guards against mistakes; it is not a lock the operator
        can't open. It should be honest that it will latch again."""
        m = BudgetManager(spend_cap_usd=0.01)
        m.record_success(cost_usd=1.0)
        assert m.state.mode == BUDGET
        message = m.switch_manual("live")
        assert m.state.mode == LIVE
        assert "over the cap" in message

    def test_reset_clears_spend_but_says_it_is_not_billing(self):
        m = BudgetManager()
        m.record_success(cost_usd=1.0, usage={"input_tokens": 10, "output_tokens": 2})
        message = m.reset_spend()
        assert m.state.spend_usd == 0.0 and m.state.calls == 0
        assert "billing is unaffected" in message


class TestPersistence:
    def test_state_survives_a_restart(self, tmp_path):
        """A latch that vanishes on restart is worse than no latch: the
        system comes back live and starts spending again unnoticed."""
        from agents.archive.store import ArchiveStore
        archive = ArchiveStore(root=str(tmp_path / "archive"))

        first = BudgetManager(archive, failure_threshold=1)
        first.record_failure(FailureKind.TERMINAL, "401 bad key")
        first.record_success(cost_usd=0.25)

        second = BudgetManager(archive)
        assert second.state.mode == BUDGET
        assert second.state.reason == "terminal"
        assert second.state.spend_usd == pytest.approx(0.25)

    def test_a_missing_archive_is_not_fatal(self):
        m = BudgetManager(archive=None)
        m.record_success(cost_usd=0.1)
        assert m.state.spend_usd == pytest.approx(0.1)

    def test_an_unreadable_archive_degrades_to_defaults(self, tmp_path):
        class Broken:
            def query(self, *a, **k): raise IOError("disk gone")
            def write(self, *a, **k): raise IOError("disk gone")
        m = BudgetManager(Broken())
        assert m.state.mode == LIVE
        m.record_success(cost_usd=0.1)      # must not raise

    def test_state_roundtrips_through_dicts(self):
        s = BudgetState(mode=BUDGET, reason="spend_cap", spend_usd=1.25)
        assert BudgetState.from_dict(s.to_dict()) == s

    def test_unknown_persisted_fields_are_ignored(self):
        """Forward compatibility: a newer build's extra field shouldn't
        crash an older one on restore."""
        s = BudgetState.from_dict({"mode": BUDGET, "some_future_field": 1})
        assert s.mode == BUDGET


class TestLoggingFields:
    """timestamp + budget_tier (Daniel, 2026-08-24): the budget log is an
    append-only snapshot per call, so each entry needs its own "when" and
    "under which tier" to be worth comparing across runs later — neither
    field feeds a mode decision."""

    def test_every_call_appends_its_own_stamped_snapshot(self, tmp_path):
        """Still an append-only log by design (Daniel was fine with that
        once each entry is self-describing) — every call adds a new row,
        each with its own timestamp."""
        from agents.archive.store import ArchiveStore
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        m = BudgetManager(archive, budget_tier="super")
        m.record_success(cost_usd=0.1)
        m.record_success(cost_usd=0.1)
        m.record_success(cost_usd=0.1)

        records = archive.query("budget")
        assert len(records) == 3
        assert [r["calls"] for r in records] == [1, 2, 3]
        assert all(r["budget_tier"] == "super" for r in records)
        assert all(r["timestamp"] for r in records)


class TestManifestConfig:
    def test_it_reads_the_shipped_manifest(self):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        m = from_manifest(manifest)
        assert m.enabled is True
        assert m.spend_cap_usd == pytest.approx(5.0)
        assert m.failure_threshold == 3

    def test_a_null_cap_disables_capping(self):
        m = from_manifest({"budget_mode": {"spend_cap_usd": None}})
        assert m.spend_cap_usd is None

    def test_disabled_budget_mode_never_intervenes(self):
        m = from_manifest({"budget_mode": {"enabled": False}})
        for _ in range(10):
            m.record_failure(FailureKind.TERMINAL, "401")
        assert m.should_call_substrate() is True


# ---------------------------------------------------------------------------
# In the pipeline
# ---------------------------------------------------------------------------

class TestInThePipeline:
    def test_live_mode_calls_the_substrate_and_records_cost(self, tmp_path):
        eco = _boot(tmp_path)
        eco.sensory.ingest(PROMPT, source_type="prompt")

        assert eco.budget.state.calls == 1
        assert eco.budget.state.spend_usd > 0
        assert eco.analytics.metrics["llm_calls"] == 1

    def test_budget_mode_never_touches_the_substrate_and_still_completes(self, tmp_path):
        eco = _boot(tmp_path)
        eco.budget.switch_manual("budget")
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        assert eco.analytics.substrate.provider.calls == 0
        assert eco.budget.state.spend_usd == 0.0

        hops = [(e.source, e.destination) for e in eco.bus.trace()
                if e.event_id == event_id]
        assert hops[-1] == ("Governance", "Action")
        assert len(eco.action.executed) == 1

        out = _analytics_out(eco, event_id)
        assert out.meta["analytics"]["proceed"] is True
        assert out.meta["analytics"]["decided_by"] == "budget"

    def test_a_gated_event_declines_in_budget_mode(self, tmp_path):
        """The asymmetry that matters. Budget mode is most likely to be on
        when something is already wrong; approving a gate because the
        reasoner is unavailable would be worse than having no gate.

        v0.35e moved the gate: Security's yellow lane goes to INTENT now,
        not Analytics, so this is Intent's budget-mode fallback being
        asserted rather than Analytics' — but the property under test is
        the same one, and it is exactly why the gating registers had to
        get a fail-closed fallback when they moved."""
        eco = _boot(tmp_path)
        eco.budget.switch_manual("budget")
        event_id = _yellow(eco, proposed="the unapproved thing")

        intent_out = [e for e in eco.bus.trace()
                      if e.event_id == event_id and e.source == "Intent"][0]
        # This suite pins Intent to its mock tier, which declines a gating
        # register outright (it cannot judge, so it says so) — the same
        # outcome budget mode produces on the live tier, reported honestly
        # as deterministic rather than as a degraded call. The live tier's
        # budget-mode path is asserted in tests/test_phase05_intent_veto.py.
        assert intent_out.meta["proceed"] is False
        assert intent_out.meta["intent"]["failed_closed"] is True
        assert not [s for s in _spoken(eco, event_id) if "the unapproved thing" in s]

    def test_a_terminal_failure_latches_mid_run(self, tmp_path):
        eco = _boot(tmp_path, fail_kind="terminal")
        eco.sensory.ingest(PROMPT, source_type="prompt")

        assert eco.budget.state.mode == BUDGET
        assert len(eco.action.executed) == 1, "the event still completed"

    def test_after_latching_no_further_calls_are_made(self, tmp_path):
        eco = _boot(tmp_path, fail_kind="terminal")
        for i in range(4):
            eco.sensory.ingest(f"event {i}", source_type="prompt")

        # One call failed and latched; the rest never reached the substrate.
        assert eco.analytics.substrate.provider.calls == 1
        assert len(eco.action.executed) == 4

    def test_transient_failures_keep_trying_until_the_threshold(self, tmp_path):
        eco = _boot(tmp_path, fail_kind="transient", failure_threshold=3)
        for i in range(5):
            eco.sensory.ingest(f"event {i}", source_type="prompt")

        assert eco.analytics.substrate.provider.calls == 3
        assert eco.budget.state.mode == BUDGET

    def test_a_contract_violation_is_not_a_substrate_failure(self, tmp_path, monkeypatch):
        """The call succeeded and was paid for; the model just answered out
        of shape. A run of bad JSON must not latch budget mode, or one
        badly-worded prompt would look like an outage."""
        eco = _boot(tmp_path)

        def unparseable(self, request, *, model):
            return CompletionResponse(text="not json at all", model=model,
                                      provider=self.name,
                                      usage={"input_tokens": 100, "output_tokens": 10})

        monkeypatch.setattr(FlakyProvider, "complete", unparseable)

        for i in range(5):
            eco.sensory.ingest(f"event {i}", source_type="prompt")

        assert eco.budget.state.mode == LIVE
        assert eco.budget.state.consecutive_failures == 0
        assert eco.analytics.metrics["fallbacks"] == 5

    def test_the_spend_cap_latches_the_running_pipeline(self, tmp_path):
        eco = _boot(tmp_path, spend_cap_usd=0.005)   # ~2 calls at 1000/200 tokens
        for i in range(6):
            eco.sensory.ingest(f"event {i}", source_type="prompt")

        assert eco.budget.state.mode == BUDGET
        assert eco.budget.state.reason == "spend_cap"
        assert eco.analytics.substrate.provider.calls < 6
        assert len(eco.action.executed) == 6, "every event still produced an action"

    def test_bootstrap_restores_a_latch(self, tmp_path):
        path = _manifest(tmp_path)
        first = Recovery(str(path)).bootstrap()
        first.budget.switch_manual("budget")

        second = Recovery(str(path)).bootstrap()
        assert second.budget.state.mode == BUDGET
        assert second.analytics.budget.state.mode == BUDGET


# ---------------------------------------------------------------------------
# Console commands
# ---------------------------------------------------------------------------

class TestConsoleCommands:
    def _eco(self, tmp_path):
        return _boot(tmp_path)

    def test_budget_commands_switch_and_switch_back(self, tmp_path):
        from tools.console import handle_command
        eco = self._eco(tmp_path)
        assert handle_command("switch to budget mode", eco) is True
        assert eco.budget.state.mode == BUDGET
        handle_command("switch to live mode", eco)
        assert eco.budget.state.mode == LIVE

    def test_a_command_publishes_nothing(self, tmp_path):
        """Pre-queue by construction: no event_id, no tokens, no agent
        ever sees it."""
        from tools.console import handle_command
        eco = self._eco(tmp_path)
        before = len(eco.bus.trace())
        handle_command("switch to budget mode", eco)
        assert len(eco.bus.trace()) == before
        assert eco.analytics.substrate.provider.calls == 0

    def test_reset_budget_zeroes_the_counters(self, tmp_path):
        from tools.console import handle_command
        eco = self._eco(tmp_path)
        eco.budget.record_success(cost_usd=1.0)
        handle_command("reset budget", eco)
        assert eco.budget.state.spend_usd == 0.0

    def test_an_ordinary_prompt_is_not_a_command(self, tmp_path):
        from tools.console import handle_command
        eco = self._eco(tmp_path)
        assert handle_command("what is the budget for this project?", eco) is False

    def test_alerts_drain_to_the_console(self, tmp_path, capsys):
        from tools.console import show_alerts
        eco = self._eco(tmp_path)
        eco.budget.record_failure(FailureKind.TERMINAL, "401 bad key")
        show_alerts(eco)
        assert "budget mode" in capsys.readouterr().out
        show_alerts(eco)
        assert capsys.readouterr().out == "", "alerts must drain exactly once"


# ---------------------------------------------------------------------------
# Failure classification
# ---------------------------------------------------------------------------

class TestFailureClassification:
    @pytest.mark.parametrize("status,expected", [
        (429, FailureKind.TRANSIENT),   # rate limited
        (401, FailureKind.TERMINAL),    # bad key
    ])
    def test_status_codes_map_to_the_right_kind(self, status, expected):
        from substrates.providers import _classify

        class VendorError(Exception):
            status_code = status
        assert _classify(VendorError())[0] is expected

    def test_connection_failures_without_a_status_are_transient(self):
        from substrates.providers import _classify

        class APIConnectionError(Exception):
            pass
        assert _classify(APIConnectionError())[0] is FailureKind.TRANSIENT

    def test_unrecognised_failures_default_to_unknown(self):
        from substrates.providers import _classify
        assert _classify(ValueError("what"))[0] is FailureKind.UNKNOWN

    def test_the_real_vendor_exceptions_classify_correctly(self):
        """Against the actual SDK classes, so a vendor renaming or
        renumbering one is caught here rather than in production."""
        anthropic = pytest.importorskip("anthropic")
        from substrates.providers import _classify

        for name, expected in (("RateLimitError", FailureKind.TRANSIENT),
                               ("OverloadedError", FailureKind.TRANSIENT),
                               ("AuthenticationError", FailureKind.TERMINAL),
                               ("PermissionDeniedError", FailureKind.TERMINAL),
                               ("NotFoundError", FailureKind.TERMINAL)):
            exc_type = getattr(anthropic, name, None)
            if exc_type is None:
                continue
            status = getattr(exc_type, "status_code", None)
            if status is None:
                continue

            class _Probe(Exception):
                status_code = status
            assert _classify(_Probe())[0] is expected, f"{name} ({status})"
