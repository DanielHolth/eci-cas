"""
Embedded pub-sub bus — ECI-spec-v0-30.md §3.

Phase 0 uses an in-memory, synchronous, single-process bus
(manifest: message_bus.type = "embedded-pubsub"). Topics:

    events.sensory | events.impulse | events.governance | events.analytics
    events.intent  | events.security | events.action
    system.diagnostic   (BootCheck / SystemCheck synthetic pings, §9)
    system.control       (Governance -> Intent-node rotation signals, §7.2)

Every publish is:
  1. dispatched synchronously to all subscribers of that topic, and
  2. appended to Archive's hot queue log (/archive/queue/events_*.jsonl)
     so every hop is inspectable per §13.2/§13.3 exit criteria.

Control traffic (system.*) never mixes with business events (events.*) —
kept as separate topic namespaces per §3, and NOT logged into the
business queue log (Watchdog's Level 1 ping is explicitly zero-footprint
per §11.1).
"""
from __future__ import annotations

from collections import defaultdict
from typing import Callable, Dict, List

from bus.envelope import Envelope

BUSINESS_TOPICS = {
    "events.sensory", "events.impulse", "events.governance",
    "events.analytics", "events.intent", "events.security", "events.action",
}
SYSTEM_TOPICS = {"system.diagnostic", "system.control"}
ALL_TOPICS = BUSINESS_TOPICS | SYSTEM_TOPICS

Handler = Callable[[Envelope], None]


class EmbeddedBus:
    def __init__(self, archive=None):
        self._subscribers: Dict[str, List[Handler]] = defaultdict(list)
        self._trace: List[Envelope] = []          # in-memory trace, for tests/harness
        self.archive = archive                    # ArchiveStore, optional (Phase 0 mocks may run without it)

    def subscribe(self, topic: str, handler: Handler) -> None:
        if topic not in ALL_TOPICS:
            raise ValueError(f"Unknown topic '{topic}'. Known topics: {sorted(ALL_TOPICS)}")
        self._subscribers[topic].append(handler)

    def publish(self, topic: str, envelope: Envelope) -> None:
        if topic not in ALL_TOPICS:
            raise ValueError(f"Unknown topic '{topic}'. Known topics: {sorted(ALL_TOPICS)}")

        self._trace.append(envelope)

        # Business events are logged to Archive's hot queue (§13.2). System
        # topics are control-plane and intentionally excluded (§3, §11.1) —
        # Level 1 Watchdog pings must have zero queue footprint.
        if topic in BUSINESS_TOPICS and self.archive is not None:
            self.archive.log_event(topic, envelope)

        for handler in list(self._subscribers.get(topic, [])):
            handler(envelope)

    def trace(self) -> List[Envelope]:
        """Full in-process trace of everything published this run, in order."""
        return list(self._trace)

    def reset_trace(self) -> None:
        self._trace.clear()
