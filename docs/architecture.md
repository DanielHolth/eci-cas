# Architecture

The system is a bus-driven agent runtime: one queue per agent, fire-and-forget
publishing, and no agent calling another directly. The code treats outcomes as
truth, not cross-agent ordering.

## Strategic contract

This is the live product direction and layer contract. It is not an open
roadmap item.

- Thick client on the user's machine.
- Cross-platform host runtime across Windows, macOS and Linux.
- Remote relay owns API keys and provider secrets.
- Steam Cloud owns durable user state and the utterance parquet archive.
- Client-side local state remains device-scoped and transient.

## Layer boundaries

- Shared core: bus, agents, prompts, archive contracts, routing, and the
  memory model.
- Platform shell: OS/window/input capture, desktop integration, and UI
  lifecycle.
- Remote relay: provider auth, secret management, and model gatewaying.
- Sync layer: Steam Cloud archive/state and durable user memory.

The shell, host, relay and sync layers are not allowed to blur together.

## Turn flow

1. Perception publishes a fresh event with a correlation id.
2. Impulse, Identity, Hindsight, Recall, Sight and Scribe react in parallel.
3. Governance bundles advisories and publishes a verdict.
4. Intent writes the proposal.
5. Security enforces the final guardrail.
6. Action delivers the response.
7. Reflection may publish a self-driven idea after the turn.

## Memory and state

- Utterances are ground truth.
- Facts are derived from utterances and rebuilt as needed.
- Passages are reflection output, not user truth.
- Durable state and archive live in the sync layer; local state is transient.

**Fact reliability.** Every minted fact carries `Confidence` and `Freshness`
(both 0–1), plus `EvaluatedAt` and `OriginModel`, written to the facts
parquet by `FactReliabilityScorer` at mint time — before threading, so the
scorer sees the fact in isolation, not against the archive. Freshness decays
by `Class` and age. Confidence is a mint-time proxy (origin model present,
entity resolved, class not `other`), not a judgment of extraction quality or
contradiction — that needs the weaver's verdict, which runs after minting,
and is open work (see `roadmap.md`).

## Governance and tools

Governance owns the verdict and the final handoff. Tooling stays explicit and
auditable: requests are routed, results are folded back into perception, and the
system can explain what it did without making tool execution part of the core
agent call stack.

Governance only makes decisions. It bundles advisories, gates on the
verdict, and counts revision passes. It also writes the honesty notice: a
fixed, non-model message that says which advisor was degraded or missing.

## Toolkits

The companion UI can expose one or more toolkits as first-class, async
operations. A toolkit is not a normal agent call stack: it is a dedicated
request/result pipeline that reports back into perception so the persona can
speak the outcome without making the tool execution itself part of the
turn-loop call chain.

- `events.toolkit.request` — request sent by the UI or a prompt-driven
  action to run a toolkit capability.
- `events.toolkit.result` — the execution result from the toolkit handler.
- `events.perception.toolkit` — a perception event created by the toolkit
  manager for logging and display, in parallel with the normal
  `events.perception` path.

The flow is:

1. The UI opens a left-side Toolkit panel listing available toolkits and
   their current status.
2. `ToolkitManagerAgent` fans a turn out to whichever registered toolkit's
   trigger exemplars it best matches, above `Toolkit:RouteFloor` — the built-in
   ones (`powershell`, `guide`, `accessibility`, `discord`) and any approved
   JSON manifest toolkit alike; the toolkit guide is always listed as a
   built-in explanation surface (`MorrowGuide` plus each registered
   descriptor).
3. `ToolkitHandlerAgent` executes the requested command in-process and
   publishes the structured result back to the bus.
4. `ToolkitManagerAgent` converts that result into a perception-ready text
   payload, which can then be vocalized by Morrow as ordinary perception.
5. Status ordering is stable: running toolkits first, then completed ones by
   finish time descending, then ready/unassigned entries by updated time.

This keeps toolkit execution explicit and observable: the system can explain
what it is doing, report a result, and keep the UI agent-local rather than
mixing tool execution into the core bus semantics.

**Built-in toolkits.** `PowerShellToolkit` translates natural language into a
script via a substrate call, then runs it; `AccessibilityToolkit` speaks text
aloud through the Windows speech engine; `DiscordToolkit` posts to a
configured channel through a bot token; `GuideToolkit` answers "what can you
do" from the live descriptor set plus `MorrowGuide`'s fixed prose about
Morrow herself.

**JSON manifest toolkits.** Anything dropped as a `*.json` file in the
`Toolkits/` directory is read at startup by `ManifestToolkitLoader` as a
`ToolkitManifest` — no rebuild required. A manifest can only compose one of
two fixed verbs (`http_call` to an author-fixed URL, or `speak_text`), never
arbitrary code, which keeps its ceiling small enough to be safe if the format
is ever exposed as a Steam-Workshop-style "subscribe" button. Every manifest
carries an `Approved` flag that defaults to `false` and that nothing in code
ever sets: a manifest that fails validation, or is valid but not yet
approved, is logged at startup and skipped, never registered as a callable
`ManifestToolkit`. Turning one on is a deliberate, human, one-line edit —
installing a manifest and it running are never the same action. See
`Toolkits/README.md` for the manifest schema.

**Consent gates.** Two of the built-in toolkits carry a real capability —
running commands, capturing the screen — and both are off by default on
every tier, with no tier file ever flipping them on:

- `PowerShellOptions.Approved` (`PowerShell:Approved`) gates
  `PowerShellToolkit.ExecuteAsync` directly, separately from the shared
  `Toolkit:Enabled` switch every toolkit answers to. `Toolkit:Enabled` decides
  whether the fan-out runs at all (off on Free/Budget); `PowerShell:Approved`
  is PowerShell's own switch for its own risk, and defaults to `false`
  everywhere. Not approved means `ExecuteAsync` returns a clear refusal
  outcome instead of translating or running anything.
- `ScreenShotOptions.Enabled` (`Shell:Screen:Enabled`), covered under Sight
  above, gates whether a screenshot is ever taken in the first place — same
  off-by-default, human-edit-only shape.

Both follow the same pattern as a manifest's `Approved` flag: a config value
nothing in code sets to `true`, so turning the feature on is a deliberate act
by the person who read what it does.

## Config over code

- **Tiers:** `appsettings.<Tier>.json` files, layered on top of the base
  config.
- **Substrates:** `Substrates:Agents` maps each model consumer to a provider.
  The map is validated at boot.
- **Instructions:** all prompt prose lives in `src/EciCas.Host/instructions/`,
  one file per agent. Everything is loaded at boot, and any error stops the
  host.
- **Runtime knobs:** these can be changed live from the Debug panel.

Vendors disagree about their own wire format, so the disagreements are
config too: `Substrates:Providers:<name>:MaxTokensField` names what an
endpoint calls the output ceiling, because OpenAI's reasoning models answer
400 to `max_tokens` while llama-server and Mistral still take it.
