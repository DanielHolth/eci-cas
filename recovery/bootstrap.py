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
from pathlib import Path
from typing import Any, Dict, Optional

import yaml

from bus.pubsub import EmbeddedBus
from agents.archive.agent import ArchiveAgent
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
from agents.intent.base import DEFAULT_CONTEXT_EVENTS, IntentBase
from agents.intent.live import IntentAgent
from agents.archive_lookup.agent import ArchiveLookupMock
from agents.archive_lookup.live import ArchiveLookupAgent
from agents.archive_lookup import contract as lookup_contract
from agents.archive_lookup.base import ArchiveLookupBase
from agents.consolidator.agent import ConsolidatorMock
from agents.consolidator.base import ConsolidatorBase
from agents.consolidator.live import ConsolidatorAgent
from agents.reflection.agent import ReflectionMock
from agents.reflection.base import ReflectionBase
from agents.reflection.live import ReflectionAgent
from agents.security.agent import SecurityAgent, SecurityMock
from agents.security.rules import RuleSet, RulesError
from agents.action.agent import ActionAgent, ActionMock
from agents.action import sinks as action_sinks
from budget.state import BudgetManager, from_manifest as budget_from_manifest
from budget import tiers as budget_tiers
from recovery.watchdog import Watchdog
from substrates.base import SubstrateError
from substrates.registry import resolve_role_substrate, resolve_substrate


class BootstrapError(RuntimeError):
    """Raised when a role fails its health check — stops deterministically."""


@dataclass
class Ecosystem:
    """Live handles to every provisioned component — what Recovery hands
    back once bootstrap succeeds. Not one of the 8 itself; just a
    convenience bag for the test harness / caller."""
    manifest: Dict[str, Any]
    bus: EmbeddedBus
    archive: ArchiveStore               # the store — §5.8's two endpoints
    archive_agent: Any                  # ArchiveAgent, the bus door (Phase 0.6)
    sensory: Sensory
    impulse: Impulse
    governance: Governance
    analytics: AnalyticsBase           # AnalyticsMock or AnalyticsAgent (§13.4)
    intent: IntentBase                 # IntentMock or IntentAgent (§13.4, Phase 0.4)
    consolidator: ConsolidatorBase     # ConsolidatorMock or ConsolidatorAgent (v0.35f)
    reflection: ReflectionBase         # ReflectionMock or ReflectionAgent (dispatch #4)
    personality: ArchiveLookupBase     # archive-grounded lookup, identity store (v0.35b)
    security: Any                      # SecurityMock or SecurityAgent (Phase 0.6)
    action: Any                        # ActionMock or ActionAgent (Phase 0.6)
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

        # Phase 0.8: structured Parquet store for knowledge swarm retrieval.
        structured_store = None
        if manifest["storage"]["profile"] == "hybrid-parquet":
            from agents.archive.structured_store import StructuredStore
            structured_store = StructuredStore(root=storage_root)
            print(f"[recovery] structured store: {structured_store.count('knowledge')} "
                  f"knowledge, {structured_store.count('identity')} personality records")

        # Step 5 groundwork: bus binding happens as each agent subscribes
        # in its own constructor below. Bus must exist first.
        bus = EmbeddedBus(archive=archive)

        # Phase 0.6: Archive gains a bus door beside its two endpoints.
        # The store is passed to roles exactly as before — this does not
        # move any existing caller onto messaging (see the agent's header
        # for why Consolidator in particular stays direct). What it adds
        # is a write path for agents that have no business holding the
        # store, and a receipt so that writes are observable at all.
        archive_agent = self._provision_archive(bus, manifest, archive)

        # Step 3: provision deterministic tier (mock or real per manifest)
        roles = manifest["roles"]
        sensory = Sensory(bus)  # real from day one, §13.1 — mock flag ignored by design
        if roles["sensory"]["mock"] is not False:
            print("[recovery] WARNING: manifest marks sensory as mocked, "
                  "but Sensory is always real per §13.1 — ignoring.")

        impulse = self._provision_impulse(bus, archive, manifest)

        security = self._provision_security(bus, manifest)
        action = self._provision_action(bus, manifest)

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
        analytics = self._provision_analytics(bus, manifest, archive, budget,
                                                structured_store=structured_store)

        personality = self._provision_lookup(bus, manifest, archive,
                                             "Personality", budget,
                                             structured_store=structured_store)

        # Consolidator subscribes to "events.consolidator" like any other
        # role, but nothing publishes to it here — it's fed by Governance's
        # BUNDLE fork (agents/governance/agent.py's emit()), provisioned
        # further down. Constructed here anyway, alongside the other
        # cognitive roles, since order matters only for Governance below.
        consolidator = self._provision_consolidator(bus, manifest, archive,
                                                    budget,
                                                    structured_store=structured_store)

        # Reflection (dispatch #4) is fed by Governance's `_conclude()` fork
        # on "events.reflection" — same "subscribes, nothing publishes here"
        # shape as Consolidator above. It also gets a direct `sensory`
        # reference (read-only: it only ever calls ingest()) so an Idea
        # outcome can re-enter the pipeline the same way a consolidation
        # doodle click does.
        reflection = self._provision_reflection(bus, manifest, budget,
                                                structured_store=structured_store,
                                                sensory=sensory)
        intent = self._provision_intent(bus, manifest, archive, budget)

        # Governance is provisioned LAST because it is the only role that
        # holds a reference to another: Impulse (to READ an expression from
        # when an exchange is blocked, v0.35e). Nothing flows back the
        # other way — the frustration nudge that answers a block goes over
        # the control plane rather than through a reference.
        governance = self._provision_governance(bus, manifest,
                                                impulse=impulse,
                                                structured_store=structured_store)

        real_roles = ["Sensory", "Impulse", "Governance"] + (
            ["Archive"] if archive_agent is not None else []) + (
            ["Security"] if getattr(security, "tier", "mock") == "live" else []) + (
            ["Action"] if getattr(action, "tier", "mock") == "live" else []) + (
            ["Analytics"] if analytics.tier == "live" else []) + (
            ["Intent"] if intent.tier == "live" else []) + (
            ["Consolidator"] if consolidator.tier == "live" else []) + (
            ["Reflection"] if reflection.tier == "live" else []) + (
            ["Personality"] if personality.tier == "live" else [])
        print(f"[recovery] provisioned {12 - len(real_roles)} mocks + {len(real_roles)} real "
              f"({', '.join(real_roles)})")

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
            manifest=manifest, bus=bus, archive=archive,
            archive_agent=archive_agent, sensory=sensory,
            impulse=impulse, governance=governance, analytics=analytics,
            intent=intent, consolidator=consolidator, reflection=reflection,
            personality=personality,
            security=security, action=action,
            watchdog=watchdog, budget=budget,
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

    def _provision_archive(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                           archive: ArchiveStore):
        """Provision Archive's bus door (Phase 0.6).

        `roles.archive.mock` selects whether the door exists, not whether
        memory works: the store is constructed in step 2 either way, and
        every direct caller is unaffected. Mocked here therefore means
        "no bus door", which is precisely the pre-0.6 state — worth being
        able to return to in one manifest line while this is new."""
        role_config = manifest.get("roles", {}).get("archive", {}) or {}

        if role_config.get("mock", True):
            print("[recovery] archive: store only, no bus door "
                  "(roles.archive.mock: true) — writes must hold the store.")
            return None

        agent = ArchiveAgent(bus, archive)
        print(f"[recovery] archive: LIVE — store at '{archive.root}' plus a "
              f"bus door on '{agent.topic}' (receipts on system.control)")
        return agent

    def _provision_security(self, bus: EmbeddedBus,
                            manifest: Dict[str, Any]):
        """Select Security's tier from `roles.security.mock` (Phase 0.6).

        Deterministic either way — there is no substrate to resolve and no
        credential to check, so this is the one role whose "real" tier
        costs nothing to run. What it needs instead is a rules file, and
        that is treated exactly as a credential is elsewhere: missing or
        unreadable stops the bootstrap (§9.1 step 6) rather than
        degrading.

        The degradation this refuses to do is the whole point. Security's
        only failure mode that matters is answering green when it
        shouldn't, and that is indistinguishable from the mock — so a
        Security that cannot load its rules must not boot at all."""
        role_config = manifest.get("roles", {}).get("security", {}) or {}

        if role_config.get("mock") is not False:
            print("[recovery] security: MOCK tier — every action clears green "
                  "(§13.1). Nothing is being enforced.")
            return SecurityMock(bus)

        rules_path = role_config.get("rules")
        if not rules_path:
            raise BootstrapError(
                "roles.security.mock is false but roles.security.rules names "
                "no rules file. Security cannot run real without rules.")

        resolved = self._resolve_config_path(str(rules_path))
        try:
            rules = RuleSet.load(resolved)
        except RulesError as exc:
            raise BootstrapError(f"security rules: {exc}") from exc

        agent = SecurityAgent(bus, rules)
        print(f"[recovery] security: LIVE tier — {len(rules)} rules from "
              f"'{resolved}' (v{rules.version or 'unversioned'}), "
              f"deterministic, no substrate")
        return agent

    def _provision_action(self, bus: EmbeddedBus, manifest: Dict[str, Any]):
        """Select Action's tier from `roles.action.mock` (Phase 0.6).

        Unlike Security, a misconfigured Action is NOT a bootstrap
        failure in only one direction: an unknown sink type stops the
        boot (a typo must not silently become silence), but an empty sink
        list is allowed and says so loudly. The difference is what the
        two failures cost. A Security that can't enforce looks identical
        to one that can; an Action that emits nowhere is discovered by
        the first person who says hello and hears nothing back."""
        role_config = manifest.get("roles", {}).get("action", {}) or {}

        if role_config.get("mock") is not False:
            print("[recovery] action: MOCK tier — executed actions are "
                  "recorded, not emitted anywhere (§13.1).")
            return ActionMock(bus)

        # A relative file-sink path is resolved against the storage root,
        # not the working directory. The transcript of what this system
        # actually said is archive-tier data; scattering copies of it
        # wherever the process happened to start is how two deployments
        # end up disagreeing about what was said.
        configs = role_config.get("sinks")
        storage_root = Path(manifest.get("storage", {}).get("root", "data/archive"))
        if isinstance(configs, list):
            resolved = []
            for entry in configs:
                if (isinstance(entry, dict)
                        and str(entry.get("type", "")).lower() == "file"
                        and entry.get("path")
                        and not Path(str(entry["path"])).is_absolute()):
                    entry = {**entry,
                             "path": str(storage_root / Path(str(entry["path"])).name)}
                resolved.append(entry)
            configs = resolved

        try:
            sinks = action_sinks.build_sinks(configs)
        except ValueError as exc:
            raise BootstrapError(f"action sinks: {exc}") from exc

        if not sinks:
            # Legal, and loud. Somebody may genuinely want a headless
            # deployment; nobody wants an accidentally mute one.
            sinks = [action_sinks.NullSink()]
            print("[recovery] WARNING: action is real but roles.action.sinks "
                  "is empty — nothing this system says will reach anyone. "
                  "Falling back to a null sink so the pipeline still runs.")

        agent = ActionAgent(bus, sinks)
        print(f"[recovery] action: LIVE tier — emitting through "
              f"{len(sinks)} sink(s): {', '.join(s.name for s in sinks)}")
        return agent

    def _resolve_config_path(self, path: str) -> Path:
        """Find a config file named relative to something sensible.

        Tried in order: as given (absolute or CWD-relative), next to the
        manifest, in a `config/` directory beside the manifest's parent,
        in the repo root's `config/`, and finally in the config directory
        that ships with this source tree. A bare filename in a manifest
        should not require the operator to know which directory the
        process happened to start in — nor should copying a manifest
        somewhere else (which every test fixture does) silently strand
        the shipped default rule set."""
        candidate = Path(path)
        if candidate.is_absolute():
            return candidate

        manifest_dir = Path(self.manifest_path).resolve().parent
        repo_root = manifest_dir.parent
        shipped = Path(__file__).resolve().parent.parent / "config"
        for base in (Path.cwd(), manifest_dir, manifest_dir / "config",
                     repo_root, repo_root / "config", shipped):
            found = base / candidate
            if found.exists():
                return found
        # Nothing found: hand back the most likely intended location so
        # the error names a real path rather than a bare filename.
        return shipped / candidate

    # ---- §9.1 step 4: cognitive hydration ---------------------------------

    def _provision_governance(self, bus: EmbeddedBus,
                              manifest: Dict[str, Any], *,
                              impulse=None,
                              structured_store=None) -> Governance:
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

        budget_tier = manifest.get("budget_tier", "default")
        governance = Governance(bus, impulse=impulse,
                               structured_store=structured_store,
                               budget_tier=budget_tier)
        print(f"[recovery] governance: deterministic dispatcher (no substrate), "
              f"bundling {len(governance.buffer.expected)} parallel answers per "
              f"event ({', '.join(sorted(governance.buffer.expected))})")
        return governance

    def _provision_analytics(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                             archive: ArchiveStore,
                             budget: Optional[BudgetManager] = None,
                             structured_store=None) -> AnalyticsBase:
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
            structured_store=structured_store,
        )
        print(f"[recovery] analytics: LIVE tier on substrate "
              f"{substrate.describe()} ({self._price_note(substrate)})")
        self._warn_unpriced(substrate)
        return agent

    def _provision_intent(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                          archive: ArchiveStore,
                          budget: Optional[BudgetManager]) -> IntentBase:
        """Select Intent's tier from `roles.intent.mock` (§13.4).

        Same shape as `_provision_analytics` now — mock: true is templated
        and free, mock: false resolves a real substrate and stops the
        bootstrap deterministically on a credential the role can't reach
        (§9.1 step 6).

        v0.35f removed what used to make this method special. Node
        selection is gone with the fleet/rotation model: Intent declares
        one flat `substrate`, exactly like Analytics, and there is no
        nodes[0] to pick. The consolidation substrate is gone too —
        consolidation is its own role now (see _provision_consolidator).
        A manifest still carrying the old shape is told so rather than
        silently ignored, because a stale `nodes:` list looks like it is
        doing something."""
        role_config = manifest.get("roles", {}).get("intent", {}) or {}

        if role_config.get("nodes"):
            print("[recovery] NOTE: roles.intent.nodes is set, but the Intent "
                  "fleet/rotation model was removed in v0.35f (§7 superseded) "
                  "— Intent is always active and declares one flat "
                  "'substrate'. Ignoring 'nodes'.")
        if role_config.get("consolidation_substrate"):
            print("[recovery] NOTE: roles.intent.consolidation_substrate is set, "
                  "but consolidation is its own role as of v0.35f — configure it "
                  "under roles.consolidator.substrate. Ignoring.")

        context_events = int(role_config.get("context_events", DEFAULT_CONTEXT_EVENTS))

        if role_config.get("mock", True):
            print(f"[recovery] intent: MOCK tier (templated voicing, zero LLM cost), "
                  f"conversation window {context_events} events")
            return IntentMock(bus, archive, context_events=context_events)

        substrate_class = role_config.get("substrate")
        if not substrate_class:
            raise BootstrapError(
                "Intent is declared real (roles.intent.mock: false) but declares "
                "no 'substrate' class")
        try:
            substrate = resolve_substrate(manifest, substrate_class)
            substrate.validate_credentials()
        except SubstrateError as exc:
            raise BootstrapError(
                f"Intent is declared real (roles.intent.mock: false) but its "
                f"substrate is not usable: {exc}"
            ) from exc

        agent = IntentAgent(
            bus, substrate, archive,
            context_events=context_events,
            system_instruction=role_config.get("system_instruction", ""),
            temperature=float(role_config.get("temperature", 0.7)),
            max_tokens=role_config.get("max_tokens"),
            strict=bool(role_config.get("strict", False)),
            budget=budget,
        )
        print(f"[recovery] intent: LIVE tier on substrate {substrate.describe()} "
              f"({self._price_note(substrate)}), conversation window "
              f"{context_events} events")
        self._warn_unpriced(substrate)
        return agent

    def _provision_consolidator(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                                archive: ArchiveStore,
                                budget: Optional[BudgetManager],
                                structured_store=None) -> ConsolidatorBase:
        """Select Consolidator's tier from `roles.consolidator.mock` (v0.9).

        One thing here differs from every other cognitive role, and it is
        deliberate: an unusable substrate is a WARNING, not a bootstrap
        stop. Consolidation gates nothing — a degraded pass just means one
        event's facts aren't written, which is the same "an outage changes
        quality, not behaviour" posture as every other degraded path here.
        Blocking the whole live pipeline over a substrate only the memory
        writer depends on would be the wrong trade.

        Phase 0.9: Consolidator dropped its batch buffer and Impulse
        coupling entirely — it is now a fifth member of Sensory's
        per-event fan-out (`agents/sensory/agent.py`), provisioned the
        same way as Personality/Knowledge: bus-subscribed at construction,
        no batch/synchronous/impulse params."""
        role_config = manifest.get("roles", {}).get("consolidator", {}) or {}

        if role_config.get("mock", True):
            print("[recovery] consolidator: MOCK tier (templated fact "
                  "extraction, zero LLM cost)")
            return ConsolidatorMock(bus, archive)

        substrate_class = role_config.get("substrate")
        substrate = None
        if not substrate_class:
            print("[recovery] WARNING: consolidator is declared real but names no "
                  "'substrate' class; falling back to the MOCK tier. Long-term "
                  "memory will not be written until this is fixed.")
        else:
            try:
                substrate = resolve_substrate(manifest, substrate_class)
                substrate.validate_credentials()
            except SubstrateError as exc:
                print(f"[recovery] WARNING: consolidator substrate "
                      f"'{substrate_class}' is not usable ({exc}); falling back "
                      f"to the MOCK tier. Facts will not be written until this "
                      f"is fixed.")
                substrate = None

        if substrate is None:
            return ConsolidatorMock(bus, archive)

        agent = ConsolidatorAgent(
            bus, substrate, archive,
            system_instruction=role_config.get("system_instruction", ""),
            temperature=float(role_config.get("temperature", 0.3)),
            max_tokens=role_config.get("max_tokens"),
            strict=bool(role_config.get("strict", False)),
            budget=budget,
            structured_store=structured_store,
        )
        print(f"[recovery] consolidator: LIVE tier on substrate "
              f"{substrate.describe()} ({self._price_note(substrate)})")
        self._warn_unpriced(substrate)
        return agent

    def _provision_reflection(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                              budget: Optional[BudgetManager], *,
                              structured_store=None, sensory=None) -> ReflectionBase:
        """Select Reflection's tier from `roles.reflection.mock` (dispatch
        #4, 2026-08-29). Same posture as Consolidator: an unusable
        substrate is a WARNING, not a bootstrap stop — Reflection gates
        nothing, and a degraded pass just means one batch's pattern (if
        any) goes unremembered."""
        role_config = manifest.get("roles", {}).get("reflection", {}) or {}

        if role_config.get("mock", True):
            print("[recovery] reflection: MOCK tier (always silent, zero LLM cost)")
            return ReflectionMock(bus, structured_store=structured_store, sensory=sensory,
                                  batch_size=int(role_config.get("batch_size", 5)))

        substrate_class = role_config.get("substrate")
        substrate = None
        if not substrate_class:
            print("[recovery] WARNING: reflection is declared real but names no "
                  "'substrate' class; falling back to the MOCK tier.")
        else:
            try:
                substrate = resolve_substrate(manifest, substrate_class)
                substrate.validate_credentials()
            except SubstrateError as exc:
                print(f"[recovery] WARNING: reflection substrate "
                      f"'{substrate_class}' is not usable ({exc}); falling back "
                      f"to the MOCK tier.")
                substrate = None

        if substrate is None:
            return ReflectionMock(bus, structured_store=structured_store, sensory=sensory,
                                  batch_size=int(role_config.get("batch_size", 5)))

        agent = ReflectionAgent(
            bus, substrate,
            structured_store=structured_store, sensory=sensory,
            batch_size=int(role_config.get("batch_size", 5)),
            system_instruction=role_config.get("system_instruction", ""),
            temperature=float(role_config.get("temperature", 0.3)),
            max_tokens=role_config.get("max_tokens"),
            strict=bool(role_config.get("strict", False)),
            budget=budget,
        )
        print(f"[recovery] reflection: LIVE tier on substrate "
              f"{substrate.describe()} ({self._price_note(substrate)}), "
              f"batch_size={agent.batch_size}")
        self._warn_unpriced(substrate)
        return agent

    def _provision_lookup(self, bus: EmbeddedBus, manifest: Dict[str, Any],
                          archive: ArchiveStore, role: str,
                          budget: Optional[BudgetManager] = None,
                          structured_store=None) -> ArchiveLookupBase:
        """Provision one member of the archive-lookup family (v0.35b).

        Called once per role, with no per-role branching anywhere in this
        method — that is the whole point of the family being one class
        with two configurations. A third member is an entry in
        agents/archive_lookup/base.py's ROLE_STORES plus a manifest block,
        and this method already handles it.

        Phase 0.6 gave the family a live tier, so `mock: false` now means
        what it says instead of being reported and ignored. The
        credential check is the same offline one every cognitive role
        gets: constructible with every endpoint unreachable (§9), but a
        role declared real with no way to reach its substrate stops the
        bootstrap rather than quietly running mocked."""
        key = role.lower()
        role_config = manifest.get("roles", {}).get(key, {}) or {}
        query_limit = int(role_config.get("query_limit",
                                          lookup_contract.DEFAULT_QUERY_LIMIT))
        brief = role_config.get("brief", "")

        if role_config.get("mock", True):
            agent = ArchiveLookupMock(
                bus, archive, role=role, brief=brief, query_limit=query_limit,
                structured_store=structured_store)
            print(f"[recovery] {key}: MOCK tier (read-only lookup over the "
                  f"'{agent.store_kind}' store, zero LLM cost)")
            return agent

        try:
            substrate = resolve_role_substrate(manifest, key)
            substrate.validate_credentials()
        except SubstrateError as exc:
            raise BootstrapError(
                f"{role} is declared real (roles.{key}.mock: false) but its "
                f"substrate is not usable: {exc}"
            ) from exc

        agent = ArchiveLookupAgent(
            bus, archive, substrate, role=role, brief=brief,
            query_limit=query_limit,
            system_instruction=role_config.get("system_instruction", ""),
            temperature=float(role_config.get("temperature", 0.2)),
            max_tokens=role_config.get("max_tokens"),
            strict=bool(role_config.get("strict", False)),
            budget=budget,
            structured_store=structured_store,
        )
        print(f"[recovery] {key}: LIVE tier on substrate "
              f"{substrate.describe()} — read-only lookup over the "
              f"'{agent.store_kind}' store ({self._price_note(substrate)})")
        self._warn_unpriced(substrate)
        return agent

    # ---- Shared reporting helpers ------------------------------------------

    @staticmethod
    def _price_note(substrate) -> str:
        return ("unpriced — budget mode cannot estimate spend"
                if not substrate.has_prices
                else f"${substrate.price_per_mtok_in}/"
                     f"${substrate.price_per_mtok_out} per Mtok")

    @staticmethod
    def _warn_unpriced(substrate) -> None:
        if not substrate.has_prices:
            print("[recovery] WARNING: no price_per_mtok declared for substrate "
                  f"'{substrate.substrate_class}'; the spend cap cannot protect you.")

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
