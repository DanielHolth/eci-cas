"""
Budget mode — adaptive substrate throttling (Phase 0.2.1).

Real reasoning while the substrate is healthy and the spend is within
budget; the deterministic fallbacks the pipeline already has when it
isn't. The pipeline never stops, and it never degrades silently.

What this is, honestly
----------------------
The original design targeted a Claude Pro subscription and latched on
"Pro quota exhausted". That mechanism doesn't exist: a Pro subscription
can't serve an API client, so there was no such error to catch. The
architecture around it was right, though, and every scenario it described
still works — the trigger just needed to be something real.

So budget mode latches on things that actually happen to an API-backed
substrate:

  manual         a human said so
  terminal       one failure that cannot resolve itself (bad key,
                 exhausted credit, unknown model) — latch immediately,
                 because the next call fails identically
  transient      N consecutive failures that COULD resolve (rate limit,
                 overload, timeout) — latch after the run, not the first
  spend cap      estimated spend crossed the manifest's ceiling

The asymmetry between terminal and transient is the point. The vendor SDK
has already retried transients with backoff before we ever see one, so a
single transient reaching us is worth counting, not panicking over. A
terminal failure retried is pure waste.

What it deliberately does not do
--------------------------------
It doesn't touch topology. Governance still routes identically, Impulse
is unaffected, Security stays deterministic, and Analytics keeps the same
interface — it simply doesn't call its substrate. The per-task fallbacks
in agents/analytics/contract.py are reused verbatim, so budget mode has
no fallback behaviour of its own to get wrong: Evaluate degrades and
proceeds, Review and Revise decline.

That inherited asymmetry matters more here than anywhere else. Budget
mode is most likely to be active exactly when something is already wrong,
and a gate that approves things because the reasoner is unavailable would
be worse than no gate at all.

The spend figure is an ESTIMATE
-------------------------------
Cost is computed from reported token usage against manifest-declared list
prices. It ignores cache discounts, batch pricing and tier differences,
and it can only ever be as good as those numbers. Treat it as a smoke
alarm — good enough to catch a runaway loop, not an invoice. The real
figure lives in the vendor's console, and the cap is a backstop for
mistakes rather than an accounting control.
"""
from __future__ import annotations

import time
from dataclasses import asdict, dataclass, field
from typing import Any, Callable, Dict, List, Optional

from substrates.base import FailureKind

LIVE = "live"
BUDGET = "budget"

#: Consecutive TRANSIENT failures before latching. One is noise — the SDK
#: already retried it. Three in a row is a pattern.
DEFAULT_FAILURE_THRESHOLD = 3

#: Estimated USD ceiling. Crossing it latches budget mode.
DEFAULT_SPEND_CAP_USD = 5.0

#: Fraction of the cap that triggers a one-time warning.
DEFAULT_WARN_AT = 0.5


@dataclass
class BudgetState:
    """Current mode plus the telemetry the decision is made from."""

    mode: str = LIVE
    reason: Optional[str] = None            # manual | terminal | transient | spend_cap
    detail: str = ""
    since: Optional[str] = None             # ISO-8601, when the mode last changed

    consecutive_failures: int = 0
    total_failures: int = 0
    calls: int = 0
    tokens_in: int = 0
    tokens_out: int = 0
    spend_usd: float = 0.0
    warned_at_fraction: bool = False

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "BudgetState":
        known = {f for f in cls.__dataclass_fields__}
        return cls(**{k: v for k, v in (data or {}).items() if k in known})

    @property
    def is_live(self) -> bool:
        return self.mode == LIVE


class BudgetManager:
    """Owns the mode and the counters. One instance, shared by the agents
    that call substrates.

    Deliberately NOT an agent: it has no inbox, publishes nothing, and
    sits outside the eight (§2.2's pattern for Recovery, Watchdog and
    Diagnostic). Budget mode is control-plane state, not queue traffic.
    """

    def __init__(self, archive=None, *,
                 failure_threshold: int = DEFAULT_FAILURE_THRESHOLD,
                 spend_cap_usd: Optional[float] = DEFAULT_SPEND_CAP_USD,
                 warn_at: float = DEFAULT_WARN_AT,
                 enabled: bool = True,
                 initial_mode: str = LIVE,
                 on_change: Optional[Callable[[str, "BudgetState"], None]] = None):
        self.archive = archive
        self.failure_threshold = max(1, int(failure_threshold))
        self.spend_cap_usd = spend_cap_usd
        self.warn_at = warn_at
        self.enabled = enabled
        self.on_change = on_change

        #: Alerts raised since the last drain. The console surfaces these;
        #: they deliberately do NOT go through Action, which executes what
        #: Governance hands it and authors nothing of its own (§5.7).
        self.alerts: List[str] = []

        self.state = self._restore() or BudgetState(mode=initial_mode)

    # ---- Persistence ------------------------------------------------------

    def _restore(self) -> Optional[BudgetState]:
        """Load the last persisted state, so a latch survives a restart.

        Without this, a system that latched overnight comes back live and
        starts spending again before anyone notices."""
        if self.archive is None:
            return None
        try:
            records = self.archive.query("budget", limit=1)
        except Exception:
            return None
        if not records:
            return None
        try:
            return BudgetState.from_dict(records[-1])
        except Exception:
            return None

    def save(self) -> None:
        if self.archive is None:
            return
        try:
            self.archive.write("budget", self.state.to_dict())
        except Exception:
            # Losing a state write must never break the pipeline. The
            # in-memory mode is still correct for this session.
            pass

    # ---- Mode changes -----------------------------------------------------

    def _switch(self, mode: str, reason: str, detail: str = "") -> bool:
        if self.state.mode == mode:
            return False
        self.state.mode = mode
        self.state.reason = reason
        self.state.detail = detail[:300]
        self.state.since = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        if mode == LIVE:
            self.state.consecutive_failures = 0
        self.save()
        if self.on_change:
            self.on_change(mode, self.state)
        return True

    def switch_manual(self, mode: str) -> str:
        """A human chose. Manual always wins, including re-enabling live
        after a spend-cap latch — the cap is a guard against mistakes, not
        a lock the operator can't open."""
        mode = LIVE if str(mode).lower().startswith("l") else BUDGET
        if mode == LIVE:
            self.state.consecutive_failures = 0
            self.state.warned_at_fraction = False
        changed = self._switch(mode, "manual", "requested by the operator")
        if not changed:
            return f"Already in {mode} mode."
        if mode == BUDGET:
            return ("Switched to budget mode. Analytics will use its "
                    "deterministic fallbacks; no substrate calls, no spend.")
        over = (self.spend_cap_usd is not None
                and self.state.spend_usd >= self.spend_cap_usd)
        note = (" Note: estimated spend is still over the cap, so it will "
                "latch again on the next call unless you raise it."
                if over else "")
        return f"Switched to live mode. Real reasoning re-enabled.{note}"

    def should_call_substrate(self) -> bool:
        """Asked before every substrate call."""
        return not self.enabled or self.state.is_live

    # ---- Outcomes ---------------------------------------------------------

    def record_success(self, *, usage: Optional[Dict[str, Any]] = None,
                       cost_usd: float = 0.0) -> None:
        """One successful call. Resets the consecutive-failure run — the
        counter is about runs, not lifetime totals."""
        self.state.consecutive_failures = 0
        self.state.calls += 1
        if usage:
            self.state.tokens_in += int(usage.get("input_tokens") or 0)
            self.state.tokens_out += int(usage.get("output_tokens") or 0)
        self.state.spend_usd = round(self.state.spend_usd + max(0.0, cost_usd), 8)
        self._check_spend()
        self.save()

    def record_failure(self, kind: FailureKind, detail: str = "") -> None:
        """One failed call, classified. Terminal latches now; transient
        latches after a run of them."""
        self.state.total_failures += 1
        self.state.consecutive_failures += 1

        if not self.enabled or not self.state.is_live:
            self.save()
            return

        if kind is FailureKind.TERMINAL:
            if self._switch(BUDGET, "terminal", detail):
                self.alerts.append(
                    f"Substrate failed permanently and will not recover on its own "
                    f"({detail[:160]}). Switched to budget mode — Analytics is now "
                    f"using deterministic fallbacks. Fix the cause, then say "
                    f"'switch to live mode'.")
            return

        if self.state.consecutive_failures >= self.failure_threshold:
            if self._switch(BUDGET, "transient",
                            f"{self.state.consecutive_failures} consecutive failures: {detail}"):
                self.alerts.append(
                    f"Substrate failed {self.state.consecutive_failures} times in a row "
                    f"({detail[:160]}). Switched to budget mode. Say 'switch to live "
                    f"mode' to try again.")
        self.save()

    def _check_spend(self) -> None:
        if self.spend_cap_usd is None or not self.enabled:
            return

        spent, cap = self.state.spend_usd, self.spend_cap_usd

        if spent >= cap:
            if self._switch(BUDGET, "spend_cap",
                            f"estimated spend ${spent:.4f} reached the ${cap:.2f} cap"):
                self.alerts.append(
                    f"Estimated spend reached the ${cap:.2f} cap (${spent:.4f} over "
                    f"{self.state.calls} calls). Switched to budget mode. Raise "
                    f"budget_mode.spend_cap_usd in the manifest, or say 'switch to "
                    f"live mode' to continue anyway. This figure is an estimate — "
                    f"check the vendor console for the real number.")
            return

        if not self.state.warned_at_fraction and spent >= cap * self.warn_at:
            self.state.warned_at_fraction = True
            self.alerts.append(
                f"Estimated spend is ${spent:.4f}, past {int(self.warn_at * 100)}% of "
                f"the ${cap:.2f} cap, over {self.state.calls} calls.")

    # ---- Reporting --------------------------------------------------------

    def drain_alerts(self) -> List[str]:
        alerts, self.alerts = self.alerts, []
        return alerts

    def reset_spend(self) -> str:
        self.state.spend_usd = 0.0
        self.state.tokens_in = self.state.tokens_out = self.state.calls = 0
        self.state.warned_at_fraction = False
        self.save()
        return "Spend counters reset. (The vendor's own billing is unaffected.)"

    def summary(self) -> str:
        s = self.state
        cap = (f" of ${self.spend_cap_usd:.2f} cap"
               if self.spend_cap_usd is not None else " (no cap)")
        lines = [
            f"mode          {s.mode}"
            + (f"  ({s.reason}: {s.detail})" if s.reason and s.mode == BUDGET else ""),
            f"calls         {s.calls}",
            f"tokens        {s.tokens_in:,} in / {s.tokens_out:,} out",
            f"est. spend    ${s.spend_usd:.4f}{cap}",
            f"failures      {s.consecutive_failures} in a row, {s.total_failures} total",
        ]
        if s.since:
            lines.append(f"since         {s.since}")
        return "\n".join(lines)


def from_manifest(manifest: Dict[str, Any], archive=None, **kwargs) -> BudgetManager:
    """Build a manager from the manifest's `budget_mode:` section."""
    config = (manifest.get("budget_mode") or {}) if manifest else {}
    cap = config.get("spend_cap_usd", DEFAULT_SPEND_CAP_USD)
    return BudgetManager(
        archive,
        enabled=bool(config.get("enabled", True)),
        initial_mode=str(config.get("initial_mode", LIVE)),
        failure_threshold=int(config.get("failure_threshold", DEFAULT_FAILURE_THRESHOLD)),
        spend_cap_usd=None if cap in (None, False) else float(cap),
        warn_at=float(config.get("warn_at_fraction", DEFAULT_WARN_AT)),
        **kwargs,
    )


__all__ = ["BudgetState", "BudgetManager", "from_manifest", "LIVE", "BUDGET"]
