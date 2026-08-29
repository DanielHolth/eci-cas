"""
Budget tiers — model allocation presets.

budget_mode (runtime): a latch that trips on failure and falls back to
deterministic per-task fallbacks. budget_tier (design-time): which
vendor/model backs each cognitive role — Minimal/Budget/Default/Super.
They compose: any tier can still latch into budget mode on a failure.

A tier names substrate CLASSES (resolved in the manifest's substrates
table). Four cognitive roles have live implementations: Analytics,
Intent, Consolidator, and Reflection. Personality and Knowledge use the
lookup family tier. Minimal mocks Analytics and lookups; all tiers run
Intent, Consolidator, and Reflection live.
"""
from __future__ import annotations

from typing import Any, Dict, List

MINIMAL = "minimal"
BUDGET = "budget"
DEFAULT = "default"
SUPER = "super"
CUSTOM = "custom"          # not a tier — means "ignore this module"

TIER_NAMES = (MINIMAL, BUDGET, DEFAULT, SUPER)

#: Substrate class names the tiers point at. These must exist in the
#: manifest's `substrates:` table — a tier NAMES a class, it doesn't
#: define one (§10.2: only the manifest's substrates table knows the
#: vendor).
#:
#: Dispatch #5 (2026-08-29) replaced local/low/medium/high with two axes:
#: fast-* for the live/gated path (Analytics, Personality, Knowledge,
#: Intent), where time-to-first-token is the budget, and slow-* for the
#: async path (Consolidator, Reflection), which can spend a slower TTFT on
#: a smarter answer because nothing downstream is waiting on it. Purpose
#: of each class, not its literal current vendor — that lives in the
#: manifest's substrates table and can move without this module changing
#: (§10.2). See the manifest for current vendor/model per class.
FAST_LOCAL_CLASS = "fast-local"            # self-hosted, keyless; live path
SLOW_LOCAL_CLASS = "slow-local"            # self-hosted, keyless; async path
FAST_LOW_CLASS = "fast-low"                # cheap hosted model; live path
SLOW_LOW_CLASS = "slow-low"                # cheap hosted model; async path
FAST_MEDIUM_CLASS = "fast-medium"          # Default tier's Intent slot
SLOW_MEDIUM_CLASS = "slow-medium"          # Default tier's Consolidator/Reflection slot
FAST_HIGH_CLASS = "fast-high"              # Super tier's Intent slot
SLOW_HIGH_CLASS = "slow-high"              # Super tier's Consolidator/Reflection slot

#: How many concluded events of conversation Intent carries on a live
#: call (v0.35c, `roles.intent.context_events`). Tier-scaled on Daniel's
#: call (2026-08-24): this rides on EVERY live call, so it is charged
#: against the same flat-cost claim (§1) as the persona itself, and a
#: tier that exists to cap spend should cap this too. Whole events, never
#: a partial one — see agents/intent/base.py's recent_conversation().
CONTEXT_EVENTS = {MINIMAL: 1, BUDGET: 5, DEFAULT: 10, SUPER: 15}

#: role -> config, per tier. `analytics.substrate: None` means "leave
#: whatever's declared" — irrelevant while analytics.mock is True, and
#: there's no reason to clobber an operator's substrate choice for a role
#: that isn't calling it.
TIER_PRESETS: Dict[str, Dict[str, Any]] = {
    MINIMAL: {
        "analytics": {"mock": True, "substrate": None},
        "lookup": {"mock": True, "substrate": None},
        "intent_live": FAST_LOCAL_CLASS,
        "consolidation": SLOW_LOCAL_CLASS,
        "reflection": SLOW_LOCAL_CLASS,
        "context_events": CONTEXT_EVENTS[MINIMAL],
    },
    BUDGET: {
        "analytics": {"mock": False, "substrate": FAST_LOW_CLASS},
        "lookup": {"mock": False, "substrate": FAST_LOW_CLASS},
        "intent_live": FAST_LOW_CLASS,
        "consolidation": SLOW_LOW_CLASS,
        "reflection": SLOW_LOW_CLASS,
        "context_events": CONTEXT_EVENTS[BUDGET],
    },
    DEFAULT: {
        "analytics": {"mock": False, "substrate": FAST_LOW_CLASS},
        "lookup": {"mock": False, "substrate": FAST_LOW_CLASS},
        "intent_live": FAST_MEDIUM_CLASS,
        "consolidation": SLOW_MEDIUM_CLASS,
        "reflection": SLOW_MEDIUM_CLASS,
        "context_events": CONTEXT_EVENTS[DEFAULT],
    },
    SUPER: {
        "analytics": {"mock": False, "substrate": FAST_LOW_CLASS},
        "lookup": {"mock": False, "substrate": FAST_LOW_CLASS},
        "intent_live": FAST_HIGH_CLASS,
        "consolidation": SLOW_HIGH_CLASS,
        "reflection": SLOW_HIGH_CLASS,
        "context_events": CONTEXT_EVENTS[SUPER],
    },
}


class UnknownTier(ValueError):
    """Raised when `budget_tier` is set but isn't one of TIER_NAMES/'custom'."""


#: Tiers apply_tier() never mutates the manifest for. `custom` is the
#: documented escape hatch. `default` joins it for a reason worth being
#: explicit about: the shipped manifest's roles.* IS the appendix's
#: Default combination already (Analytics/Personality/Knowledge on
#: `fast-low`, Intent on `fast-medium`, Consolidator/Reflection on
#: `slow-medium`), so "apply Default" and "change nothing" are the same
#: operation. Making that a real no-op — rather than a preset that happens
#: to reproduce the status quo — means a test (or an operator) that
#: overrides `roles.analytics.mock` for a zero-cost run keeps working with
#: no need to also set `budget_tier: custom`. Only Minimal, Budget and
#: Super actually rewrite anything.
_NOOP_TIERS = (CUSTOM, DEFAULT)


def apply_tier(manifest: Dict[str, Any]) -> Dict[str, Any]:
    """Return a COPY of `manifest` with its `budget_tier` preset applied.

    `budget_tier` absent, "custom", or "default" -> the manifest comes
    back untouched (see _NOOP_TIERS above for why default belongs there
    too). That's also the escape hatch for a manifest that wants to
    mix-and-match rather than take one of the four named combinations.

    `minimal`, `budget`, or `super` OVERWRITE
    `roles.analytics.{mock,substrate}`, every `roles.intent.nodes[*].substrate`,
    and `roles.intent.consolidation_substrate` unconditionally. They have
    to win outright, not merge: the appendix's promise is "switching tiers
    is a one-line manifest edit + restart", and a per-role override sitting
    next to `budget_tier` would silently stop applying on the next switch
    if it only filled gaps. If you want a bespoke mix, set
    `budget_tier: custom` (or leave the field out) and write the per-role
    config directly.
    """
    tier = manifest.get("budget_tier")
    tier = str(tier).strip().lower() if tier else CUSTOM
    if tier in _NOOP_TIERS:
        return manifest

    if tier not in TIER_PRESETS:
        raise UnknownTier(
            f"Unknown budget_tier '{manifest.get('budget_tier')}'. "
            f"Expected one of {TIER_NAMES} or 'custom'."
        )
    preset = TIER_PRESETS[tier]

    out = dict(manifest)
    roles = dict(out.get("roles") or {})

    analytics = dict(roles.get("analytics") or {})
    a_preset = preset["analytics"]
    analytics["mock"] = a_preset["mock"]
    if a_preset["substrate"] is not None:
        analytics["substrate"] = a_preset["substrate"]
    elif a_preset["mock"]:
        # The preset deliberately assigns no substrate (Minimal: Analytics
        # is mocked, full stop — see TIER_PRESETS). Whatever substrate
        # happened to be declared in the base manifest is leftover from
        # before this tier applied — not something this tier stands
        # behind — so drop it rather than let a stale value sit there and
        # get reported by tooling (tools/preflight.py) as if it meant
        # something.
        analytics.pop("substrate", None)
    roles["analytics"] = analytics

    # v0.35f: Intent lost its per-node substrate list along with the
    # fleet/rotation model, and consolidation became its own ROLE rather
    # than a second substrate hanging off Intent. A tier now names both
    # directly, which is simpler than what it replaced.
    intent = dict(roles.get("intent") or {})
    # Every named tier runs Intent live — the appendix's four rows all
    # name a real substrate for it, and only Analytics is ever mocked
    # (Minimal). So, same as roles.analytics.mock above, a tier states
    # this explicitly rather than leaving whatever roles.* already had: an
    # operator's stale `intent.mock: true` sitting next to
    # `budget_tier: minimal` must not silently keep Intent mocked.
    intent["mock"] = False
    intent["substrate"] = preset["intent_live"]
    intent["context_events"] = preset["context_events"]
    roles["intent"] = intent

    # The archive-lookup family (Personality, Knowledge) got a live tier
    # in Phase 0.6, which makes it tier-relevant for the first time: two
    # more substrate calls on EVERY event, in parallel. A tier that exists
    # to cap spend has to have an opinion about that, and Minimal's
    # opinion in particular is load-bearing — its promise is that the
    # whole ecosystem boots with no credentials at all, which a live
    # lookup on a hosted slot would quietly break.
    #
    # Both members always get the same treatment: they are two instances
    # of one class by design, and a tier that split them would be
    # asserting a difference the architecture says doesn't exist.
    l_preset = preset["lookup"]
    for key in ("personality", "knowledge"):
        role = dict(roles.get(key) or {})
        role["mock"] = l_preset["mock"]
        if l_preset["substrate"] is not None:
            role["substrate"] = l_preset["substrate"]
        elif l_preset["mock"]:
            role.pop("substrate", None)
        roles[key] = role

    # Consolidator is a role of its own as of v0.35f. Same explicit-flag
    # discipline: a tier that names a consolidation substrate is also
    # saying consolidation runs for real on it.
    consolidator = dict(roles.get("consolidator") or {})
    consolidator["mock"] = False
    consolidator["substrate"] = preset["consolidation"]
    roles["consolidator"] = consolidator

    # Reflection (dispatch #4) gets its own preset dimension, separate
    # from Consolidator's in principle — even though dispatch #5 has them
    # sharing the same slow-* class per tier, they're named independently
    # here so a future tier can split them without a module change.
    reflection = dict(roles.get("reflection") or {})
    reflection["mock"] = False
    reflection["substrate"] = preset["reflection"]
    roles["reflection"] = reflection

    out["roles"] = roles
    return out


def describe(manifest: Dict[str, Any]) -> str:
    """One line for the boot log — what's ACTUALLY configured right now,
    read from the manifest rather than recomputed from a preset. For
    minimal/budget/super that's identical to the preset (apply_tier already
    wrote it); for default/custom it's whatever roles.* says, which is the
    point — nothing here overwrote it."""
    raw = manifest.get("budget_tier")
    tier = str(raw).strip().lower() if raw else CUSTOM
    label = tier if (tier in TIER_PRESETS or tier == CUSTOM) else f"{tier} (unrecognised)"
    suffix = " — no preset applied, roles.* as written" if tier in _NOOP_TIERS else ""

    roles = manifest.get("roles") or {}
    analytics = roles.get("analytics") or {}
    intent = roles.get("intent") or {}
    consolidator = roles.get("consolidator") or {}

    a = "mock (zero cost)" if analytics.get("mock") else f"live/{analytics.get('substrate')}"
    i = ("mock (zero cost)" if intent.get("mock")
         else f"live/{intent.get('substrate', 'unset')}")
    c = ("mock (zero cost)" if consolidator.get("mock", True)
         else f"live/{consolidator.get('substrate', 'unset')}")
    return (f"{label}{suffix} — analytics={a}, intent={i} "
            f"(context {intent.get('context_events', 'unset')} events), "
            f"consolidator={c}")


__all__ = [
    "TIER_NAMES", "TIER_PRESETS", "UnknownTier", "apply_tier", "describe",
    "MINIMAL", "BUDGET", "DEFAULT", "SUPER", "CUSTOM", "CONTEXT_EVENTS",
    "FAST_LOCAL_CLASS", "SLOW_LOCAL_CLASS", "FAST_LOW_CLASS", "SLOW_LOW_CLASS",
    "FAST_MEDIUM_CLASS", "SLOW_MEDIUM_CLASS", "FAST_HIGH_CLASS", "SLOW_HIGH_CLASS",
]
