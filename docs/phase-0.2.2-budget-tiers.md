# Phase 0.2.2 — Budget Tiers, as built

**Status:** implemented, offline-tested, additive (zero breaking changes)
**Source:** `docs/budget-tiers-appendix.md` (Minimal / Budget / Default / Super)
**Depends on:** the substrate layer (§10.2, Phase 0.2) and budget mode (Phase 0.2.1) — this
phase adds neither a new mechanism nor a new agent, only a naming layer over both.

**2026-08-23 addendum — preprod stress test:** `fast-reflex`, `deep-reasoning`
and `local-fast` are currently pointed at deliberately cheap real models
(OpenAI `gpt-5.4-nano` and `gpt-4o-mini`) instead of their normal targets,
to press bottlenecks — odd JSON, contract violations, latency, rate
limits — before production. `identity-specialist` was moved the other
direction, onto `claude-haiku-4-5-20251001` (real, well-behaved), so it's
temporarily the *cheapest well-behaved* option rather than the expensive
rare one the name implies. None of this changes what's described below —
tiers still resolve to the same class names; only what those classes
currently point at changed. See "Preprod stress test: cheapest-model
swap" at the end of this doc for the details and what to revert before
going to prod.

---

## What this is

The appendix describes four named combinations of "which vendor backs
which cognitive role" — Minimal, Budget, Default, Super — as a
cost/quality ladder an operator picks once and restarts on. This phase
makes that a real manifest field (`budget_tier`) instead of prose: set
one line, restart, and `roles.analytics.*` (today) and
`roles.intent.nodes[*].substrate` / `roles.intent.consolidation_substrate`
(recorded now, consumed once those roles go live) resolve to the tier's
combination.

It is a different axis from budget **mode** (Phase 0.2.1), and the two
compose rather than overlap:

| | Budget tier | Budget mode |
|---|---|---|
| **What** | Which vendor backs each role, structurally | A runtime latch: keep calling, or fall back |
| **When it changes** | Manifest edit + restart | Mid-session, on a classified failure or spend cap |
| **Where it's decided** | `budget/tiers.py`, applied in `Recovery.parse_manifest()` | `budget/state.py`'s `BudgetManager` |

Any tier can still latch into budget mode. Minimal already runs Analytics
mocked, so there's nothing for budget mode to degrade further there — the
two mechanisms just don't have anything to fight over in that case.

## What was already true, and what's new

The substrate layer has taken a local, OpenAI-compatible endpoint (Ollama,
LM Studio, vLLM) since before this phase existed — `providers.py`'s
`OpenAICompatibleProvider` and the `ollama`/`lmstudio` aliases in
`registry.py` were Phase 0.2 work, not this one's. **Nothing new had to be
built for a local model to work.** What Minimal and Budget needed was a
name for a combination that was already one manifest edit away, plus an
actual substrate-class entry to point at (`local-fast`, added to
`manifests/ecosystem-manifest.yaml`) rather than the commented-out worked
example that stood in for it before.

What *is* new: `budget/tiers.py` — the four presets, the resolution
function, and the boot-time log line — and the `budget_tier` manifest
field itself.

## The presets

| Tier | Analytics | Intent (live, Phase 0.4+) | Consolidation | Analytics mock? |
|---|---|---|---|---|
| **Minimal** | mocked | `local-fast` | `local-fast` | yes |
| **Budget** | `local-fast` | `local-fast` | `fast-reflex` | no |
| **Default** | `deep-reasoning` | `fast-reflex` | `fast-reflex` | no |
| **Super** | `deep-reasoning` | `fast-reflex` | `identity-specialist` | no |

`local-fast` is the substrate class name Minimal/Budget point at — an
`ollama`-provider entry in the manifest's `substrates:` table, keyless,
pointed at `http://localhost:11434/v1` by default. `identity-specialist`
is Super's one expensive class, named so it's obvious at a glance that
nothing in the live pipeline can reach it — only a future consolidation
step would.

## Why `default` is a no-op, not a preset that happens to match

The shipped manifest's `roles.analytics.substrate: deep-reasoning` (and,
once Intent is live, its node substrate) already IS the appendix's
Default combination — both `deep-reasoning` and `fast-reflex` currently
resolve to the same Haiku model under Phase 1's single-substrate-class
rule (§14). So "apply Default" and "change nothing" are the same
operation on this manifest, and `apply_tier()` treats them that way:
`default` and `custom` are both members of `_NOOP_TIERS` in
`budget/tiers.py`.

This matters beyond tidiness. Every existing test that overrides
`roles.analytics.mock` to run a scenario at zero cost — and there are a
lot of them, across `test_phase0_e2e.py`, `test_phase01_governance.py`,
`test_phase02_analytics.py` — writes a manifest whose `budget_tier` is
still whatever the base manifest shipped with (`"default"`). A tier
resolver that treated Default as an active preset would silently
overwrite every one of those overrides back to `mock: false` on the next
`parse_manifest()`, because a preset — by this phase's own design — has
to win outright over whatever `roles.*` already says (see "one line, then
restart" below). That's not a hypothetical: it's exactly what happened
the first time this was implemented, and every phase-0/0.1/0.2 test that
bootstraps failed with a credential error before the no-op fix. Making
`default` genuinely inert was the fix, not a workaround.

## "One line, then restart" — why a tier overwrites rather than merges

`minimal`, `budget`, and `super` overwrite `roles.analytics.{mock,substrate}`
and every `roles.intent.nodes[*].substrate` / `consolidation_substrate`
unconditionally, rather than filling in only what's missing. The
alternative — merge semantics, where an explicit per-role value in the
manifest always wins over the tier — sounds safer but breaks the
appendix's actual promise: "switching tiers is a one-line manifest edit +
restart." If a leftover `roles.analytics.substrate: deep-reasoning`
sitting next to `budget_tier: minimal` silently kept Analytics live and
spending, changing the tier wouldn't have changed anything, and the
operator would have no way to know without reading the resolved manifest
by hand.

The escape hatch is explicit instead: `budget_tier: custom` (or omitting
the field) means "ignore this module, `roles.*` is the whole story" —
exactly today's behaviour, exactly what every existing manifest and test
gets by default.

## What's wired today, and what's recorded for later

Only Analytics has a live implementation (Phase 0.2), so applying a tier
changes real behaviour for exactly one role right now. Intent's live tier
doesn't exist until Phase 0.4 (`agents/intent/agent.py` is still
`IntentMock`), and Consolidation isn't its own spend point yet either —
`IntentMock._consolidate()` writes a templated epoch regardless of tier.

Applying `minimal`/`budget`/`super` still writes
`roles.intent.nodes[*].substrate` and `roles.intent.consolidation_substrate`
onto the manifest. That's forward compatibility, not dead code: a
manifest with `budget_tier: minimal` written today keeps working
unmodified once Intent and Consolidation go real, the same "declared in
the manifest, credential-checked at boot, waiting" posture the substrate
layer itself shipped with ahead of Phase 0.2 (v0.34's closing note on
Analytics inheriting the substrate layer "tested and credential-checked"
before it needed it).

## Testing

`tests/test_budget_tiers.py` — offline, no key needed, same posture as
`test_budget_mode.py`:

- each named tier resolves to exactly what the table above says;
- `default`/`custom`/absent are no-ops, and don't mutate the input manifest;
- a tier overwrites an explicit conflicting `roles.analytics.*` setting
  rather than merging with it (the property the whole "one line, then
  restart" design rests on);
- an unknown tier name raises `UnknownTier`, and `Recovery.parse_manifest()`
  turns that into a `BootstrapError` — the same deterministic-stop
  posture as every other misconfiguration (§9.1 step 6);
- `minimal` boots the whole ecosystem to "system live" with **no**
  `ANTHROPIC_API_KEY` or `OPENAI_API_KEY` set at all, which is the actual
  point of the tier.

Full suite: `209 passed, 26 skipped` (skips are the opt-in `-m live` /
real-vendor-SDK tests, unaffected by this phase and unchanged in count).

## What this deliberately did not do

- **No new agent, no new mechanism.** Tiers are a name for combinations
  the substrate layer and budget mode already supported.
- **No change to Analytics' or Intent's runtime code.** `AnalyticsAgent`,
  `AnalyticsMock`, and `IntentMock` are all untouched; they still just
  read `roles.*` off the manifest, which now happens to have been
  pre-resolved by a tier.
- **No opinion about which tier is "right."** That's the appendix's job,
  restated in the manifest's `budget_tier` comment; this phase just makes
  the choice mechanical.

## Preprod stress test: cheapest-model swap (2026-08-23)

Requested: intentionally target the cheapest available real models to
press whatever breaks before production — malformed JSON, contract
violations under `agents/analytics/contract.py`, latency against the
60s/120s timeouts, rate limiting — rather than find out in prod. Three
substrate-class entries in the manifest changed; `budget/tiers.py`'s
class names and preset structure did not.

| Class | Was | Now | Provider | $/MTok in / out |
|---|---|---|---|---|
| `fast-reflex` | `claude-haiku-4-5-20251001` | `gpt-5.4-nano` | OpenAI | 0.20 / 1.25 |
| `deep-reasoning` | `claude-haiku-4-5-20251001` | `gpt-5.4-nano` | OpenAI | 0.20 / 1.25 |
| `local-fast` | `llama3.1:8b` (Ollama, keyless, local) | `gpt-4o-mini` | OpenAI | 0.15 / 0.60 |
| `identity-specialist` | `claude-opus-4-5-20251001` (placeholder, never verified) | `claude-haiku-4-5-20251001` | Anthropic | 1.00 / 5.00 |

Model ids and prices were checked against vendor/aggregator documentation
on 2026-08-23 (OpenRouter's model pages for the two OpenAI models;
Anthropic's own `platform.claude.com/docs/en/about-claude/models/overview`
and pricing page for Haiku 4.5, which confirms `claude-haiku-4-5-20251001`
— the id this manifest already had for `fast-reflex` before the swap —
is real and correctly priced at $1.00/$5.00). That's model-catalog
verification, not a reachability probe: nothing in this sandbox can reach
your Anthropic/OpenAI accounts or run against your keys. Run the real
check yourself:

```bash
export OPENAI_API_KEY=...
export ANTHROPIC_API_KEY=...
python -m tools.preflight --manifest manifests/ecosystem-manifest.yaml --live
```

Offline resolution (`--manifest`, no `--live`) was run here and confirms
the manifest parses and every class resolves to the right adapter/model —
`analytics` -> `openai-compatible:gpt-5.4-nano`, correctly reports a
missing `OPENAI_API_KEY` with no key set. That's as far as this sandbox
can verify; `--live` is the step that actually asks OpenAI/Anthropic "do
you serve this."

**Three things worth noticing, not just accepting:**

1. **`identity-specialist` is now cheaper than `fast-reflex`/`deep-reasoning`**
   ($1/$5 vs $0.20/$1.25) — inverted from what "specialist" implies. That's
   correct for *this* test (Haiku is the best-behaved of the three
   available here) but would be a confusing production config if it
   shipped this way. A real specialist model (Opus-class) is still TBD.
2. **`local-fast` is not local or free right now.** It's real, metered
   OpenAI spend standing in for the self-hosted runtime that doesn't
   exist yet. Minimal tier stays $0 in practice today only because
   nothing calls `local-fast` yet (Intent isn't live) — once it is, this
   entry needs to point at an actual local runtime (`base_url`, keyless)
   before Minimal's "$0/month" claim in the appendix and README is true
   again.
3. **`orthogonal` was left untouched** (still `claude-haiku-4-5-20251001`)
   — it's reserved for Phase 3 diversity (§7.5), not part of the
   local/budget/specialist vocabulary this swap covers, and nothing
   requested it move.

**Before production:** revert `fast-reflex`/`deep-reasoning` to a real
model choice, point `local-fast` at an actual self-hosted runtime
(`api_key_env: null`, `base_url` set, price `0.00/0.00`), and decide
`identity-specialist`'s real model. All three are called out inline in
the manifest with `PREPROD STRESS TEST` / `TEMP` markers so they're easy
to grep for.

### Bug found by the swap: `max_tokens` vs `max_completion_tokens`

First bottleneck the stress test actually surfaced, same day: `gpt-5.4-nano`
400s on `max_tokens` —

```
400 - {'error': {'message': "Unsupported parameter: 'max_tokens' is not
supported with this model. Use 'max_completion_tokens' instead."}}
```

`OpenAICompatibleProvider` already had a mechanism for exactly this shape
of drift (`_split_supported`, `substrates/providers.py`) — but it works by
`inspect.signature()` on the installed SDK's `create()` method, and that
signature still lists `max_tokens`; the SDK forwards it, the *model*
rejects it. That's a distinction the existing mechanism structurally
couldn't see: it detects what changed between SDK generations, not what a
specific model refuses to accept.

Fixed in `substrates/providers.py`: `OpenAICompatibleProvider` now
recognises this specific vendor error text (`_is_max_tokens_rename_error`,
matched on the message rather than a model-name allowlist, so a future
model with the same quirk needs no code change), retries the same call
once with `max_completion_tokens`, and caches the outcome per model
(`self._token_param_by_model`) so every later call against that model
goes straight to the right parameter name instead of paying for a
failing round trip each time. Transparent to Analytics — `AnalyticsAgent`
only ever sees success or a `CompletionError`, so budget mode's failure
counter never learns about a retry that succeeded.

Covered by `tests/test_substrate_providers.py`:
`test_max_tokens_rename_is_retried_and_then_remembered` (retry-then-cache,
against a stub server returning the exact error text above) and
`test_a_genuine_bad_request_is_not_mistaken_for_the_rename` (an unrelated
400 must still surface as an ordinary `CompletionError`, not trigger the
retry). Full suite: 224 passed, 13 skipped.
  restated in the manifest's `budget_tier` comment; this phase just makes
  the choice mechanical.
