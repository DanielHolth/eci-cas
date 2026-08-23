"""
Substrate preflight — check the ecosystem can reach its models before
asking Recovery to bootstrap it (§9.1, §10.2).

Recovery already refuses to declare "system live" on a role whose
substrate is unusable, so nothing here is load-bearing. What this adds is
a faster, narrower answer to one question: *is my key set up right?* —
without spinning up a bus, an archive, or eight agents to find out.

Two modes:

    python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml
        OFFLINE. Resolves every substrate class a real role depends on and
        runs its credential check — no network call, no token spent. This
        is the same check Recovery runs at boot.

    python -m tools.preflight --manifest ... --live
        Adds one minimal completion per distinct (provider, model), and
        reports latency and token usage. Costs a few tokens. Use it once
        after setting a key, to confirm the whole path works end to end.

Exit code is 0 when everything a real role needs is usable, 1 otherwise —
so it drops straight into CI or a shell guard.
"""
from __future__ import annotations

import argparse
import sys
import time
from typing import Any, Dict, List, Optional, Tuple

import yaml

from budget import tiers as budget_tiers
from substrates.base import CompletionError, SubstrateError
from substrates.registry import resolve_substrate

#: Roles that make model calls when running real. Deterministic roles are
#: skipped even if the manifest leaves a stale `substrate` key on them —
#: Governance in particular carries one for historical reasons and never
#: uses it (v0.34).
COGNITIVE_ROLES = ("analytics", "intent")

OK = "  ok   "
SKIP = " skip  "
FAIL = " FAIL  "


def _iter_required_substrates(
    manifest: Dict[str, Any]
) -> List[Tuple[str, Optional[str], bool]]:
    """Yield (role, substrate_class, is_required) for every cognitive role.

    A mocked role with a substrate still declared has it resolved and
    reported — informational lookahead (e.g. Intent's future live
    substrate, checked today while it's still mocked); a failure there
    doesn't fail preflight overall.

    substrate_class is None when a mocked role has no substrate at all —
    Minimal's Analytics, specifically (budget/tiers.py's apply_tier()
    clears it rather than leaving a stale leftover value sitting there).
    That's reported as plainly mocked, not resolved against anything."""
    out: List[Tuple[str, Optional[str], bool]] = []
    roles = manifest.get("roles") or {}

    for role in COGNITIVE_ROLES:
        config = roles.get(role) or {}
        if not config:
            continue
        required = config.get("mock", True) is False

        if role == "intent":
            # Intent declares its substrate per node (§7.5), not on the role.
            for node in config.get("nodes", []) or []:
                if node.get("substrate"):
                    out.append((f"{role}[{node.get('id', '?')}]",
                                node["substrate"], required))
            continue

        if config.get("substrate"):
            out.append((role, config["substrate"], required))
        elif not required:
            out.append((role, None, required))

    return out


def _probe(substrate, timeout_note: str = "") -> Tuple[bool, str]:
    """One minimal completion. Deliberately tiny: this is a reachability
    and auth check, not a capability test."""
    started = time.perf_counter()
    try:
        response = substrate.complete(
            system="Reply with exactly one word.",
            user="Say: ok",
            temperature=0.0,
            max_tokens=16,
        )
    except CompletionError as exc:
        return False, f"{type(exc).__name__}: {str(exc)[:180]}"

    latency_ms = round((time.perf_counter() - started) * 1000)
    usage = response.usage or {}
    tokens = ""
    if usage.get("input_tokens") is not None:
        tokens = (f", {usage.get('input_tokens')} in / "
                  f"{usage.get('output_tokens')} out")
    text = " ".join(str(response.text).split())[:40]
    return True, f"{latency_ms} ms{tokens} — served by {response.model!r}, said {text!r}"


def main(argv: Optional[list] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Check the ecosystem's substrates before bootstrapping (§10.2)")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--live", action="store_true",
                        help="also make one minimal call per model (costs tokens)")
    args = parser.parse_args(argv)

    with open(args.manifest) as f:
        manifest = yaml.safe_load(f)

    # Bootstrap resolves budget_tier before it looks at roles.* (Recovery.
    # parse_manifest) — preflight has to do the same, or it reports on the
    # manifest's literal, pre-tier roles.analytics block instead of what
    # will actually run. "custom"/"default" are no-ops (budget/tiers.py);
    # anything else overwrites roles.analytics.* and roles.intent.* here,
    # same as at boot.
    try:
        manifest = budget_tiers.apply_tier(manifest)
    except budget_tiers.UnknownTier as exc:
        print(f"Preflight — {args.manifest}: {exc}", file=sys.stderr)
        return 1

    required = _iter_required_substrates(manifest)
    if not required:
        print("No cognitive role declares a substrate. Nothing to check.")
        return 0

    print(f"Preflight — {args.manifest} (phase {manifest.get('phase')}, "
          f"budget tier: {budget_tiers.describe(manifest)})")
    print(f"{'':7}{'role':<16}{'substrate':<16}{'resolved to'}")
    print("-" * 72)

    failures = 0
    probed: Dict[Tuple[str, str], Tuple[bool, str]] = {}

    for role, substrate_class, is_required in required:
        if substrate_class is None:
            # Mocked with no substrate assigned at all (Minimal's
            # Analytics) — nothing to resolve or check. Say so plainly
            # rather than printing a leftover value that implies a
            # substrate identity the tier never claimed.
            print(f"{OK}{role:<16}{'mocked':<16}(no substrate — role is fully mocked)")
            continue

        try:
            substrate = resolve_substrate(manifest, substrate_class)
        except SubstrateError as exc:
            print(f"{FAIL}{role:<16}{substrate_class:<16}{exc}")
            failures += is_required
            continue

        target = f"{substrate.provider_name}:{substrate.model}"

        try:
            substrate.validate_credentials()
        except SubstrateError as exc:
            marker = FAIL if is_required else SKIP
            note = "" if is_required else " (role is mocked — informational)"
            print(f"{marker}{role:<16}{substrate_class:<16}{target}")
            print(f"{'':7}{'':<32}{exc}{note}")
            failures += is_required
            continue

        print(f"{OK}{role:<16}{substrate_class:<16}{target}"
              + ("" if is_required else "   (role is mocked)"))

        if args.live and is_required:
            key = (substrate.provider_name, substrate.model)
            if key not in probed:
                probed[key] = _probe(substrate)
            succeeded, detail = probed[key]
            print(f"{OK if succeeded else FAIL}{'':<16}{'live call':<16}{detail}")
            if not succeeded:
                failures += 1

            # A manifest knob this SDK build silently ignores is drift,
            # and drift is a bug (§1.1). Say so rather than let the
            # manifest claim something that isn't happening.
            unsupported = getattr(substrate.provider, "unsupported_params", set())
            if unsupported:
                print(f"{SKIP}{'':<16}{'ignored':<16}"
                      f"{', '.join(sorted(unsupported))} — this SDK build does not "
                      f"accept it; the manifest value has no effect")

    print("-" * 72)
    if failures:
        print(f"{failures} substrate(s) a real role depends on are not usable.")
        print("Fix the manifest or the environment, then re-run — Recovery would "
              "stop at the same place (§9.1 step 6).")
        return 1

    if args.live:
        print("All required substrates reachable and answering.")
    else:
        print("All required substrates are configured. Add --live to prove "
              "they answer (costs a few tokens).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
