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
from agents.impulse.agent import ImpulseMock
from agents.governance.agent import GovernanceMock
from agents.analytics.agent import AnalyticsMock
from agents.intent.agent import IntentMock
from agents.security.agent import SecurityMock
from agents.action.agent import ActionMock
from recovery.watchdog import Watchdog


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
    impulse: ImpulseMock
    governance: GovernanceMock
    analytics: AnalyticsMock
    intent: IntentMock
    security: SecurityMock
    action: ActionMock
    watchdog: Watchdog


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
        self.manifest = manifest
        return manifest

    # ---- §9.1 steps 2-7 -------------------------------------------------

    def bootstrap(self) -> Ecosystem:
        manifest = self.parse_manifest()
        phase = manifest["phase"]
        print(f"[recovery] parsed manifest '{self.manifest_path}' (phase {phase})")

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

        impulse_vectors = roles.get("impulse", {}).get("initial_vectors")
        impulse = ImpulseMock(bus, archive)
        if impulse_vectors:
            impulse.vectors.update(impulse_vectors)
            archive.set_drive_vectors(impulse.vectors)

        security = SecurityMock(bus)
        action = ActionMock(bus)

        # Step 4: cognitive hydration — register Intent nodes from manifest
        governance = GovernanceMock(bus)
        analytics = AnalyticsMock(bus)

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

        print(f"[recovery] provisioned 7 mocks + 1 real (Sensory), "
              f"Intent node '{nodes[0]['id']}' registered")

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
        )

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
