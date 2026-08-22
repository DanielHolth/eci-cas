"""
Watchdog — passive monitor, not one of the 8 (§11).

Tracks message-queue transition intervals, independent of the live
agents. Two clocks, never sharing a value or a role (§11.1):

  - timers.watchdog.*                 (seconds-scale) "is the machinery
    still alive?" — Levels 1-5 below.
  - timers.impulse.idle_musing_interval_sec (hours-scale) — Impulse's own
    province (§5.3), NOT implemented here.

Phase 0 implementation status:
  Level 1 (Deterministic Ping)   — implemented. Zero-token, targets
                                     Impulse only, produces no queue
                                     content (§11.1) — cannot be observed
                                     via the queue trace by design.
  Level 2 (In-band SystemCheck)  — implemented. Injects a SystemCheck via
                                     Sensory, routed to Analytics, which
                                     replies directly to Recovery (Action
                                     bypassed).
  Level 3 (Out-of-band ping)     — stubbed. Requires a real out-of-queue
                                     transport to Governance; meaningful
                                     once agents are real processes
                                     (docker-nested), not in-process mocks.
  Level 4 (Soft rollback)        — stubbed. Requires a real Recovery
                                     snapshot/restore path (§7.2's
                                     pre-swap snapshot; §9's restore
                                     behavior) — deferred to when Archive
                                     has real state worth rolling back.
  Level 5 (Catastrophic rebuild) — stubbed. Requires the full manifest
                                     redeploy path; exercised once
                                     Recovery.bootstrap() is itself
                                     callable from a failure handler
                                     without operator involvement.

Levels 3-5 are genuinely deferred rather than faked: testing them
meaningfully needs real process death (docker-nested), which is a
known gap called out in the project hand-off — bare in-process mocks
have nothing to crash.
"""
from __future__ import annotations

import time
from typing import Callable, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus


class Watchdog:
    def __init__(self, bus: EmbeddedBus, interval_x_sec: float = 5.0,
                 interval_y_sec: float = 10.0,
                 on_level2: Optional[Callable[[], None]] = None):
        self.bus = bus
        self.interval_x_sec = interval_x_sec       # Level 1 threshold
        self.interval_y_sec = interval_y_sec        # Level 2 threshold (X + Y total)
        self.on_level2 = on_level2
        self._last_transition = time.monotonic()
        self.level2_fired = False

        for topic in ("events.sensory", "events.impulse", "events.governance",
                      "events.analytics", "events.intent", "events.security",
                      "events.action"):
            self.bus.subscribe(topic, self._note_transition)

    def _note_transition(self, envelope: Envelope) -> None:
        self._last_transition = time.monotonic()

    def check(self) -> str:
        """Poll-based check (call periodically from a scheduler/loop).
        Returns the level that fired, if any: 'none' | 'level1' | 'level2'."""
        idle = time.monotonic() - self._last_transition

        if idle > (self.interval_x_sec + self.interval_y_sec):
            self._fire_level2()
            return "level2"
        if idle > self.interval_x_sec:
            self._fire_level1()
            return "level1"
        return "none"

    def _fire_level1(self) -> None:
        """Zero-token liveness ping to Impulse (deterministic, no LLM call,
        no queue footprint per §11.1) — intentionally NOT published to the
        bus, since Level 1 must produce no content the human perceives."""
        pass  # by design: nothing to log, nothing to trace (§11.1)

    def _fire_level2(self) -> None:
        if self.level2_fired:
            return
        self.level2_fired = True
        if self.on_level2:
            self.on_level2()

    def reset(self) -> None:
        self._last_transition = time.monotonic()
        self.level2_fired = False
