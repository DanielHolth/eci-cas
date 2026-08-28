"""
Impulse — deterministic tier (§5.3, Phase 0.3). No substrate by design.

Runs on every event in parallel with Analytics and Personality. Two
mechanisms beyond the Phase 0 mock:

  1. Vectors DRIFT back toward baseline over wall-clock time at per-vector
     rates (drift_tau_sec). See `_drift()`.
  2. The reaction is a weighted appraisal: five drive vectors collapse into
     three axes (alertness, warmth, engagement) via fixed linear combinations.
     See `_axes()`.

Hard invariant: Impulse's severity assessment is capped at Elevated
(IMPULSE_SEVERITY_CEILING). Only an external Sensory signal can produce
Critical — drive-vector state alone never can.
"""
from __future__ import annotations

import math
import time
from typing import Dict, Optional

from bus.envelope import Envelope, severity_max
from bus.pubsub import EmbeddedBus
from agents.archive.store import ArchiveStore

#: §15 default seed vectors — also the DEFAULT baseline drift relaxes
#: toward, when a manifest doesn't declare its own initial_vectors.
DEFAULT_VECTORS = {
    "curiosity": 0.8,
    "fatigue": 0.1,
    "urgency": 0.0,
    "social_drive": 0.5,
    "temperature": 0.4,
}

#: Per-vector relaxation time constant, in seconds — how long it takes a
#: displaced vector to fall to ~37% (1/e) of its distance from baseline.
#: Short tau = snaps back fast; long tau = slow, temperament-like. Purely
#: a design default: everything here is manifest-tunable
#: (roles.impulse.drift_tau_sec).
#:
#: Deliberately inert at rest: a vector sitting exactly on its baseline
#: (the normal state for every offline test fixture, which fires an
#: event within milliseconds of construction) computes (value - baseline)
#: == 0.0 and stays there bit-for-bit, regardless of elapsed time or tau
#: — so drift is only ever OBSERVABLE once something has displaced a
#: vector away from baseline (manual feedback, a test override, or a
#: future idle-musing/recalibration path). That is what keeps this
#: compatible with the Phase 0 exit criterion's byte-identical traces
#: across two independent cold bootstraps (tests/test_phase0_e2e.py).
DEFAULT_DRIFT_TAU_SEC = {
    "curiosity": 3600.0,
    "fatigue": 1800.0,
    "urgency": 300.0,
    "social_drive": 3600.0,
    "temperature": 7200.0,
}

#: Tunable — how high `alertness` (see _axes) has to read before Impulse
#: raises its own severity assessment from Neutral to Elevated.
URGENCY_ELEVATED_THRESHOLD = 0.6

#: HARD invariant, v0.31/§3 — NOT manifest-configurable. Drive-vector
#: state alone can never produce a "Critical" assessment; only an
#: external signal via Sensory can. See recovery.bootstrap for the
#: refusal to let a manifest override this.
IMPULSE_SEVERITY_CEILING = "Elevated"

#: Appraisal-axis bucket edges. Three buckets keeps the reaction
#: vocabulary small and every choice traceable to "which third of the
#: range is this axis in", rather than a continuous, unexplainable slide.
_BUCKET_EDGES = (0.35, 0.65)

#: dominant axis -> bucket -> reflex text. Deliberately larger than the
#: Phase 0 mock's 3 branches, and keyed on APPRAISAL STATE, never on
#: parsing the event's actual words — Impulse relays content verbatim
#: and has no persona or judgment mandate (§5.3); reacting to its own
#: internal state is the whole of its job, not reading the human's mind.
REACTION_VOCABULARY: Dict[str, Dict[str, str]] = {
    "alertness": {
        "low":  "Calm reaction.",
        "mid":  "Attentive, slightly quickened reaction.",
        "high": "Terse, protective reaction.",
    },
    "warmth": {
        "low":  "Reserved, businesslike reaction.",
        "mid":  "Friendly, even-keeled reaction.",
        "high": "Warm, engaged reaction.",
    },
    "engagement": {
        "low":  "Flat, low-energy reaction.",
        "mid":  "Interested reaction.",
        "high": "Calm, exploratory reaction.",
    },
}


#: Appraisal state -> a face. Read by Governance when an exchange is
#: blocked outright (v0.35e's blocked incident), so the expression the
#: human sees is what the ecosystem ACTUALLY feels at that moment rather
#: than a canned sad emoji stapled onto an error message.
#:
#: Deliberately a small, closed vocabulary: a product layer can map these
#: onto avatar frames, and a text-only front end can say the word. Adding
#: a seventh feeling is a design decision, not a tuning knob.
EXPRESSIONS = ("angry", "scared", "sad", "warm", "alert", "neutral")

#: What a blocked exchange does to the drive vectors (v0.35e). Small,
#: named and fixed — the same discipline as every other nudge in this
#: file: something may ASK for a shift, but the number that actually
#: lands is written here, in code.
#:
#: Frustration reads as: more urgency (this mattered and it didn't work),
#: a little more fatigue (it cost something), slightly less warmth (the
#: persona is not delighted about it). Nothing here can manufacture a
#: Critical severity — the ceiling below still holds.
FRUSTRATION_NUDGE = {
    "urgency": 0.15,
    "fatigue": 0.05,
    "temperature": -0.05,
}


def _clamp(x: float) -> float:
    return max(0.0, min(1.0, x))


def _bucket(score: float) -> str:
    low, high = _BUCKET_EDGES
    if score < low:
        return "low"
    if score < high:
        return "mid"
    return "high"


class Impulse:
    """Real Phase 0.3 Impulse. See the module docstring for the two
    things that changed from the Phase 0 mock (drift, weighted appraisal)
    and the one thing that is structurally guaranteed not to (the
    Elevated ceiling)."""

    def __init__(self, bus: EmbeddedBus, archive: ArchiveStore, *,
                 initial_vectors: Optional[Dict[str, float]] = None,
                 urgency_elevated_threshold: float = URGENCY_ELEVATED_THRESHOLD,
                 drift_tau_sec: Optional[Dict[str, float]] = None):
        self.bus = bus
        self.archive = archive

        self.vectors: Dict[str, float] = dict(DEFAULT_VECTORS)
        if initial_vectors:
            self.vectors.update(initial_vectors)
        #: What drift relaxes toward. The seeded state IS the baseline —
        #: a manifest that seeds an unusually anxious or curious persona
        #: means THAT is what "at rest" looks like for this instance, not
        #: the code's own DEFAULT_VECTORS.
        self._baseline: Dict[str, float] = dict(self.vectors)

        self.urgency_elevated_threshold = float(urgency_elevated_threshold)
        self.drift_tau_sec: Dict[str, float] = dict(DEFAULT_DRIFT_TAU_SEC)
        if drift_tau_sec:
            self.drift_tau_sec.update(drift_tau_sec)

        self._last_update = time.monotonic()
        self.metrics: Dict[str, int] = {"events": 0, "frustrations": 0,
                                        "criticals": 0}
        self.archive.set_drive_vectors(self.vectors)
        self.bus.subscribe("events.impulse", self.on_event)
        self.bus.subscribe("system.control", self.on_control)

    # ---- Drift (§4.1) ------------------------------------------------------

    def _drift(self) -> None:
        """Relax every vector toward its baseline by the wall-clock time
        elapsed since the last update, exponentially, per-vector. See the
        DEFAULT_DRIFT_TAU_SEC docstring for why this is a no-op at rest."""
        now = time.monotonic()
        elapsed = max(0.0, now - self._last_update)
        self._last_update = now
        if elapsed <= 0:
            return

        changed = False
        for name, value in self.vectors.items():
            baseline = self._baseline.get(name, value)
            if value == baseline:
                continue                      # exact no-op, see module docstring
            tau = self.drift_tau_sec.get(name)
            if not tau or tau <= 0:
                continue
            decay = math.exp(-elapsed / tau)
            self.vectors[name] = _clamp(baseline + (value - baseline) * decay)
            changed = True
        if changed:
            self.archive.set_drive_vectors(self.vectors)

    # ---- Appraisal (the reaction engine) -----------------------------------

    def _axes(self) -> Dict[str, float]:
        """Five drive vectors collapse into three legible appraisal axes.
        Fixed, documented linear combinations — a formula, not a model.
        Weights are a first cut, not tuned against real data; revisit
        once there's a live LLM-backed variant to compare against."""
        v = self.vectors
        return {
            "alertness":  _clamp(v["urgency"] - 0.3 * v["fatigue"]),
            "warmth":     _clamp(0.6 * v["social_drive"] + 0.4 * v["temperature"]),
            "engagement": _clamp(v["curiosity"] - 0.4 * v["fatigue"]),
        }

    def _reflex(self) -> str:
        axes = self._axes()
        dominant, score = max(axes.items(), key=lambda kv: kv[1])
        return REACTION_VOCABULARY[dominant][_bucket(score)]

    def _assessed_severity(self) -> str:
        """Impulse's own severity read, from current appraisal state,
        hard-capped at IMPULSE_SEVERITY_CEILING. See the module docstring
        — this cap is a safety invariant, not tidiness."""
        if self._axes()["alertness"] > self.urgency_elevated_threshold:
            return IMPULSE_SEVERITY_CEILING   # never higher, regardless of score
        return "Neutral"

    def expression(self) -> str:
        """The face this appraisal state implies. READ-ONLY — nothing is
        mutated, and Impulse is the only thing that ever decides it.

        Governance asks for this when an exchange has been blocked twice
        and there is nothing left to say (v0.35e). It is the same
        appraisal the reflex vocabulary is drawn from, collapsed one step
        further: three axes, one word."""
        axes = self._axes()
        alert, warm, engaged = (axes["alertness"], axes["warmth"],
                                axes["engagement"])
        high, low = _BUCKET_EDGES[1], _BUCKET_EDGES[0]

        if alert >= high:
            # Aroused. Which way it reads depends on whether there is any
            # warmth behind it.
            return "angry" if warm < low else "scared"
        if engaged < low and alert < low:
            return "sad"
        if warm >= high:
            return "warm"
        if alert >= low:
            return "alert"
        return "neutral"

    # ---- Feedback (§4.1 reward path) ---------------------------------------

    def apply_feedback(self, valence: float, driver: str) -> None:
        """Reward path: Impulse shifts drive vectors immediately, no
        Intent pre-approval. This is the immediate-shift half of §4.1;
        _drift() is the gradual-relaxation half."""
        if driver in self.vectors:
            self.vectors[driver] = _clamp(self.vectors[driver] + valence)
            self.archive.set_drive_vectors(self.vectors)

    # ---- Recalibration (§5.3's "only Intent adjusts temperature, during
    # consolidation") — Phase 0.4's "slow coloring" coupling -----------------

    def recalibrate_baseline(self, vector: str, delta: float,
                             rationale: str = "") -> bool:
        """Shift what a vector DRIFTS BACK TOWARD, not its live value.

        This is the distinction that makes it "slow coloring" rather than
        a per-event nudge: `_drift()` always relaxes `self.vectors[name]`
        toward `self._baseline[name]` (see the module docstring's §4.1
        split — immediate shift vs. gradual relaxation). Moving the
        BASELINE means every future relaxation settles somewhere new,
        which is what lets a temperament actually shift across many
        consolidation cycles instead of jumping once and drifting straight
        back to where it started.

        Deliberately Intent-only in practice (§5.3: "only Intent adjusts
        the temperature, during consolidation — a values judgment, not a
        security one") — nothing else in the ecosystem calls this. Also
        deliberately small per call: agents/intent/live.py's
        IntentAgent._parse_consolidation clamps every value to
        [-0.2, 0.2] before it ever reaches here, the same
        enforce-it-at-the-boundary discipline as
        IMPULSE_SEVERITY_CEILING — a manifest or a model can ask for more,
        but the number that actually lands is capped regardless.

        Returns False (silent no-op) for an unrecognized vector name,
        matching apply_feedback()'s posture — there's no vector to shift,
        so there's nothing to raise an error about."""
        if vector not in self._baseline:
            return False
        self._baseline[vector] = _clamp(self._baseline[vector] + delta)
        # The live value doesn't jump — only where it's headed changes.
        # _drift() will carry it there over drift_tau_sec, same as any
        # other displacement from baseline. Nothing in self.vectors
        # changed, so there's nothing new to persist via
        # set_drive_vectors() here; the baseline itself isn't yet
        # persisted across restarts (Phase 0's in-memory posture, same as
        # every other piece of Impulse's live state — see the module
        # docstring's "no-op-at-rest" note on why that's fine for now).
        return True

    # ---- Control plane ------------------------------------------------------

    def on_control(self, envelope: Envelope) -> None:
        """Governance says an exchange was blocked and dropped (v0.35e).

        A nudge, not a command: Governance publishes the FACT and holds no
        reference to what happens next — Impulse owns what any signal does
        to its own drive vectors, exactly as it owns what an event does.
        The shift is immediate (§4.1's reward path), and `_drift()` will
        carry it back toward baseline afterwards like any other
        displacement, so frustration fades rather than accumulating."""
        if envelope.type != "Frustration" or envelope.destination != "Impulse":
            return
        self._drift()
        for vector, delta in FRUSTRATION_NUDGE.items():
            self.apply_feedback(delta, vector)
        self.metrics["frustrations"] += 1

    # ---- Bus ----------------------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1
        self._drift()
        reflex = self._reflex()
        combined_severity = severity_max(envelope.severity, self._assessed_severity())
        if combined_severity == "Critical":
            # Only ever from an upstream Sensory tag — Impulse's own
            # assessment is capped below that (IMPULSE_SEVERITY_CEILING).
            # Counted here because this is the one read that opens the
            # fast path past cognition (v0.35d).
            self.metrics["criticals"] += 1

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


__all__ = [
    "Impulse", "DEFAULT_VECTORS", "DEFAULT_DRIFT_TAU_SEC",
    "URGENCY_ELEVATED_THRESHOLD", "IMPULSE_SEVERITY_CEILING",
    "REACTION_VOCABULARY", "EXPRESSIONS", "FRUSTRATION_NUDGE",
]
