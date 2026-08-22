"""
Impulse — MOCK, deterministic tier (§5.3, §13.1).

Phase 0 mock: real drive-vector bookkeeping (it's cheap, deterministic
code — no LLM call either way), but the reflexive "reaction" text is
templated rather than reasoned.

v0.31 — Impulse is now the SOLE trigger into Governance (see revision
notes / §3.2). It receives the raw Sensory input, computes its own
reflex and severity assessment, and forwards ONE envelope to Governance
carrying:
  - content:      the ORIGINAL verbatim input, unmodified — Analytics
                   and Intent downstream need what was actually said,
                   not Impulse's paraphrase of it.
  - meta.reflex:   Impulse's own reaction text (the "flavor" it adds).
  - severity:      combined via OR-upscale-only (bus.envelope.severity_max)
                   with whatever Sensory already tagged. Impulse can
                   RAISE severity based on its own drive-vector state,
                   but can never LOWER a tag Sensory set upstream (e.g.
                   a future vision-modality agent flagging danger).

Guardrail: Impulse's own severity assessment is capped at "Elevated" —
drive-vector state alone (urgency spiking from internal causes) can
never independently produce "Critical". Only an external signal via
Sensory can set Critical; Impulse can amplify up to Elevated but not
manufacture a false alarm past that ceiling. This is deliberate, not
an oversight — see the spec's revision notes for v0.31.
"""
from __future__ import annotations

from bus.envelope import Envelope, severity_max
from bus.pubsub import EmbeddedBus
from agents.archive.store import ArchiveStore

DEFAULT_VECTORS = {
    "curiosity": 0.8,
    "fatigue": 0.1,
    "urgency": 0.0,
    "social_drive": 0.5,
    "temperature": 0.4,      # §15 default seed vectors
}

# Tunable (§15-style): urgency level above which Impulse assesses its
# own severity contribution as "Elevated" instead of "Neutral".
URGENCY_ELEVATED_THRESHOLD = 0.6

# Guardrail ceiling: Impulse's own assessment NEVER exceeds this,
# regardless of vector values. Only Sensory can tag "Critical".
IMPULSE_SEVERITY_CEILING = "Elevated"


class ImpulseMock:
    def __init__(self, bus: EmbeddedBus, archive: ArchiveStore):
        self.bus = bus
        self.archive = archive
        self.vectors = dict(DEFAULT_VECTORS)
        self.archive.set_drive_vectors(self.vectors)
        self.bus.subscribe("events.impulse", self.on_event)

    def _reflex(self) -> str:
        """Templated reflexive reaction — real reasoning arrives when
        this mock is replaced with a live agent per §13.4."""
        if self.vectors["urgency"] > URGENCY_ELEVATED_THRESHOLD:
            return "Terse, protective reaction."
        if self.vectors["curiosity"] > 0.6:
            return "Calm, exploratory reaction."
        return "Calm reaction."

    def _assessed_severity(self) -> str:
        """Impulse's own severity read from current drive-vector state,
        hard-capped at IMPULSE_SEVERITY_CEILING (the guardrail)."""
        if self.vectors["urgency"] > URGENCY_ELEVATED_THRESHOLD:
            return IMPULSE_SEVERITY_CEILING  # "Elevated" — never higher
        return "Neutral"

    def apply_feedback(self, valence: float, driver: str) -> None:
        """Reward path, §4.1: Impulse shifts drive vectors immediately,
        no Intent pre-approval."""
        if driver in self.vectors:
            self.vectors[driver] = max(0.0, min(1.0, self.vectors[driver] + valence))
            self.archive.set_drive_vectors(self.vectors)

    def on_event(self, envelope: Envelope) -> None:
        reflex = self._reflex()
        combined_severity = severity_max(envelope.severity, self._assessed_severity())

        out = envelope.reply(
            source="Impulse",
            destination="Governance",
            type=envelope.type,
            content=envelope.content,   # verbatim original — not the reflex text
            severity=combined_severity,
            triggered_by=envelope.triggered_by,
            meta={
                "reflex": reflex,
                "drive_vectors": dict(self.vectors),
                "source_type": envelope.meta.get("source_type"),
            },
        )
        self.bus.publish("events.governance", out)
