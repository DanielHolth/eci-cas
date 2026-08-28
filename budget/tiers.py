"""
Budget tiers — model allocation presets (docs/budget-tiers-appendix.md,
Phase 0.2.2).

Budget MODE (budget/state.py) and budget TIER (this module) are two
different axes and it is worth being precise about the difference:

  budget_mode   RUNTIME. A latch that trips on a classified substrate
                failure or a spend cap and falls back to the deterministic
                per-task fallbacks that already exist. Same substrate
                assignment throughout; the pipeline just stops calling it.

  budget_tier   DESIGN-TIME. Which vendor/model backs each cognitive role
                in the first place — the appendix's Minimal / Budget /
                Default / Super. A manifest edit plus a restart, not
                something that trips mid-session.

A tier answers "what do I want to pay, structurally". Budget mode answers
"what happens right now when that structure can't be reached". They
compose: any tier can still latch into budget mode on a failure.

What's wired today
-------------------
Three cognitive roles have live implementations as of Phase 0.5:
Analytics (Phase 0.2), Intent (Phase 0.4) and Consolidator (v0.35f — the
former "Consolidating" mode of Intent, now a role of its own with its own
substrate slot; what used to be `roles.intent.consolidation_substrate` is
`roles.consolidator.substrate`). Every named tier sets `mock: false`
explicitly on all three, the same way it always did for Analytics — an
operator's stale `mock: true` must not silently survive a tier switch.
Only Minimal mocks a role at all, and only Analytics.

A tier also scales Intent's conversation window (`context_events`, see
CONTEXT_EVENTS below). That rides on every live call, so it is charged
against the same flat-cost claim (§1) as the persona — which makes it a
tier's business.

Personality and Knowledge (v0.35b) are mock-first and have no substrate
to allocate yet, so no tier names one for them.

Minimal and Budget name a `local-fast` substrate class. Nothing new had
to be built for that to work — the substrate layer has taken an
OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, any local runtime)
since before this module existed (substrates/providers.py). A tier is
just a name for a combination that was already reachable one manifest
edit away; see manifests/ecosystem-manifest.yaml for the `local-fast`
entry these tiers point at.
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
#: Purpose of each class, not its literal current vendor — that lives in
#: the manifest's substrates table and can move without this module
#: changing (§10.2). As of the 2026-08-23 preprod stress test,
#: local-fast and fast-reflex are deliberately pointed at real hosted
#: cheap-tier models rather than their normal targets (self-hosted /
#: claude-haiku-4-5) — see the manifest for the current values and why.
LOCAL_CLASS = "local-fast"                 # normally self-hosted, keyless
FAST_CLASS = "fast-reflex"                 # cheap hosted model, live duty
SPECIALIST_CLASS = "identity-specialist"   # expensive*, rare, consolidation-only
# * not currently true — see manifest note on identity-specialist

#: What Analytics used before this module existed, and what Default/Super
#: keep pointing it at. Under Phase 1's single-substrate-class rule
#: (§14) this currently resolves to the exact same model as FAST_CLASS —
#: but it's the class name the shipped manifest and the existing test
#: suite already build on (§10.2's "swap the vendor in the table, not
#: here"), so a tier that claims to match prior behaviour has to name it
#: specifically rather than assume the two classes are interchangeable
#: forever.
ANALYTICS_DEFAULT_CLASS = "fast-reflex"

#: role -> config, per tier. `analytics.substrate: None` means "leave
#: whatever's declared" — irrelevant while analytics.mock is True, and
#: there's no reason to clobber an operator's substrate choice for a role
#: that isn't calling it.
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
        "intent_live": LOCAL_CLASS,
        "consolidation": LOCAL_CLASS,
        "context_events": CONTEXT_EVENTS[MINIMAL],
    },
    BUDGET: {
        "analytics": {"mock": False, "substrate": LOCAL_CLASS},
        "lookup": {"mock": False, "substrate": LOCAL_CLASS},
        "intent_live": LOCAL_CLASS,
        "consolidation": FAST_CLASS,
        "context_events": CONTEXT_EVENTS[BUDGET],
    },
    DEFAULT: {
        "analytics": {"mock": False, "substrate": ANALYTICS_DEFAULT_CLASS},
        "lookup": {"mock": False, "substrate": FAST_CLASS},
        "intent_live": FAST_CLASS,
        "consolidation": FAST_CLASS,
        "context_events": CONTEXT_EVENTS[DEFAULT],
    },
    SUPER: {
        "analytics": {"mock": False, "substrate": ANALYTICS_DEFAULT_CLASS},
        "lookup": {"mock": False, "substrate": FAST_CLASS},
        "intent_live": FAST_CLASS,
        "consolidation": SPECIALIST_CLASS,
        "context_events": CONTEXT_EVENTS[SUPER],
    },
}


class UnknownTier(ValueError):
    """Raised when `budget_tier` is set but isn't one of TIER_NAMES/'custom'."""


#: Tiers apply_tier() never mutates the manifest for. `custom` is the
#: documented escape hatch. `default` joins it for a reason worth being
#: explicit about: the shipped manifest's roles.* IS the appendix's
#: Default combination already (Analytics on `deep-reasoning`, Intent's
#: nodes on `fast-reflex` — both Haiku, per Phase 1's single-class rule),
#: so "apply Default" and "change nothing" are the same operation. Making
#: that a real no-op — rather than a preset that happens to reproduce the
#: status quo — means a test (or an operator) that overrides
#: `roles.analytics.mock` for a zero-cost run keeps working with no need
#: to also set `budget_tier: custom`. Only Minimal, Budget and Super
#: actually rewrite anything.
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
    "LOCAL_CLASS", "FAST_CLASS", "SPECIALIST_CLASS", "ANALYTICS_DEFAULT_CLASS",
]
