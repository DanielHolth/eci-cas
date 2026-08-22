"""
Message envelope for the ECI-CAS pub-sub bus.

Matches ECI-spec-v0-31.md §3:
    { Source, Destination, Type, Content, Severity }

We extend the spec's minimal envelope with two internal-only fields that
never change the wire contract described in the spec:

- event_id:     correlates every hop of a single pipeline run (needed for
                 Governance's Sensory+Impulse merge, §3.2, and for
                 Watchdog's transition-interval tracking, §11).
- triggered_by: "sensory" | "self" — required by Impulse's idle-musing
                 tagging rule (§5.3) so Governance can tell reflex from
                 unprompted musing.
"""
from __future__ import annotations

import uuid
import time
from dataclasses import dataclass, field, asdict
from typing import Any, Dict, Optional


def new_event_id() -> str:
    return uuid.uuid4().hex[:12]


# Severity scale (v0.31) — ordered low to high. "Restful" is a genuine
# positive/thriving read, not just "nothing happening"; "Neutral" is the
# ordinary default baseline. Combine rule is OR-upscale-only: any agent
# along the chain may raise severity, none may lower a tag set upstream
# (§3, §5.3 in the spec's revision notes).
SEVERITY_LEVELS = ["Restful", "Neutral", "Elevated", "Critical"]


def severity_rank(severity: str) -> int:
    try:
        return SEVERITY_LEVELS.index(severity)
    except ValueError:
        raise ValueError(f"Unknown severity '{severity}'. Valid: {SEVERITY_LEVELS}")


def severity_max(a: str, b: str) -> str:
    """OR-upscale-only combine: returns whichever of a/b is higher on the
    scale. Never used to downscale — callers are responsible for only
    ever combining forward (e.g. Impulse combining its own capped
    assessment with Sensory's incoming tag), never discarding a higher
    upstream tag."""
    return a if severity_rank(a) >= severity_rank(b) else b


@dataclass
class Envelope:
    source: str
    destination: str
    type: str
    content: Any
    severity: str = "Neutral"
    event_id: str = field(default_factory=new_event_id)
    triggered_by: str = "sensory"          # sensory | self  (§5.3)
    timestamp: str = field(default_factory=lambda: time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
    meta: Dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    def reply(self, *, source: str, destination: str, type: str,
              content: Any, severity: Optional[str] = None,
              triggered_by: Optional[str] = None,
              meta: Optional[Dict[str, Any]] = None) -> "Envelope":
        """Build a follow-on envelope that keeps the same event_id (correlation)."""
        return Envelope(
            source=source,
            destination=destination,
            type=type,
            content=content,
            severity=severity or self.severity,
            event_id=self.event_id,
            triggered_by=triggered_by or self.triggered_by,
            meta=meta or {},
        )
