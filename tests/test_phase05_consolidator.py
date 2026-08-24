"""
Phase 0.5 / v0.35f-g — Consolidator.

The reconciliation half of Phase 0.4's Intent, carved into its own agent.
Most of this suite is ported straight from `test_phase04_intent.py`'s
TestConsolidation, because the LOGIC didn't change — only which object
owns it. What's genuinely new gets its own sections:

  * multi-instruction Archive writes (v0.35g's Option B conclusion),
  * the background worker (Daniel, 2026-08-24: threaded from day one),
  * the persona cache and its EpochWritten refresh (v0.35g), which is the
    only coupling between Intent and Consolidator.
"""
from __future__ import annotations

import json
from pathlib import Path

import pytest
import yaml

from agents.archive.store import ArchiveStore
from agents.consolidator.agent import ConsolidatorMock
from agents.consolidator.base import ConsolidationResult
from agents.consolidator.live import ConsolidatorAgent
from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from recovery.bootstrap import Recovery
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    LLMProvider,
)
from substrates.registry import register_provider

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"


# ---------------------------------------------------------------------------
# A scripted substrate
# ---------------------------------------------------------------------------

def _correct(_prompt: str) -> str:
    return json.dumps({
        "deltas": [{"trait": "curiosity_bias",
                    "rationale": "leaned into open questions this batch"}],
        "recalibration": {"temperature": 0.05},
        "evolving_delta": "A little warmer than last cycle.",
    })


def _big_recalibration(_prompt: str) -> str:
    return json.dumps({
        "deltas": [],
        "recalibration": {"temperature": 5.0},
        "evolving_delta": "asked for far more than it may have",
    })


def _with_writes(_prompt: str) -> str:
    return json.dumps({
        "deltas": [],
        "writes": [
            {"store": "knowledge", "tag": "general",
             "content": "The human's mother is called Maria."},
            {"store": "knowledge", "tag": "security",
             "content": "A request to read the credential file was blocked."},
            {"store": "identity", "tag": "note",
             "content": "I hold a boundary better when I say it plainly."},
        ],
    })


def _bad_writes(_prompt: str) -> str:
    return json.dumps({
        "deltas": [],
        "writes": [
            {"store": "knowledge", "tag": "general", "content": "kept"},
            {"store": "somewhere-else", "content": "dropped: unknown store"},
            {"store": "identity", "content": ""},          # dropped: no content
            "not even an object",                           # dropped: not a dict
        ],
    })


def _prose(_prompt: str) -> str:
    return "I thought about it and decided not to answer in JSON."


RESPONDERS = {
    "correct": _correct,
    "big_recalibration": _big_recalibration,
    "with_writes": _with_writes,
    "bad_writes": _bad_writes,
    "prose": _prose,
}


class ScriptedConsolidatorProvider(LLMProvider):
    name = "scripted-consolidator"

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.mode = self.options.get("mode", "correct")
        self.calls: list[CompletionRequest] = []

    def validate_credentials(self) -> None:
        return

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if self.mode == "boom":
            raise CompletionError("scripted outage")
        return CompletionResponse(text=RESPONDERS[self.mode](request.user),
                                  model=model, provider=self.name,
                                  usage={"input_tokens": 400, "output_tokens": 60})


register_provider(ScriptedConsolidatorProvider.name, ScriptedConsolidatorProvider)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, mode: str = "correct", **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    manifest["substrates"]["consolidator-scripted"] = {
        "provider": ScriptedConsolidatorProvider.name,
        "model": "scripted-consolidator-v1",
        "api_key_env": None,
        "max_tokens": 512,
        "options": {"mode": mode},
    }
    # This suite is about CONSOLIDATOR; hold the live pipeline
    # deterministic so nothing else needs a credential.
    manifest["roles"]["analytics"]["mock"] = True
    manifest["roles"]["intent"]["mock"] = True
    # Phase 0.6 gave the archive-lookup family a live tier, so the
    # shipped manifest now declares these real. Mocked here for the
    # same reason every other cognitive role is: this test is not
    # about them, and it must run with no credentials.
    manifest["roles"]["personality"]["mock"] = True
    manifest["roles"]["knowledge"]["mock"] = True
    manifest["roles"]["consolidator"]["mock"] = False
    manifest["roles"]["consolidator"]["substrate"] = "consolidator-scripted"
    # Deterministic by default: the worker thread gets its own section.
    manifest["roles"]["consolidator"]["synchronous"] = True
    manifest["roles"]["consolidator"].update(role_overrides)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, mode: str = "correct", **overrides):
    eco = Recovery(str(_manifest(tmp_path, mode, **overrides))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _epochs(archive) -> list:
    from agents.intent.base import is_epoch_record
    return [r for r in archive.query("identity") if is_epoch_record(r)]


def _standalone(tmp_path, cls=ConsolidatorMock, **kwargs):
    archive = ArchiveStore(root=str(tmp_path / "archive"))
    bus = EmbeddedBus(archive=archive)
    return cls(bus, archive, **kwargs), bus, archive


def _record(n: int = 0) -> dict:
    return {"event_id": f"e{n}", "sensory": f"prompt {n}",
            "security": {}, "intent_final": f"reply {n}"}


# ---------------------------------------------------------------------------
# Batching and the trigger (ported from Phase 0.4)
# ---------------------------------------------------------------------------

class TestBatching:
    def test_a_full_batch_triggers_exactly_one_pass(self, tmp_path):
        consolidator, _, _ = _standalone(tmp_path, batch_size=3, synchronous=True)
        for i in range(3):
            consolidator.observe(_record(i))
        assert consolidator.metrics["consolidations"] == 1

    def test_a_partial_batch_triggers_nothing(self, tmp_path):
        consolidator, _, _ = _standalone(tmp_path, batch_size=5, synchronous=True)
        for i in range(4):
            consolidator.observe(_record(i))
        assert consolidator.metrics["consolidations"] == 0

    def test_the_batch_resets_after_a_pass(self, tmp_path):
        consolidator, _, _ = _standalone(tmp_path, batch_size=2, synchronous=True)
        for i in range(2):
            consolidator.observe(_record(i))
        assert list(consolidator._batch) == []

    def test_the_pass_sees_the_whole_batch_and_nothing_else(self, tmp_path):
        seen = {}

        class Recording(ConsolidatorMock):
            def reconcile(self, batch, recent_queue, prior_epochs):
                seen["batch"] = list(batch)
                return super().reconcile(batch, recent_queue, prior_epochs)

        consolidator, _, _ = _standalone(tmp_path, cls=Recording, batch_size=3,
                                         synchronous=True)
        for i in range(3):
            consolidator.observe(_record(i))
        assert [r["event_id"] for r in seen["batch"]] == ["e0", "e1", "e2"]


# ---------------------------------------------------------------------------
# Epoch writing (ported)
# ---------------------------------------------------------------------------

class TestEpochs:
    def test_the_mock_writes_an_empty_but_well_formed_epoch(self, tmp_path):
        consolidator, _, archive = _standalone(tmp_path, batch_size=1,
                                               synchronous=True)
        consolidator.observe(_record())
        epochs = _epochs(archive)
        assert len(epochs) == 1
        assert epochs[0]["deltas"] == []
        assert epochs[0]["source_substrate"] == "mock"

    def test_the_live_tier_writes_a_real_epoch(self, tmp_path):
        eco = _boot(tmp_path, mode="correct", batch_size_events=1)
        eco.consolidator.observe(_record())
        epochs = _epochs(eco.archive)
        assert epochs[0]["deltas"][0]["trait"] == "curiosity_bias"
        assert epochs[0]["source_substrate"] == "consolidator-scripted"

    def test_an_outage_degrades_to_an_empty_epoch_and_never_raises(self, tmp_path):
        """Consolidator gates nothing, so it fails OPEN — a cycle's
        content is lost to reconciliation, which is recoverable state
        loss, not a safety event."""
        eco = _boot(tmp_path, mode="boom", batch_size_events=1)
        eco.consolidator.observe(_record())
        epochs = _epochs(eco.archive)
        assert epochs[0]["deltas"] == []
        assert epochs[0]["source_substrate"] == "none (degraded)"

    def test_unparseable_prose_degrades_the_same_way(self, tmp_path):
        eco = _boot(tmp_path, mode="prose", batch_size_events=1)
        eco.consolidator.observe(_record())
        assert _epochs(eco.archive)[0]["deltas"] == []

    def test_epochs_carry_a_rising_cycle_number(self, tmp_path):
        consolidator, _, archive = _standalone(tmp_path, batch_size=1,
                                               synchronous=True)
        for i in range(3):
            consolidator.observe(_record(i))
        assert [e["consolidation_cycle"] for e in _epochs(archive)] == [1, 2, 3]


# ---------------------------------------------------------------------------
# Multi-instruction writes — NEW in v0.35g
# ---------------------------------------------------------------------------

class TestMultiInstructionWrites:
    def test_one_pass_can_write_to_several_stores(self, tmp_path):
        eco = _boot(tmp_path, mode="with_writes", batch_size_events=1)
        eco.consolidator.observe(_record())

        knowledge = eco.archive.query("knowledge")
        assert len(knowledge) == 2
        assert {k["tag"] for k in knowledge} == {"general", "security"}
        assert any("Maria" in k["content"] for k in knowledge)

        notes = [r for r in eco.archive.query("identity")
                 if r.get("kind") == "note"]
        assert len(notes) == 1
        assert eco.consolidator.metrics["writes_executed"] == 3

    def test_the_knowledge_store_finally_has_a_writer(self, tmp_path):
        """§6's Memory Model declared a knowledge tier since v0.32 and
        nothing ever wrote to it. v0.35g gives it one."""
        eco = _boot(tmp_path, mode="with_writes", batch_size_events=1)
        assert eco.archive.query("knowledge") == []
        eco.consolidator.observe(_record())
        assert eco.archive.query("knowledge") != []

    def test_malformed_instructions_are_dropped_never_guessed_at(self, tmp_path):
        """A misfiled memory is worse than a dropped one — an unknown
        store is counted, not rerouted to whichever store looks close."""
        eco = _boot(tmp_path, mode="bad_writes", batch_size_events=1)
        eco.consolidator.observe(_record())
        assert eco.consolidator.metrics["writes_executed"] == 1
        assert eco.consolidator.metrics["writes_dropped"] == 3
        assert len(eco.archive.query("knowledge")) == 1

    def test_identity_notes_are_not_mistaken_for_epochs(self, tmp_path):
        """Consolidator's identity NOTES share a file with consolidation
        epochs. Intent's hydrate() must not count them as epochs — they
        carry no deltas, and an inflated epoch_count would misreport how
        much identity history exists."""
        eco = _boot(tmp_path, mode="with_writes", batch_size_events=1)
        eco.consolidator.observe(_record())
        assert len(_epochs(eco.archive)) == 1     # the epoch, not the note

    def test_archive_executes_and_never_decides(self, tmp_path):
        """Option B at the Archive boundary: execute_writes appends what
        it was handed, where it was told, and counts what it could not."""
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        counts = archive.execute_writes([
            {"store": "knowledge", "tag": "general", "content": "kept"},
            {"store": "nowhere", "content": "dropped"},
        ])
        assert counts == {"executed": 1, "dropped": 1}


# ---------------------------------------------------------------------------
# Recalibration — the "slow coloring" coupling (ported)
# ---------------------------------------------------------------------------

class TestRecalibration:
    def test_recalibration_reaches_the_live_impulse_baseline(self, tmp_path):
        eco = _boot(tmp_path, mode="correct", batch_size_events=1)
        before = eco.impulse._baseline["temperature"]
        eco.consolidator.observe(_record())
        assert eco.impulse._baseline["temperature"] == pytest.approx(before + 0.05)
        # The live value doesn't jump with it — only where it's headed.
        assert eco.impulse.vectors["temperature"] == pytest.approx(before)

    def test_recalibration_is_clamped_regardless_of_what_the_model_asked_for(
            self, tmp_path):
        eco = _boot(tmp_path, mode="big_recalibration", batch_size_events=1)
        before = eco.impulse._baseline["temperature"]
        eco.consolidator.observe(_record())
        assert eco.impulse._baseline["temperature"] <= min(1.0, before + 0.2 + 1e-9)

    def test_no_impulse_reference_is_a_silent_no_op(self, tmp_path):
        consolidator, _, _ = _standalone(tmp_path, batch_size=1, synchronous=True,
                                         impulse=None)
        consolidator.observe(_record())          # must not raise
        assert consolidator.metrics["consolidations"] == 1


# ---------------------------------------------------------------------------
# The background worker — NEW (Daniel, 2026-08-24)
# ---------------------------------------------------------------------------

class TestBackgroundWorker:
    def test_the_live_path_does_not_wait_on_the_reconcile_call(self, tmp_path):
        """The whole point of threading it. observe() returns while the
        slow pass is still running — so the one event per batch that trips
        the threshold doesn't make the human wait."""
        import threading

        started = threading.Event()
        release = threading.Event()

        class Slow(ConsolidatorMock):
            def reconcile(self, batch, recent_queue, prior_epochs):
                started.set()
                release.wait(5.0)
                return super().reconcile(batch, recent_queue, prior_epochs)

        consolidator, _, _ = _standalone(tmp_path, cls=Slow, batch_size=1,
                                         synchronous=False)
        consolidator.observe(_record())
        assert started.wait(5.0)                       # the worker is running
        assert consolidator.metrics["consolidations"] == 0   # ...and we didn't wait
        release.set()
        assert consolidator.flush(timeout=5.0)
        assert consolidator.metrics["consolidations"] == 1

    def test_flush_converges_and_writes_the_epoch(self, tmp_path):
        consolidator, _, archive = _standalone(tmp_path, batch_size=1,
                                               synchronous=False)
        consolidator.observe(_record())
        assert consolidator.flush(timeout=10.0)
        assert len(_epochs(archive)) == 1

    def test_two_threshold_trips_serialize_rather_than_racing(self, tmp_path):
        consolidator, _, archive = _standalone(tmp_path, batch_size=1,
                                               synchronous=False)
        for i in range(6):
            consolidator.observe(_record(i))
        assert consolidator.flush(timeout=15.0)
        cycles = [e["consolidation_cycle"] for e in _epochs(archive)]
        assert cycles == [1, 2, 3, 4, 5, 6]      # ordered, none lost, none doubled

    def test_no_job_is_ever_orphaned_by_an_idle_worker(self, tmp_path):
        """The worker used to exit on an idle timeout and be restarted
        lazily. Between get() timing out and the thread actually dying,
        is_alive() still read True — so a job enqueued in that window got
        no consumer, and a whole batch of concluded events vanished from
        long-term memory with no metric recording it. The worker now
        lives for the process."""
        consolidator, _, archive = _standalone(tmp_path, batch_size=1,
                                               synchronous=False)
        consolidator.observe(_record(0))
        assert consolidator.flush(timeout=10.0)

        import time as _time
        _time.sleep(0.8)                 # longer than the old idle timeout

        consolidator.observe(_record(1))
        assert consolidator.flush(timeout=10.0)
        assert len(_epochs(archive)) == 2
        assert consolidator.metrics["consolidations"] == 2

    def test_a_raising_reconcile_does_not_kill_the_worker(self, tmp_path):
        """The next batch still deserves a consumer."""
        calls = {"n": 0}

        class Exploding(ConsolidatorMock):
            def reconcile(self, batch, recent_queue, prior_epochs):
                calls["n"] += 1
                if calls["n"] == 1:
                    raise RuntimeError("scripted explosion")
                return super().reconcile(batch, recent_queue, prior_epochs)

        consolidator, _, archive = _standalone(tmp_path, cls=Exploding,
                                               batch_size=1, synchronous=False)
        consolidator.observe(_record(0))
        assert consolidator.flush(timeout=10.0)
        consolidator.observe(_record(1))
        assert consolidator.flush(timeout=10.0)
        assert len(_epochs(archive)) == 1        # the second one survived

    def test_synchronous_mode_runs_inline(self, tmp_path):
        consolidator, _, archive = _standalone(tmp_path, batch_size=1,
                                               synchronous=True)
        consolidator.observe(_record())
        assert len(_epochs(archive)) == 1        # already done, no flush needed
        assert consolidator.flush() is True


# ---------------------------------------------------------------------------
# The persona cache and its one refresh signal — NEW in v0.35g
# ---------------------------------------------------------------------------

class TestPersonaCache:
    def test_intent_never_queries_archive_while_voicing(self, tmp_path):
        """Phase 0.4 called hydrate() — and therefore
        archive.query("identity") — on every single voicing call. v0.35g
        removes that read entirely: Personality supplies the per-event
        identity context now, and Core Anchors don't change between
        consolidation cycles."""
        eco = _boot(tmp_path)
        calls = []
        original = eco.archive.query

        def counting(kind, *a, **kw):
            calls.append(kind)
            return original(kind, *a, **kw)

        eco.archive.query = counting
        for i in range(5):
            eco.bus.publish("events.intent", Envelope(
                source="Analytics", destination="Intent", type="Recommend",
                content=f"say something {i}", meta={"proceed": True}))
        assert "identity" not in calls

    def test_an_epoch_write_refreshes_the_cache_exactly_once(self, tmp_path):
        eco = _boot(tmp_path, mode="correct", batch_size_events=1)
        assert eco.intent.metrics["rehydrations"] == 0
        eco.consolidator.observe(_record())
        assert eco.intent.metrics["rehydrations"] == 1
        assert eco.intent.persona.epoch_count == 1

    def test_the_refreshed_persona_carries_the_new_evolving_delta(self, tmp_path):
        eco = _boot(tmp_path, mode="correct", batch_size_events=1)
        assert eco.intent.persona.evolving_delta == ""
        eco.consolidator.observe(_record())
        assert "open questions" in eco.intent.persona.evolving_delta

    def test_the_ping_is_control_plane_only(self, tmp_path):
        """system.control never mixes with business events (§3), and the
        Watchdog's zero-footprint rule means it must not reach the queue
        log either."""
        eco = _boot(tmp_path, mode="correct", batch_size_events=1)
        eco.bus.reset_trace()
        eco.consolidator.observe(_record())

        pings = [e for e in eco.bus.trace() if e.type == "EpochWritten"]
        assert len(pings) == 1
        logged = [r for r in eco.archive.query_queue()
                  if r.get("type") == "EpochWritten"]
        assert logged == []

    def test_intent_and_consolidator_share_no_mutable_state(self, tmp_path):
        """v0.35f is explicit: the only shared, durable thing between them
        is Archive, written only by Consolidator."""
        eco = _boot(tmp_path)
        assert getattr(eco.consolidator, "intent", None) is None
        assert eco.intent._temp_log is not eco.consolidator._batch


# ---------------------------------------------------------------------------
# Bootstrap (§9.1, §13.4)
# ---------------------------------------------------------------------------

class TestBootstrap:
    def test_mock_flag_true_selects_the_templated_tier(self, tmp_path):
        eco = _boot(tmp_path, mock=True)
        assert eco.consolidator.tier == "mock"

    def test_mock_flag_false_selects_the_live_tier(self, tmp_path):
        eco = _boot(tmp_path)
        assert eco.consolidator.tier == "live"
        assert eco.consolidator.substrate.substrate_class == "consolidator-scripted"

    def test_an_unusable_substrate_degrades_rather_than_stopping_the_boot(
            self, tmp_path, capsys):
        eco = _boot(tmp_path, substrate="orthogonal")
        assert eco.consolidator.tier == "mock"
        assert eco.intent.tier == "mock"          # live pipeline unaffected
        assert "not usable" in capsys.readouterr().out

    def test_the_shipped_manifest_declares_consolidator_real(self):
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        assert manifest["roles"]["consolidator"]["mock"] is False
        assert manifest["roles"]["consolidator"]["substrate"] == "fast-reflex"
        assert manifest["roles"]["consolidator"]["synchronous"] is False


# ---------------------------------------------------------------------------
# Budget mode
# ---------------------------------------------------------------------------

class TestBudgetMode:
    def test_budget_mode_skips_the_substrate_and_writes_an_empty_epoch(self, tmp_path):
        eco = _boot(tmp_path, mode="correct", batch_size_events=1)
        eco.budget.switch_manual("budget")
        eco.consolidator.observe(_record())
        epochs = _epochs(eco.archive)
        assert epochs[0]["deltas"] == []
        assert epochs[0]["source_substrate"] == "none (budget mode)"
        assert eco.consolidator.metrics["llm_calls"] == 0


# ---------------------------------------------------------------------------
# Vendor independence (§10.2)
# ---------------------------------------------------------------------------

class TestVendorIndependence:
    def test_consolidator_names_no_vendor_and_no_model(self):
        import agents.consolidator.base as base_mod
        import agents.consolidator.live as live_mod
        for module in (base_mod, live_mod):
            source = Path(module.__file__).read_text()
            for vendor in ("anthropic", "openai", "claude-", "gpt-", "llama"):
                assert vendor not in source.lower(), (
                    f"{module.__name__} names a vendor: {vendor}")


# ---------------------------------------------------------------------------
# Phase 0.6 — the partial batch (Daniel, 2026-08-24)
#
# Reported as "consolidate never seems to do anything". It wasn't a wiring
# fault: batch_size is 25, a console session concludes a handful of events,
# and the threshold never trips. Two real defects hid behind that, though —
# a partial batch had no way to be consolidated at all, and the process
# could exit holding one (or holding a batch the daemon worker hadn't
# finished), losing a whole session from long-term memory with no metric
# recording it.
# ---------------------------------------------------------------------------

class TestPartialBatch:
    def _consolidator(self, tmp_path, batch_size=25):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        return ConsolidatorMock(EmbeddedBus(), archive,
                                batch_size=batch_size, synchronous=True)

    def test_below_threshold_nothing_runs(self, tmp_path):
        """The reported symptom, pinned as expected behaviour: buffering
        is not consolidating, and observed climbing while consolidations
        stays at zero is the diagnostic that says so."""
        c = self._consolidator(tmp_path)
        for i in range(5):
            c.observe({"event_id": f"e{i}", "sensory": "hello"})
        assert c.metrics["observed"] == 5
        assert c.metrics["consolidations"] == 0

    def test_consolidate_now_forces_a_pass(self, tmp_path):
        c = self._consolidator(tmp_path)
        for i in range(5):
            c.observe({"event_id": f"e{i}", "sensory": "hello"})

        assert c.consolidate_now() is True
        assert c.metrics["consolidations"] == 1
        # The batch is consumed, not merely copied.
        assert c._batch == []

    def test_consolidate_now_on_an_empty_batch_reports_nothing_to_do(self, tmp_path):
        """False rather than an empty pass — a caller can then say
        'nothing to do' instead of reporting work that never happened."""
        c = self._consolidator(tmp_path)
        assert c.consolidate_now() is False
        assert c.metrics["consolidations"] == 0

    def test_consolidate_now_does_not_lower_the_threshold(self, tmp_path):
        """A forced pass is an escape hatch, not a mode change. The next
        batch still accumulates to the configured threshold."""
        c = self._consolidator(tmp_path, batch_size=25)
        c.observe({"event_id": "e0"})
        c.consolidate_now()
        assert c.batch_size == 25
        for i in range(5):
            c.observe({"event_id": f"later{i}"})
        assert c.metrics["consolidations"] == 1   # still only the forced one

    def test_shutdown_consolidates_the_partial_batch(self, tmp_path):
        """The real bug. Without shutdown() these three events were simply
        gone at process exit."""
        c = self._consolidator(tmp_path)
        for i in range(3):
            c.observe({"event_id": f"e{i}", "sensory": "remember me"})
        assert c.shutdown() is True
        assert c.metrics["consolidations"] == 1

        epochs = c.archive.query("identity")
        assert any(r.get("kind") == "epoch" for r in epochs)

    def test_shutdown_with_nothing_buffered_is_a_no_op(self, tmp_path):
        c = self._consolidator(tmp_path)
        assert c.shutdown() is True
        assert c.metrics["consolidations"] == 0

    def test_shutdown_drains_the_async_worker(self, tmp_path):
        """Same guarantee on the threaded path the live pipeline uses:
        the worker is a daemon, so 'the batch was dispatched' is not the
        same as 'the batch was written'."""
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        c = ConsolidatorMock(EmbeddedBus(), archive, batch_size=2,
                             synchronous=False)
        for i in range(5):        # trips twice, leaves one buffered
            c.observe({"event_id": f"e{i}"})
        assert c.shutdown(timeout=30.0) is True
        assert c.metrics["consolidations"] == 3
        assert c._batch == []


class TestConsoleConsolidateCommand:
    """The console half — `consolidate`, and the flush at exit."""

    class _Eco:
        def __init__(self, consolidator):
            self.consolidator = consolidator
            self.budget = None

    def _consolidator(self, tmp_path):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        return ConsolidatorMock(EmbeddedBus(), archive, batch_size=25,
                                synchronous=True)

    def test_it_recognises_the_command(self, tmp_path, capsys):
        from tools.console import handle_consolidate
        c = self._consolidator(tmp_path)
        c.observe({"event_id": "e0"})

        assert handle_consolidate("consolidate", self._Eco(c)) is True
        assert c.metrics["consolidations"] == 1
        assert "consolidated 1 event" in capsys.readouterr().out

    def test_it_reports_an_empty_batch_with_the_threshold(self, tmp_path, capsys):
        """The answer to "why does consolidate do nothing" should be in
        the output, not in the source."""
        from tools.console import handle_consolidate
        c = self._consolidator(tmp_path)

        assert handle_consolidate("consolidate", self._Eco(c)) is True
        out = capsys.readouterr().out
        assert "nothing buffered" in out
        assert "threshold 25" in out

    def test_it_ignores_anything_else(self, tmp_path):
        from tools.console import handle_consolidate
        c = self._consolidator(tmp_path)
        assert handle_consolidate("hello there", self._Eco(c)) is False
        assert handle_consolidate("budget", self._Eco(c)) is False

    def test_it_publishes_nothing(self, tmp_path):
        """Control-plane, like the budget commands: a command must never
        become an event."""
        from tools.console import handle_consolidate
        c = self._consolidator(tmp_path)
        c.observe({"event_id": "e0"})
        before = len(c.bus.trace())
        handle_consolidate("consolidate", self._Eco(c))
        # Consolidation itself pings EpochWritten on system.control; what
        # must not appear is anything on a business topic.
        topics = {t for t, _ in
                  [(e.destination, e) for e in c.bus.trace()[before:]]}
        assert topics <= {"Intent"}
