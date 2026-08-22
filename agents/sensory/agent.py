"""
Sensory — REAL, deterministic (§5.2, §13.1).

"It's essentially an input field plus source-tagging; there is nothing
meaningful to mock. It is also the injection point for every test."

Two roles:
  1. External injection point — ingest(content, source_type) is called
     directly (by the test harness, a CLI, or later a real UI/webhook)
     with a human prompt or a feedback signal (§3.1). Sensory tags it
     and fans it out IN PARALLEL to Impulse and Governance, matching the
     §3.2 worked example ("Sensory and Impulse fire in parallel").
  2. Bus re-entry point — subscribed to events.sensory, which is where
     Action's outcomes re-enter (proprioception, §4), where feedback
     signals are ingested (§4.1), and where Recovery's synthetic
     BootCheck / SystemCheck pings arrive (§9).
"""
from __future__ import annotations

from typing import Optional

from bus.envelope import Envelope, new_event_id
from bus.pubsub import EmbeddedBus

VALID_SOURCE_TYPES = {"prompt", "feedback", "vision", "audio", "https"}


class Sensory:
    def __init__(self, bus: EmbeddedBus):
        self.bus = bus
        self.bus.subscribe("events.sensory", self.on_reentry)

    def ingest(self, content, source_type: str = "prompt",
               severity: str = "Restful", triggered_by: str = "sensory",
               event_id: Optional[str] = None) -> str:
        """External injection point (§13.1). Returns the event_id so
        callers/tests can correlate the resulting trace."""
        if source_type not in VALID_SOURCE_TYPES:
            raise ValueError(f"Unknown source type '{source_type}'. Valid: {VALID_SOURCE_TYPES}")

        eid = event_id or new_event_id()

        # Fan out in parallel to Impulse and directly to Governance (§3.2).
        to_impulse = Envelope(
            source="Sensory", destination="Impulse", type=source_type,
            content=content, severity=severity, event_id=eid, triggered_by=triggered_by,
            meta={"source_type": source_type},
        )
        to_governance = Envelope(
            source="Sensory", destination="Governance", type=source_type,
            content=content, severity=severity, event_id=eid, triggered_by=triggered_by,
            meta={"source_type": source_type},
        )
        self.bus.publish("events.impulse", to_impulse)
        self.bus.publish("events.governance", to_governance)
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
        """Bus re-entry: Action outcomes (proprioception), Recovery pings,
        or feedback signals landing on events.sensory. Phase 0 mock
        behavior: log-only (already logged by the bus's Archive
        write-through) — no further fan-out, since an Action outcome is
        an end-state, not a new prompt requiring a fresh pipeline run."""
        pass
