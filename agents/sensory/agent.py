"""
Sensory — deterministic input agent (§5.2, §13.1).

External injection point: ingest(content, source_type) tags input and
fans it out to Impulse, Analytics, and Personality in parallel. Impulse
is dispatched first (synchronous) because it gates the Critical fast
path. The other workers run concurrently on a thread pool.

This is the one hop with no Governance in between — the four agents are
independent and stateless, so parallel dispatch is safe. Governance
buffers all answers and bundles them for Intent.

Severity may be tagged from the start; downstream agents may only raise
it (OR-upscale-only, bus.envelope.severity_max).
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from typing import Optional

from bus.envelope import Envelope, new_event_id, SEVERITY_LEVELS
from bus.pubsub import EmbeddedBus

#: "idea" (dispatch #4, 2026-08-29): the Reflection Agent's re-entry point
#: — a resurfaced pattern genuinely IS a perception, the same reasoning
#: docs/ideas/consolidation-doodle.md makes for "ui_click".
VALID_SOURCE_TYPES = {"prompt", "feedback", "vision", "audio", "https", "ui_click", "idea"}

#: The v0.35a fan-out. Impulse first — see ingest(). Order is otherwise
#: irrelevant to correctness (Governance bundles on completeness, not
#: arrival order) but it is fixed rather than derived so a trace reads the
#: same way every run, which is what the Phase 0 byte-identical-trace exit
#: criterion depends on.
FAN_OUT = (
    ("Impulse", "events.impulse"),
    ("Analytics", "events.analytics"),
    ("Personality", "events.personality"),
)

#: The subset of FAN_OUT dispatched concurrently (2026-08-25, Daniel) —
#: every archive-grounded/analytical worker except Impulse, which stays
#: synchronous and first. One pool covers however many of this family
#: exist; they are identical in shape (one substrate call, one event),
#: differing only in system instruction and which Archive store they read.
COGNITIVE_FAN_OUT = tuple(pair for pair in FAN_OUT if pair[0] != "Impulse")


class Sensory:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.bus.subscribe("events.sensory", self.on_reentry)

    def ingest(self, content, source_type: str = "prompt",
               severity: str = "Neutral", triggered_by: str = "sensory",
               event_id: Optional[str] = None,
               ref_event_id: Optional[str] = None) -> str:
        """External injection point (§13.1). Returns the event_id so
        callers/tests can correlate the resulting trace.

        `ref_event_id` is the one addition the consolidation doodle needs
        (docs/ideas/consolidation-doodle.md): a `ui_click` is its own new
        event (it gets its own `event_id` like anything else), but it
        needs to say WHICH consolidation write-pass it's about — the
        event_id of the Consolidator pass the click references. Kept
        minimal on purpose: this is the one piece of caller-supplied
        context the doodle needs, not a general extra-meta passthrough."""
        if source_type not in VALID_SOURCE_TYPES:
            raise ValueError(f"Unknown source type '{source_type}'. Valid: {VALID_SOURCE_TYPES}")
        if severity not in SEVERITY_LEVELS:
            raise ValueError(f"Unknown severity '{severity}'. Valid: {SEVERITY_LEVELS}")

        eid = event_id or new_event_id()
        meta = {"source_type": source_type}
        if ref_event_id is not None:
            meta["ref_event_id"] = ref_event_id

        # v0.35a: the four-way fan-out. Each worker gets its OWN envelope
        # — same event_id, same verbatim content, its own destination —
        # so Governance can tell four answers to one event apart from one
        # answer to four events.
        def _envelope(destination: str) -> Envelope:
            return Envelope(
                source="Sensory", destination=destination, type=source_type,
                content=content, severity=severity, event_id=eid,
                triggered_by=triggered_by,
                meta=dict(meta),
            )

        # Impulse is published to first, synchronously, deliberately: it
        # is the only agent that can open the Critical fast path (v0.35d),
        # and this guarantees an emergency is already on its way to
        # Security before the other three are even dispatched.
        self.bus.publish("events.impulse", _envelope("Impulse"))

        # 2026-08-25 (Daniel): "truly async" — the other three are
        # genuinely independent (of Impulse and of each other) and each
        # blocks on its own slow substrate call, so they run concurrently
        # on a small thread pool rather than one after another. ingest()
        # still returns only once every worker has actually finished — no
        # timeout, no partial fan-out, nothing about EventState.ready()'s
        # completeness check had to change.
        with ThreadPoolExecutor(max_workers=len(COGNITIVE_FAN_OUT)) as pool:
            futures = [pool.submit(self.bus.publish, topic, _envelope(destination))
                       for destination, topic in COGNITIVE_FAN_OUT]
            for future in futures:
                future.result()   # re-raise here, not silently, if a worker blew up
        return eid

    def inject_diagnostic_ping(self, kind: str, event_id: Optional[str] = None) -> str:
        """Recovery's synthetic BootCheck / SystemCheck (§9, §11 Level 2).

        Published on system.diagnostic, NOT events.sensory — these bypass
        Action entirely and are structurally distinct from real events
        (§9's boundary with the live queue). Sensory is the channel
        Recovery uses to inject them, per §5.2 / §9.
        """
        if kind not in ("BootCheck", "SystemCheck"):
            raise ValueError("kind must be 'BootCheck' or 'SystemCheck'")
        eid = event_id or new_event_id()
        ping = Envelope(
            source="Recovery", destination="Governance", type=kind,
            content=f"{kind} liveness probe", event_id=eid,
        )
        self.bus.publish("system.diagnostic", ping)
        return eid

    def on_reentry(self, envelope: Envelope) -> None:
        """v0.32: no-op. events.sensory currently has no real publisher —
        Action no longer reports here (see revision notes). Kept as a
        subscribed no-op for protocol completeness and forward
        compatibility with a future external source publishing directly
        onto the bus, rather than removing the topic outright."""
        pass
