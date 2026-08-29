"""
Shared Consolidator core — per-event fact writing (v0.9).

Consolidator used to buffer concluded events (fed by Governance only
after Action ran) and reconcile them in batches, doing two jobs in one
call: extracting structured facts, and distilling a higher-level
narrative delta + Impulse recalibration into an "epoch" record. That
batching had two costs — an in-memory buffer meant an ungraceful kill
could lose whatever hadn't been reconciled yet, and a fact stated early
in a conversation wasn't written until the next batch threshold fired,
so Knowledge could be a full batch behind what the user had already
said.

Consolidator ran for a while as a sixth member of Sensory's per-event
fan-out, reading the raw Sensory envelope directly and blind to
everything else the ecosystem already knew. That made its own job
(deciding WHERE a fact belongs) harder than it needed to be: with no
view of what already existed, the same kind of fact could land under a
different subtopic/subject spelling every call, purely from phrasing
drift, and nothing downstream could tell those apart.

Consolidator is now wired to Governance's BUNDLE route instead
(`agents/governance/agent.py`'s emit(), forked alongside the copy
Intent gets) — one hop later than before, but it now sees exactly what
Intent sees: the raw event plus whatever the knowledge swarm already
retrieved as relevant (`meta["knowledge_swarm_detail"]`). It still
writes whatever that single event states immediately, still never
replies to Governance, and still gates nothing. No buffer, no batch
threshold, no crash-loss window, no lag beyond waiting on the same
fan-out Intent already waits on.

The narrative-delta / recalibration half of the old job is dropped
entirely, not replaced — Consolidator's only remaining job is `writes`
(the multi-instruction Archive/StructuredStore upsert, v0.35g's Option
B taken to its natural conclusion: one reasoning pass, N mechanical
writes). It never speaks, never gates, and never reports back to
Governance — the same "gates nothing" posture as before, just without
anything left to gate.

Subclasses supply exactly one thing: write(envelope) -> ConsolidationResult.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List

from bus.envelope import Envelope, new_event_id
from bus.pubsub import EmbeddedBus
from agents.archive.structured_store import DEFAULT_DOMAIN

#: The stores a write instruction may target. Anything else is dropped at
#: the parse boundary (clamp-at-the-boundary discipline) and counted in
#: diagnostics — Archive never sees it.
VALID_WRITE_STORES = ("knowledge", "identity")


@dataclass
class ConsolidationResult:
    """One event's fact-extraction pass.

    `writes` fully specify their own destination
    ({"category", "topic", "subtopic", "key", "value"}), so Archive has
    nothing left to decide, only to execute."""

    writes: List[Dict[str, Any]] = field(default_factory=list)
    decided_by: str = "deterministic"
    diagnostics: Dict[str, Any] = field(default_factory=dict)


class ConsolidatorBase:
    """Bus-facing half of Consolidator. Subclass and implement write().

    Same shape as `ArchiveLookupBase`: subscribe once at construction,
    handle one event at a time, never buffer, never thread."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, archive, *, structured_store=None):
        self.bus = bus
        self.archive = archive
        self.structured_store = structured_store

        #: Optional display-layer hook, called with the records actually
        #: written on each event (empty list if none). Consolidator never
        #: publishes a bus hop of its own (it doesn't reply to Governance),
        #: so this is how an observer like tools/console.py sees its writes
        #: without adding bus plumbing for a display concern.
        self.on_write = None

        #: Doodle click dedup (docs/ideas/consolidation-doodle.md): the
        #: event_id of every consolidation write-pass that has already been
        #: acknowledged by a ui_click. In-memory only — losing this set on
        #: a restart just means a stale click could re-acknowledge once
        #: more, which is recoverable state loss consistent with
        #: Consolidator's existing fail-open posture (see module docstring).
        self._acknowledged_refs: set = set()

        self.metrics: Dict[str, int] = {
            "events": 0, "llm_calls": 0, "fallbacks": 0,
            "writes_executed": 0, "writes_dropped": 0,
        }
        self.bus.subscribe("events.consolidator", self.on_event)

    # ---- Business events ----------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1

        ref_event_id = envelope.meta.get("ref_event_id")
        if envelope.meta.get("source_type") == "ui_click" and ref_event_id:
            if ref_event_id in self._acknowledged_refs:
                # Daniel's dedup rule: the first click on a given
                # consolidation pass is the real signal; every later click
                # on the same one is a no-op — no substrate call, no writes.
                return
            self._acknowledged_refs.add(ref_event_id)

        result = self.write(envelope)

        self.metrics["writes_dropped"] += int(
            result.diagnostics.get("writes_rejected") or 0)
        if result.decided_by == "fallback":
            self.metrics["fallbacks"] += 1

        self._execute_writes(result.writes, envelope.event_id)

    # ---- Multi-instruction writes (Phase 0.9: Parquet upsert) ----------------

    def _execute_writes(self, writes: List[Dict[str, Any]], event_id: str) -> None:
        """Upsert structured records into the Parquet StructuredStore.

        Each write is a {category, topic, subtopic, key, value} dict.
        Dedup is handled by StructuredStore.upsert — matching keys get
        their value overwritten."""
        if not writes or self.structured_store is None:
            return
        records = [
            {**w, "domain": DEFAULT_DOMAIN, "source": "consolidator", "written_at": None}
            for w in writes
            if w.get("category") and w.get("key") and w.get("value")
        ]
        self.metrics["writes_dropped"] += len(writes) - len(records)
        if not records:
            return
        counts = self.structured_store.upsert("knowledge", records)
        self.metrics["writes_executed"] += counts.get("written", 0)
        if self.on_write is not None:
            self.on_write(records)
        self._publish_consolidation_written(event_id, records)

    def _publish_consolidation_written(self, event_id: str,
                                        records: List[Dict[str, Any]]) -> None:
        """Control-plane notification that this write pass actually wrote
        something (docs/ideas/consolidation-doodle.md). Mirrors
        `agents/archive/agent.py`'s `publish_receipt` — control-plane, not
        a business event, so it doesn't pollute the queue log as though
        memory had a thought. The doodle's click references this by
        event_id (there is no epoch id — Phase 0.9 removed batching, so
        one write pass IS one event_id)."""
        from agents.governance.knowledge_swarm import format_for_intent
        summary = format_for_intent(records)
        self.bus.publish("system.control", Envelope(
            source="Consolidator", destination="UI", type="ConsolidationWritten",
            content=summary, event_id=event_id or new_event_id(),
            meta={"event_id": event_id, "summary": summary},
        ))

    # ---- Tier hook ------------------------------------------------------------

    def write(self, envelope: Envelope) -> ConsolidationResult:
        raise NotImplementedError


__all__ = ["ConsolidatorBase", "ConsolidationResult", "VALID_WRITE_STORES"]
