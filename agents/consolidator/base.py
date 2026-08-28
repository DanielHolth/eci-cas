"""
Shared Consolidator core — batching, the reconcile trigger, epoch
writing, multi-instruction Archive writes, and the Impulse recalibration
coupling (v0.35f/g).

Consolidator is the half of Phase 0.4's Intent that used to run as a
mode ("Consolidating") inside one class. v0.35f splits it out into its
own agent, matching every other role's one-job shape, because every
substrate call in this system is stateless: live voicing and slow
reconciliation were both just "assemble a prompt, call a substrate", and
the mode switch only decided *which* prompt. The split costs nothing and
gets Consolidator's slow call out of Intent's way entirely.

What lives here rather than in either tier (mirroring
agents/analytics/base.py's split discipline):

  The batch buffer and threshold trigger — the moved
  `_events_since_consolidation` mechanism from Phase 0.4's IntentBase.

  Epoch assembly and the Archive writes. Option B, unchanged: the
  reasoner decides content, Archive stays a dumb executor. New in v0.35g,
  one reasoning pass may emit MULTIPLE write instructions, each fully
  specifying its own destination (store + tag) — see
  ArchiveStore.execute_writes.

  The "slow coloring" coupling to Impulse
  (recalibrate_baseline) — moved here with the rest of the
  consolidation job, still living in exactly one place.

  The background worker (v0.35f open item, resolved by Daniel
  2026-08-24: threaded from day one). The embedded bus dispatches
  synchronously, so without this the one event that trips the batch
  threshold would wait on the reconcile call before the human got their
  answer. Jobs go onto a single-worker queue; reconciles are serialized
  by construction. `synchronous=True` runs the job inline instead —
  the mode every offline test fixture uses, which is what keeps the
  Phase 0 byte-identical-trace exit criterion checkable.

  The `EpochWritten` ping. After writing an epoch, Consolidator
  publishes a control-plane envelope (system.control) so Intent can
  re-hydrate its cached persona (v0.35g's persona caching). This is the
  ONLY signal between the two agents — no shared mutable state, no
  direct references; the one shared, durable thing is Archive, written
  only here.

Subclasses supply exactly one thing: reconcile(batch, recent_queue,
prior_epochs) -> ConsolidationResult.
"""
from __future__ import annotations

import queue
import threading
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

from bus.envelope import Envelope, new_event_id
from bus.pubsub import EmbeddedBus

DEFAULT_BATCH_SIZE = 25      # §15 default, formerly rotation.batch_size_events

#: How many prior epochs feed the next reconciliation's "prior identity"
#: input. Small and bounded (§1's flat-cost claim).
RECENT_EPOCHS_FOR_RECONCILE = 3

#: How many recent queue-log records stand in for "Analytics' delta
#: report" (§7.4 #3). Same Phase 0.4 stand-in, unchanged.
RECENT_QUEUE_RECORDS_FOR_RECONCILE = 100

#: The stores a write instruction may target. Anything else is dropped at
#: the parse boundary (clamp-at-the-boundary, same discipline as the
#: recalibration cap) and counted in diagnostics — Archive never sees it.
VALID_WRITE_STORES = ("knowledge", "identity")


@dataclass
class ConsolidationResult:
    """One reconciliation pass's output, before it becomes an epoch
    record (§7.4) plus zero or more additional Archive writes (v0.35g).

    `recalibration` is the "slow coloring" hook — small, named nudges to
    Impulse's BASELINE drive vectors, applied by ConsolidatorBase (the
    caller), not by reconcile() itself, so the Impulse coupling lives in
    exactly one place regardless of which tier produced the numbers.

    `writes` are v0.35g's multi-instruction Archive writes: each entry
    fully specifies destination ({"store", "tag", "content"}), so Archive
    has nothing left to decide, only to execute — Option B taken to its
    natural conclusion: one reasoning pass, N mechanical writes."""

    deltas: List[Dict[str, Any]] = field(default_factory=list)
    recalibration: Dict[str, float] = field(default_factory=dict)
    evolving_delta: str = ""
    writes: List[Dict[str, Any]] = field(default_factory=list)
    decided_by: str = "deterministic"
    diagnostics: Dict[str, Any] = field(default_factory=dict)


class ConsolidatorBase:
    """Bus-facing half of Consolidator. Subclass and implement reconcile().

    Until the v0.35a/c fan-out exists, Consolidator has no bus inbox of
    its own — Intent hands it one record per event via observe(), the
    in-process interim wiring the v0.35 handover note names. Once
    Governance owns the settled one-bundle-per-event hand-off, observe()
    is what Governance's dispatch calls into (via the events.consolidator
    topic) and the direct call from Intent goes away."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, archive, *,
                 batch_size: int = DEFAULT_BATCH_SIZE,
                 impulse=None,
                 synchronous: bool = False):
        self.bus = bus
        self.archive = archive
        self.batch_size = max(1, int(batch_size))
        #: Optional live Impulse instance — the "slow coloring" coupling.
        #: None is legal: no Impulse reference simply means no
        #: recalibration, degrading gracefully rather than erroring.
        self.impulse = impulse
        self.synchronous = bool(synchronous)

        self._cycle = 0
        self._batch: List[Dict[str, Any]] = []
        self._lock = threading.Lock()
        self._worker_lock = threading.Lock()
        self._jobs: "queue.Queue[List[Dict[str, Any]]]" = queue.Queue()
        self._worker: Optional[threading.Thread] = None

        self.metrics: Dict[str, int] = {
            "observed": 0, "consolidations": 0, "llm_calls": 0,
            "fallbacks": 0, "writes_executed": 0, "writes_dropped": 0,
        }

    # ---- Intake -------------------------------------------------------------

    def observe(self, record: Dict[str, Any]) -> None:
        """One concluded event's record. Buffered; the batch-size
        threshold trips a reconciliation pass over the whole batch."""
        with self._lock:
            self.metrics["observed"] += 1
            self._batch.append(dict(record))
            if len(self._batch) < self.batch_size:
                return
            batch, self._batch = self._batch, []
        self._launch(batch)

    # ---- Worker -------------------------------------------------------------

    def _launch(self, batch: List[Dict[str, Any]]) -> None:
        """Hand one batch to the worker (or run it inline).

        The worker is started once and lives for the process. An earlier
        cut started it lazily and let it exit on an idle timeout, which
        opened a race with a real cost: between `get()` timing out and the
        thread actually dying, `is_alive()` still reads True, so a job
        enqueued in that window got no consumer — and a whole batch of
        concluded events was silently lost from long-term memory, with no
        metric recording it. A long-lived worker has no such window."""
        if self.synchronous:
            self._run(batch)
            return
        with self._worker_lock:
            if self._worker is None:
                self._worker = threading.Thread(
                    target=self._drain_jobs, name="consolidator-reconcile",
                    daemon=True)
                self._worker.start()
        self._jobs.put(batch)

    def _drain_jobs(self) -> None:
        while True:
            batch = self._jobs.get()          # blocks; the worker never exits
            try:
                self._run(batch)
            except Exception:
                # A reconcile that raises must not take the worker with
                # it — the next batch still deserves a consumer. The
                # tiers already degrade internally; this is the backstop
                # for anything they don't catch.
                self.metrics["fallbacks"] += 1
            finally:
                self._jobs.task_done()

    def consolidate_now(self) -> bool:
        """Force a reconciliation pass over whatever is buffered.

        The batch threshold is a cost control, not a correctness rule: a
        session that ends (or a human who asks) below the threshold would
        otherwise drop everything it had accumulated on the floor. This is
        the escape hatch, and it is deliberately the ONLY way a pass runs
        early — nothing lowers the threshold behind the operator's back.

        Returns False when there was nothing buffered to consolidate, so a
        caller can say "nothing to do" rather than reporting a pass that
        never happened."""
        with self._lock:
            if not self._batch:
                return False
            batch, self._batch = self._batch, []
        self._launch(batch)
        return True

    def shutdown(self, timeout: float = 30.0) -> bool:
        """Consolidate the partial batch, then wait for the worker to drain.

        Without this, every session below the batch threshold silently
        lost its entire long-term memory of that session at exit — the
        events concluded, Consolidator observed them, and the process
        died holding them in a list. The worker is a daemon thread, so
        even a batch dispatched moments before exit could be killed
        mid-reconcile."""
        self.consolidate_now()
        return self.flush(timeout=timeout)

    def flush(self, timeout: float = 30.0) -> bool:
        """Wait for every queued reconcile to finish. Used by tests and a
        clean shutdown; a live pipeline never calls this."""
        if self.synchronous:
            return True
        waiter = threading.Thread(target=self._jobs.join, daemon=True)
        waiter.start()
        waiter.join(timeout)
        return not waiter.is_alive()

    # ---- The pass (§7.4, moved from IntentBase._consolidate) ---------------

    def _run(self, batch: List[Dict[str, Any]]) -> None:
        prior_epochs: List[Dict[str, Any]] = []
        if self.archive is not None:
            records = self.archive.query("identity")
            prior_epochs = [r for r in records if r.get("kind") != "anchors"][
                -RECENT_EPOCHS_FOR_RECONCILE:]

        recent_queue: List[Dict[str, Any]] = []
        if self.archive is not None:
            try:
                recent_queue = self.archive.query_queue()[
                    -RECENT_QUEUE_RECORDS_FOR_RECONCILE:]
            except Exception:
                recent_queue = []

        self._cycle += 1
        result = self.reconcile(batch, recent_queue, prior_epochs)

        epoch = {
            "epoch_id": f"consolidator_cycle-{self._cycle}",
            "kind": "epoch",
            "source_substrate": result.diagnostics.get("source_substrate", "none"),
            "source_model": result.diagnostics.get(
                "source_model", "none (deterministic, zero LLM cost)"),
            "consolidation_cycle": self._cycle,
            "deltas": result.deltas,
        }
        if result.recalibration:
            epoch["recalibration"] = dict(result.recalibration)
        # Instructions the tier's own parse boundary already threw out
        # (unknown store, empty content, not an object). Counted here so
        # one metric answers "how many writes did this pass lose", whether
        # they were lost at the parse boundary or at Archive's.
        self.metrics["writes_dropped"] += int(
            result.diagnostics.get("writes_rejected") or 0)

        if self.archive is not None:
            self.archive.write("identity", epoch)
            self._execute_writes(result.writes)

        self._apply_recalibration(result.recalibration)

        self.metrics["consolidations"] += 1
        if result.decided_by == "fallback":
            self.metrics["fallbacks"] += 1

        # Persona-refresh ping (v0.35g). Control-plane: zero
        # business-queue footprint, and the only coupling to Intent.
        self.bus.publish("system.control", Envelope(
            source="Consolidator", destination="Intent", type="EpochWritten",
            content=f"epoch consolidator_cycle-{self._cycle} written",
            event_id=new_event_id(),
        ))

    # ---- Multi-instruction writes (Phase 0.9: Parquet upsert) ----------------

    def _execute_writes(self, writes: List[Dict[str, Any]]) -> None:
        """Upsert structured records into the Parquet StructuredStore.

        Each write is a {category, topic, subtopic, key, value} dict.
        Dedup is handled by StructuredStore.upsert — matching keys get
        their value overwritten."""
        structured_store = getattr(self, "structured_store", None)
        if not writes or structured_store is None:
            return
        records = [
            {**w, "source": "consolidator", "written_at": None}
            for w in writes
            if w.get("category") and w.get("key") and w.get("value")
        ]
        self.metrics["writes_dropped"] += len(writes) - len(records)
        if not records:
            return
        counts = structured_store.upsert("knowledge", records)
        self.metrics["writes_executed"] += counts.get("written", 0)

    # ---- The Impulse coupling (§5.3 "slow coloring", moved intact) ----------

    def _apply_recalibration(self, recalibration: Dict[str, float]) -> None:
        if not recalibration or self.impulse is None:
            return
        recalibrate = getattr(self.impulse, "recalibrate_baseline", None)
        if recalibrate is None:
            return
        for vector, delta in recalibration.items():
            recalibrate(vector, float(delta),
                        rationale=f"Consolidator cycle {self._cycle}")

    # ---- Tier hook ----------------------------------------------------------

    def reconcile(self, batch: List[Dict[str, Any]],
                  recent_queue: List[Dict[str, Any]],
                  prior_epochs: List[Dict[str, Any]]) -> ConsolidationResult:
        raise NotImplementedError


__all__ = ["ConsolidatorBase", "ConsolidationResult", "DEFAULT_BATCH_SIZE",
           "RECENT_EPOCHS_FOR_RECONCILE", "RECENT_QUEUE_RECORDS_FOR_RECONCILE",
           "VALID_WRITE_STORES"]
