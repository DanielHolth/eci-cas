"""
Archive — the agent half of "the only door to memory" (§5.8, Phase 0.6).

`ArchiveStore` (store.py) is the door: two stable, HTTP-shaped endpoints,
write and query, that any agent can call directly. That interface is not
changing and this module does not replace it — every existing caller
still holds the store and still calls it in-process.

What was missing is that Archive was the only one of the eleven roles
with no presence on the bus at all. Two consequences, both of which cost
something real:

  * A writer had to hold a reference to the store. Consolidator does,
    legitimately (it is the sole writer of long-term memory and it needs
    the executed/dropped counts back synchronously). But every FUTURE
    writer inherited that coupling by default, and "hold the store" is a
    much stronger grant than "ask Archive to append this" — the store has
    no read-only view on it the way the lookup family gets.
  * Nothing that happened inside Archive was observable from outside it.
    An epoch landing in long-term memory — arguably the most significant
    thing this system does that isn't speech — produced no bus event, so
    nothing downstream could react to it and no trace recorded it.

So: the store stays exactly what it was, and this agent puts a door on
the bus beside it. Write requests arrive on `events.archive`; every
completed write publishes a receipt on the control plane.

Deliberately NOT done here
--------------------------
Consolidator was not migrated onto this path. It is the sole writer of
long-term memory, its writes are synchronous by design, and it uses the
executed/dropped counts to report what a reconciliation pass lost. Moving
it to fire-and-forget messaging would trade a fact for a hope in the one
place this system keeps its auditable record. The bus door is for
everything else — and the first thing it is likely to carry is a
notification that an epoch was written, which is where the "the human
looked at what we learned" signal will attach (see docs/ideas/).

Archive still authors nothing and still decides nothing. An instruction
naming a store that doesn't exist is counted and dropped, never rerouted
to a guess: a misfiled memory is worse than a lost one, and the count is
what tells you the contract drifted.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from bus.envelope import Envelope, new_event_id
from bus.pubsub import EmbeddedBus

from agents.archive.store import ArchiveStore

#: Envelope types this agent acts on. Anything else on the topic is
#: counted and ignored rather than guessed at — Archive is the last place
#: in this system that should be inferring intent.
WRITE_TYPES = {"Write", "ArchiveWrite"}


class ArchiveAgent:
    """Archive's bus door. Wraps a store; never replaces it.

    Delegation is deliberate and total: `write`, `query`, `log_event` and
    the rest are forwarded untouched, so an agent handed this object
    instead of the raw store cannot tell the difference. That is what
    makes adopting it a non-event for existing callers."""

    tier = "live"
    topic = "events.archive"

    def __init__(self, bus: EmbeddedBus, store: ArchiveStore):
        self.bus = bus
        self.store = store
        self.metrics: Dict[str, int] = {
            "requests": 0, "executed": 0, "dropped": 0, "ignored": 0,
        }
        self.bus.subscribe(self.topic, self.on_event)

    # ---- The bus door -----------------------------------------------------

    @staticmethod
    def instructions(envelope: Envelope) -> List[Dict[str, Any]]:
        """The write instructions carried by one envelope.

        Accepts a list (several writes in one request) or a single object
        (one write), because both are natural things for a caller to
        send and neither is ambiguous. Anything else yields nothing —
        Archive does not attempt to construct a record out of prose."""
        content = envelope.content
        if isinstance(content, dict):
            return [content]
        if isinstance(content, list):
            return [i for i in content if isinstance(i, dict)]
        return []

    def on_event(self, envelope: Envelope) -> None:
        if envelope.type not in WRITE_TYPES:
            # A query over the bus is deliberately unsupported: a request
            # whose whole value is the answer needs a reply channel, and
            # inventing one here would duplicate the direct `query` call
            # every reader already has. Reads stay synchronous.
            self.metrics["ignored"] += 1
            return

        self.metrics["requests"] += 1
        instructions = self.instructions(envelope)
        result = self.store.execute_writes(instructions)
        self.metrics["executed"] += result["executed"]
        self.metrics["dropped"] += result["dropped"]
        self.publish_receipt(envelope, result)

    def publish_receipt(self, envelope: Envelope, result: Dict[str, int]) -> None:
        """Say what landed, on the control plane.

        Control-plane rather than a business event, for the same reason
        Consolidator's EpochWritten ping is: this is bookkeeping about the
        system, not a step in an event's life, and it must not appear in
        the queue log as though memory had had a thought.

        A receipt is published even when everything was dropped. An
        instruction that vanished silently is exactly the failure this
        agent exists to make visible."""
        self.bus.publish("system.control", Envelope(
            source="Archive",
            destination=envelope.source,
            type="ArchiveWritten",
            content=(f"{result['executed']} written, "
                     f"{result['dropped']} dropped"),
            event_id=envelope.event_id or new_event_id(),
            meta={"executed": result["executed"], "dropped": result["dropped"]},
        ))

    # ---- The store, unchanged (§5.8's two endpoints) ----------------------

    def write(self, kind: str, record: Dict[str, Any]) -> None:
        return self.store.write(kind, record)

    def execute_writes(self, instructions: List[Dict[str, Any]]) -> Dict[str, int]:
        return self.store.execute_writes(instructions)

    def query(self, kind: str, predicate=None, limit: Optional[int] = None):
        return self.store.query(kind, predicate=predicate, limit=limit)

    def query_queue(self, date: Optional[str] = None, predicate=None):
        return self.store.query_queue(date=date, predicate=predicate)

    def log_event(self, topic: str, envelope: Envelope) -> None:
        return self.store.log_event(topic, envelope)

    def set_drive_vectors(self, vectors: Dict[str, float]) -> None:
        return self.store.set_drive_vectors(vectors)

    def get_drive_vectors(self) -> Dict[str, float]:
        return self.store.get_drive_vectors()

    @property
    def root(self):
        return self.store.root

    def __repr__(self) -> str:                    # pragma: no cover - debug aid
        return f"<ArchiveAgent root={self.store.root}>"


__all__ = ["ArchiveAgent", "WRITE_TYPES"]
