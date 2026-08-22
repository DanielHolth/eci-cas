"""
Sensory — REAL, deterministic (§5.2, §13.1).

"It's essentially an input field plus source-tagging; there is nothing
meaningful to mock. It is also the injection point for every test."

Two roles:
  1. External injection point — ingest(content, source_type) is called
     directly (by the test harness, a CLI, or later a real UI/webhook)
     with a human prompt or a feedback signal (§3.1). Sensory tags it
     and forwards it to Impulse ONLY (v0.31: strict relay, not a
     parallel merge — see revision notes). Impulse is the sole trigger
     into Governance; this gives Impulse first look at every input,
     including from future non-text modalities (e.g. a vision agent
     that can itself flag danger), before Governance ever sees it.
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

from typing import Optional

from bus.envelope import Envelope, new_event_id, SEVERITY_LEVELS
from bus.pubsub import EmbeddedBus

VALID_SOURCE_TYPES = {"prompt", "feedback", "vision", "audio", "https"}


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

        # v0.31: single hop to Impulse — Impulse is the sole trigger into
        # Governance (see agents/impulse/agent.py). No parallel fan-out.
        to_impulse = Envelope(
            source="Sensory", destination="Impulse", type=source_type,
            content=content, severity=severity, event_id=eid, triggered_by=triggered_by,
            meta={"source_type": source_type},
        )
        self.bus.publish("events.impulse", to_impulse)
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
