"""
Recovery — deterministic Infrastructure-as-Code bootstrapper, not one of
the 8 (§9). Zero LLM API dependency during deployment: everything here
runs even if every model endpoint is offline (true by construction in
Phase 0, since no cognitive-tier role makes a real call yet).

Implements the §9.1 bootstrap sequence:
  1. Manifest parsing
  2. Storage init
  3. Provision deterministic tier
  4. Cognitive hydration
  5. Bus binding
  6. Health check (synthetic BootCheck)
  7. System live

If any role fails its health check, Recovery logs the failure and stops
(deterministic, reproducible — fix and re-run, §9.1 step 6).

CLI usage:
    python -m recovery.bootstrap --manifest manifests/ecosystem-manifest.yaml
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from typing import Any, Dict, Optional

import yaml

from bus.pubsub import EmbeddedBus
from agents.archive.store import ArchiveStore
from agents.sensory.agent import Sensory
from agents.impulse.agent import (
    Impulse,
    IMPULSE_SEVERITY_CEILING,
    URGENCY_ELEVATED_THRESHOLD,
)
from agents.governance.agent import Governance
from agents.analytics.agent import AnalyticsMock
from agents.analytics.base import AnalyticsBase
from agents.analytics.live import AnalyticsAgent
from agents.intent.agent import IntentMock
from agents.security.agent import SecurityMock
from agents.action.agent import ActionMock
from budget.state import BudgetManager, from_manifest as budget_from_manifest
from budget import tiers as budget_tiers
from recovery.watchdog import Watchdog
from substrates.base import SubstrateError
from substrates.registry import resolve_role_substrate


class BootstrapError(RuntimeError):
    """Raised when a role fails its health check — stops deterministically."""


@dataclass
class Ecosystem:
    """Live handles to every provisioned component — what Recovery hands
    back once bootstrap succeeds. Not one of the 8 itself; just a
    convenience bag for the test harness / caller."""
    manifest: Dict[str, Any]
    bus: EmbeddedBus
    archive: ArchiveStore
    sensory: Sensory
    impulse: Impulse
    governance: Governance
    analytics: AnalyticsBase           # AnalyticsMock or AnalyticsAgent (§13.4)
    intent: IntentMock
    security: SecurityMock
    action: ActionMock
    watchdog: Watchdog
    budget: BudgetManager


class Recovery:
    """Sole deployment & bootstrap backbone (§9). Triggered by Governance
    (catastrophic failure), Watchdog (deadlock escalation), or direct
    user request — here, invoked directly by the CLI / test harness."""

    def __init__(self, manifest_path: str):
        self.manifest_path = manifest_path
        self.manifest: Dict[str, Any] = {}

    # ---- §9.1 step 1 --------------------------------------------------

    def parse_manifest(self) -> Dict[str, Any]:
        with open(self.manifest_path) as f:
            manifest = yaml.safe_load(f)
        required_top_level = {"version", "phase", "storage", "message_bus", "roles"}
        missing = required_top_level - manifest.keys()
        if missing:
            raise BootstrapError(f"Manifest missing required keys: {missing}")

        # Phase 0.2.2: budget_tier resolves to concrete roles.* config
        # before anything else reads it. "custom" (or absent) is a no-op —
        # see budget/tiers.py for what each named tier overwrites.
        try:
            manifest = budget_tiers.apply_tier(manifest)
        except budget_tiers.UnknownTier as exc:
            raise BootstrapError(str(exc)) from exc

        self.manifest = manifest
        return manifest

    # ---- §9.1 steps 2-7 -------------------------------------------------

    def bootstrap(self) -> Ecosystem:
        manifest = self.parse_manifest()
        phase = manifest["phase"]
        print(f"[recovery] parsed manifest '{self.manifest_path}' (phase {phase})")
        print(f"[recovery] budget tier: {budget_tiers.describe(manifest)}")

        # Step 2: storage init
        storage_root = manifest["storage"]["root"]
        archive = ArchiveStore(root=storage_root)
        print(f"[recovery] storage initialized at '{storage_root}' "
              f"(profile: {manifest['storage']['profile']})")

        # Step 5 groundwork: bus binding happens as each agent subscribes
        # in its own constructor below. Bus must exist first.
        bus = EmbeddedBus(archive=archive)

        # Step 3: provision deterministic tier (mock or real per manifest)
        roles = manifest["roles"]
        sensory = Sensory(bus)  # real from day one, §13.1 — mock flag ignored by design
        if roles["sensory"]["mock"] is not False:
            print("[recovery] WARNING: manifest marks sensory as mocked, "
                  "but Sensory is always real per §13.1 — ignoring.")

        impulse = self._provision_impulse(bus, archive, manifest)

        security = SecurityMock(bus)
        action = ActionMock(bus)

        # Budget mode's state is restored BEFORE any agent that could spend
        # is provisioned (Phase 0.2.1). A latch has to survive a restart:
        # coming back live after latching overnight would start spending
        # again before anyone noticed.
        budget = budget_from_manifest(manifest, archive)
        if budget.enabled:
            cap = ("no cap" if budget.spend_cap_usd is None
                   else f"${budget.spend_cap_usd:.2f} cap")
            print(f"[recovery] budget mode: {budget.state.mode} "
                  f"(${budget.state.spend_usd:.4f} spent, {cap})")

        # Step 4: cognitive hydration — load system instructions and resolve
        # substrate classes for any cognitive role running real, then
        # register Intent nodes from the manifest.
        governance = self._provision_governance(bus, manifest)
        analytics = self._provision_analytics(bus, manifest, archive, budget)

        intent_cfg = roles["intent"]
        nodes = intent_cfg.get("nodes", [])
        if not nodes:
            raise BootstrapError("Manifest roles.intent.nodes is empty — need at least one node")
        if len(nodes) > 1:
            print(f"[recovery] NOTE: manifest declares {len(nodes)} Intent nodes; "
                  f"Phase 0/1 mock only runs the first ('{nodes[0]['id']}'). "
                  f"Rotation across nodes arrives in Phase 2+ (§7.3).")
        batch_size = intent_cfg.get("rotation", {}).get("batch_size_events", 25)
        intent = IntentMock(bus, archive, node_id=nodes[0]["id"], batch_size=batch_size)

        real_roles = ["Sensory", "Impulse", "Governance"] + (
            ["Analytics"] if analytics.tier == "live" else [])
        print(f"[recovery] provisioned {8 - len(real_roles)} mocks + {len(real_roles)} real "
              f"({', '.join(real_roles)}), Intent node '{nodes[0]['id']}' registered")

        # Step 5: bus binding — done (constructors above subscribed to
        # their topics). Watchdog begins passively listening now.
        wd_cfg = manifest["timers"]["watchdog"]
        watchdog = Watchdog(
            bus, interval_x_sec=wd_cfg["interval_x_sec"],
            interval_y_sec=wd_cfg["interval_y_sec"],
            on_level2=lambda: sensory.inject_diagnostic_ping("SystemCheck"),
        )
        print("[recovery] bus bound; Watchdog listening "
              f"(Level 1 > {wd_cfg['interval_x_sec']}s, "
              f"Level 2 > {wd_cfg['interval_x_sec'] + wd_cfg['interval_y_sec']}s)")

        # Step 6: health check — synthetic BootCheck via Sensory
        self._health_check(bus, sensory)

        print("[recovery] system live.")
        return Ecosystem(
            manifest=manifest, bus=bus, archive=archive, sensory=sensory,
            impulse=impulse, governance=governance, analytics=analytics,
            intent=intent, security=security, action=action, watchdog=watchdog,
            budget=budget,
        )

    # ---- §9.1 step 3: deterministic tier -----------------------------------

    def _provision_impulse(self, bus: EmbeddedBus, archive: ArchiveStore,
                           manifest: Dict[str, Any]) -> Impulse:
        """Impulse is deterministic and always real as of Phase 0.3 — same
        posture as Sensory and Governance (§13.1/v0.34). `mock` has
        nothing left to select between, so it's warned-and-ignored rather
        than pretending it does something.

        `severity.ceiling` is READ but never OBEYED if it disagrees with
        IMPULSE_SEVERITY_CEILING — that cap is the v0.31/§3 safety
        invariant (drive-vector state alone can never manufacture a
        Critical escalation), not a tuning knob a manifest can loosen.
        A manifest trying to raise it gets a loud warning, not silent
        compliance — the same discipline Governance's verdict dispatch
        applies to an unreadable Security verdict (v0.34b): doubt, or a
        value that disagrees with the invariant, does not get the benefit
        of it."""
        role_config = manifest.get("roles", {}).get("impulse", {}) or {}

        if role_config.get("mock") is not False:
            print("[recovery] WARNING: manifest marks impulse as mocked, but "
                  "Impulse is deterministic and always real per Phase 0.3 — ignoring.")

        severity_cfg = role_config.get("severity", {}) or {}
        ceiling = severity_cfg.get("ceiling")
        if ceiling and ceiling != IMPULSE_SEVERITY_CEILING:
            print(f"[recovery] WARNING: roles.impulse.severity.ceiling is '{ceiling}' "
                  f"in the manifest, but this is a hard safety invariant (v0.31/§3), "
                  f"not manifest-configurable — Impulse's own assessment stays capped "
                  f"at '{IMPULSE_SEVERITY_CEILING}' regardless.")

        impulse = Impulse(
            bus, archive,
            initial_vectors=role_config.get("initial_vectors"),
            urgency_elevated_threshold=float(
                severity_cfg.get("urgency_elevated_threshold", URGENCY_ELEVATED_THRESHOLD)),
            drift_tau_sec=role_config.get("drift_tau_sec"),
        )
        print(f"[recovery] impulse: deterministic, real "
              f"(urgency_elevated_threshold={impulse.urgency_elevated_threshold}, "
              f"severity ceiling={IMPULSE_SEVERITY_CEILING} — hard invariant)")
        return impulse

    # ---- §9.1 step 4: cognitive hydration ---------------------------------

    def _provision_governance(self, bus: EmbeddedBus,
                              manifest: Dict[str, Any]) -> Governance:
        """Governance is always real, and always deterministic (v0.34).

        There is one implementation, so `roles.governance.mock` has
        nothing to select between — same situation as Sensory (§13.1),
        and handled the same way: warn and ignore rather than pretend the
        flag does something.

        No substrate is resolved and no credentials are checked, because
        Governance holds no substrate at all. Recovery's zero-LLM-
        dependency guarantee (§9) is now trivially true for this role
        rather than carefully arranged."""
        role_config = manifest.get("roles", {}).get("governance", {}) or {}

        if role_config.get("mock") is True:
            print("[recovery] WARNING: manifest marks governance as mocked, but "
                  "Governance is deterministic and always real per v0.34 — ignoring.")
        if role_config.get("substrate"):
            print(f"[recovery] NOTE: manifest assigns governance the substrate class "
                  f"'{role_config['substrate']}', which is unused — Governance makes "
                  f"no model calls (v0.34).")

        print("[recovery] governance: deterministic dispatcher (no substrate)")
        return Governance(bus)

    def _provision_analytics(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                             archive: ArchiveStore,
                             budget: Optional[BudgetManager] = None) -> AnalyticsBase:
        """Select Analytics' tier from `roles.analytics.mock` (§13.4).

        mock: true  -> AnalyticsMock, templated reasoning, zero LLM cost.
        mock: false -> AnalyticsAgent on the substrate class the role
                       declares (§10.2).

        Unlike Governance, this role genuinely needs a model, so the
        credential check matters again. It is still OFFLINE — no network
        call, no token spent — because Recovery must be able to construct
        and health-check the whole ecosystem with every endpoint
        unreachable (§9). What it must not do is declare 'system live' on
        an Analytics with no way to reach its substrate, so a
        misconfiguration stops the bootstrap deterministically (§9.1
        step 6): fix the manifest or the environment, re-run."""
        role_config = manifest.get("roles", {}).get("analytics", {}) or {}

        if role_config.get("mock", True):
            print("[recovery] analytics: MOCK tier (templated reasoning, zero LLM cost)")
            return AnalyticsMock(bus, archive)

        try:
            substrate = resolve_role_substrate(manifest, "analytics")
            substrate.validate_credentials()
        except SubstrateError as exc:
            raise BootstrapError(
                f"Analytics is declared real (roles.analytics.mock: false) but its "
                f"substrate is not usable: {exc}"
            ) from exc

        agent = AnalyticsAgent(
            bus, substrate, archive,
            system_instruction=role_config.get("system_instruction", ""),
            temperature=float(role_config.get("temperature", 0.2)),
            max_tokens=role_config.get("max_tokens"),
            strict=bool(role_config.get("strict", False)),
            budget=budget,
        )
        price = ("unpriced — budget mode cannot estimate spend"
                 if not substrate.has_prices
                 else f"${substrate.price_per_mtok_in}/${substrate.price_per_mtok_out} per Mtok")
        print(f"[recovery] analytics: LIVE tier on substrate "
              f"{substrate.describe()} ({price})")
        if not substrate.has_prices:
            print("[recovery] WARNING: no price_per_mtok declared for substrate "
                  f"'{substrate.substrate_class}'; the spend cap cannot protect you.")
        return agent

    def _health_check(self, bus: EmbeddedBus, sensory: Sensory) -> None:
        received: Dict[str, bool] = {"ok": False}

        def on_ack(envelope):
            if envelope.type == "BootCheckAck" and envelope.destination == "Recovery":
                received["ok"] = True

        bus.subscribe("system.diagnostic", on_ack)
        sensory.inject_diagnostic_ping("BootCheck")

        if not received["ok"]:
            raise BootstrapError(
                "Health check failed: no BootCheckAck received from Governance. "
                "Fix and re-run — deterministic and reproducible (§9.1 step 6)."
            )
        print("[recovery] health check passed: BootCheck round-trip to Governance confirmed")


def main(argv: Optional[list] = None) -> int:
    parser = argparse.ArgumentParser(description="ECI-CAS Recovery bootstrapper (§9)")
    parser.add_argument("--manifest", required=True, help="Path to ecosystem-manifest.yaml")
    args = parser.parse_args(argv)

    try:
        Recovery(args.manifest).bootstrap()
    except BootstrapError as e:
        print(f"[recovery] BOOTSTRAP FAILED: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
