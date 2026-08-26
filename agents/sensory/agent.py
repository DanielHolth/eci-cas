"""
Sensory — REAL, deterministic (§5.2, §13.1).

"It's essentially an input field plus source-tagging; there is nothing
meaningful to mock. It is also the injection point for every test."

Two roles:
  1. External injection point — ingest(content, source_type) is called
     directly (by the test harness, a CLI, or later a real UI/webhook)
     with a human prompt or a feedback signal (§3.1). Sensory tags it and
     FANS IT OUT to four agents in parallel (v0.35a): Impulse,
     Analytics, Personality and Knowledge, each receiving its own copy
     of the same event.

     This is the one hop in the whole pipeline with no Governance in
     between — deliberate, confirmed repeatedly during the v0.35 design
     pass, and the single exception to Governance's otherwise-universal
     routing (v0.35c). The reason is latency: four short, cheap,
     independent calls racing in parallel beat one long call, or a serial
     chain, doing all four jobs. Nothing is lost by fanning them out,
     because none of the four needs to see another's answer to do its
     own job — every cognitive call in this system is stateless anyway.

     Governance picks the four answers back up, buffers them, and sends
     ONE bundled message to Intent.

     This replaces v0.31's strict relay, where Sensory forwarded to
     Impulse alone and Impulse was "the sole trigger into Governance".
     Impulse still gets first look in the sense that matters — it is
     still the only agent that can open the Critical fast path (v0.35d),
     and it is still published to first below, so its reflex is on the
     wire before the other three are even dispatched.

     2026-08-25 (Daniel): "truly async," not just fanned-out-but-still-
     sequential. Until now the embedded bus dispatched every publish()
     synchronously (bus/pubsub.py), so on a single thread the three
     cognitive workers' slow substrate calls still ran one after another
     behind the scenes — Impulse, then Analytics, then Personality, then
     Knowledge, each blocking the next. Impulse stays exactly where it
     was: synchronous, first, deterministic, near-instant, and the only
     one that can open the Critical fast path. Analytics, Personality and
     Knowledge are genuinely independent of each other and of Impulse —
     none needs to see another's answer, every cognitive call in this
     system is stateless anyway — so their dispatch now runs on a small
     thread pool (COGNITIVE_FAN_OUT below) instead of a loop. ingest()
     still blocks until all three have actually finished; Governance still
     buffers on completeness, not arrival order (EventState.ready()), so
     nothing about the bundling contract changed — only the wall-clock.
  2. Bus re-entry point — subscribed to events.sensory, kept for
     protocol completeness (§3's topic list) and any future external
     source that publishes directly onto the bus rather than calling
     ingest(). v0.32: Action's outcomes no longer route here (see
     revision notes) — a failed action reports straight to Governance,
     which commanded it and owns deciding what happens next; a
     successful action is silent. This topic is currently a no-op in
     Phase 0's flow.

Severity (v0.31): Sensory may tag incoming content with a severity from
the start of the chain (e.g. a vision-modality agent flagging danger).
Downstream agents (Impulse, Governance) may only ever RAISE this tag,
never lower it — see bus.envelope.severity_max.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from typing import Optional

from bus.envelope import Envelope, new_event_id, SEVERITY_LEVELS
from bus.pubsub import EmbeddedBus

VALID_SOURCE_TYPES = {"prompt", "feedback", "vision", "audio", "https"}

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
               event_id: Optional[str] = None) -> str:
        """External injection point (§13.1). Returns the event_id so
        callers/tests can correlate the resulting trace."""
        if source_type not in VALID_SOURCE_TYPES:
            raise ValueError(f"Unknown source type '{source_type}'. Valid: {VALID_SOURCE_TYPES}")
        if severity not in SEVERITY_LEVELS:
            raise ValueError(f"Unknown severity '{severity}'. Valid: {SEVERITY_LEVELS}")

        eid = event_id or new_event_id()

        # v0.35a: the four-way fan-out. Each worker gets its OWN envelope
        # — same event_id, same verbatim content, its own destination —
        # so Governance can tell four answers to one event apart from one
        # answer to four events.
        def _envelope(destination: str) -> Envelope:
            return Envelope(
                source="Sensory", destination=destination, type=source_type,
                content=content, severity=severity, event_id=eid,
                triggered_by=triggered_by,
                meta={"source_type": source_type},
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
