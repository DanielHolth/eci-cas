# Roadmap: packs, workshop contributions, social Morrows, toolsmith

Status: design, nothing built. Written 2026-09-19 after the toolkit search/read work. Scope is deliberately wide; the sections are ordered by dependency, not by size.

The thread: today a "toolkit" is a C# class plus a hardcoded descriptor. The goal is an ecosystem where the person, the toolsmith and outside contributors (Steam Workshop) can change what Morrow *does*, *looks like*, *sounds like* and *runs on*, and where friends' Morrows can consult each other, all without the trust model getting weaker than it is now.

---

## 1. Toolkits as manifests over native capabilities

Today: `search`, `guide`, `powershell`, `discord` are C# classes; their routing descriptors (name, description, trigger exemplars) are hardcoded in `ToolkitRegistration.cs`. Manifest toolkits exist but are deliberately narrow: two verbs (`http_call`, `speak_text`), one fixed URL, `{command}` substitution only, `approved` flag set by a human hand.

Add a third verb, `native`, naming a registered C# **capability**:

```json
{
  "name": "search",
  "description": "Searches the web for current information...",
  "triggers": ["What's the latest news?", "..."],
  "tiers": ["budget", "pro", "premium"],
  "verb": { "kind": "native", "capability": "web_search", "options": { "maxResults": 4 } },
  "approved": true
}
```

- Capabilities (`web_search`, `read_page`, `powershell`, `guide`, `discord_post`) are registered by name in code. A manifest can only pick from that list; it never adds code.
- Each capability declares an **options schema** and a **risk class**. The tier gate checks the capability's risk, not just the toolkit name, so a manifest cannot grant `powershell` on a tier that lacks it.
- Descriptions, triggers and tier availability move to JSON: tunable without a rebuild. `Toolkit:Allowed` (added 2026-09-19) becomes the manifest's `tiers`.
- Variants come free: a "weather" toolkit is `web_search` with different triggers and a query template.
- `guide` reads the catalog, so it is a capability that depends on the loaded manifests (the one circular shape).
- Multi-step pipelines (search, read, summarise) stay inside one capability until a second real use case demands a step list.

Needs: option schemas, hot reload of `Toolkits/`, a pending-approval UI in the Toolkit tab.

## 2. The toolsmith (a toolkit that makes toolkits)

A native toolkit routed like any other ("make me a toolkit that checks the weather in Oslo").

- Calls an LLM with the capability registry (names, option schemas, risk classes) and the manifest schema; gets a manifest back.
- A validator checks it: real capability, options fit the schema, unique name, no literal secrets (env var names only).
- Writes it to `Toolkits/` with `approved: false` (the loader's existing "pending, not broken" state).
- **A human approves it** in the Toolkit tab, on a screen showing the raw verb, target and options, not the toolsmith's own description. The toolsmith can never set the flag ("never set this from code" stays true).
- **Routing conflict check:** embed the new triggers with e5; if any exemplar scores within the 0.03 route margin of another toolkit, say so and rewrite it.
- **Dry run:** execute once with a sample command in read-only mode and show the result before approval.
- **Iteration:** edits touch the pending file only; an approved manifest is never modified in place, an edit makes a new pending version.
- Generates **tier 0 packs only** (section 3). Never tier 1 or above.
- Needs an LLM, so Pro and up. Budget can still install and use manifests.

Risks: prompt injection from web content into a toolkit request (the approval screen is the defence; new `http_call` hosts get highlighted); exfiltration via `http_call` + `{command}` in a POST body; it can only recombine existing capabilities, a new one is code.

## 3. Packs: the container above toolkits

A toolkit is one contribution among many. A pack is a folder with a manifest:

```json
{ "name": "oslo-noir", "apiVersion": 1, "permissions": [],
  "contributes": { "toolkits": [], "theme": {}, "skin": {}, "providers": [], "agents": {}, "voice": {} } }
```

### Primitives, not types

The `contributes` list above is only what we thought of. To avoid limiting contributors to our imagination, build on general surfaces and let "types" be conventions on top:

- **The bus.** Anything can perceive (subscribe) and act (publish) within declared permissions. New senses (game state, webcam, Discord, calendar), output sinks (OBS overlay, Twitch chat, smart home), memory backends.
- **Slots.** Every replaceable component has a named slot: substrates, stores, TTS, STT, embedding, avatar renderer, network transport. A pack fills a slot. New slots need no schema change.
- **Assets and config overlays.** Prompts, personas, knowledge corpora, localisation, voices, models, avatar rigs.
- **UI surfaces.** Panels, overlays and widgets mounting into named regions, plus whole skins.

Open format: unknown `contributes` keys are preserved and reported, not rejected; experimental ones use an `x-` prefix. Promote a convention to a first-class type only after several packs converge on it. Freeze the core contract narrowly (bus envelope, slot names, permission model).

Categories beyond the first three we listed: persona packs (name, temperament, Identity/Impulse defaults), knowledge packs (a game's wiki in the passage store, with Sight prompts and toolkits bundled: fits the Steam overlay plan), localisation, routines (scheduled or event-triggered), two-way integrations, evaluation packs (benchmarks, feeding the prompt-benchmark method).

### How the user's three areas map

- **Audio (paid or custom voices):** data plus an endpoint; TTS endpoint manifest like `http_call`, key named by env var, playback via the existing speech layer. Small.
- **Logic, models:** substrate providers are already config (endpoint, model, key env var, OpenAI-compatible). A pack contributes a provider and assigns it to a slot ("Reasoning: my server"). Prompt overrides and roster edits (`BundleRoster` add/remove/reorder) are the same kind of data.
- **Logic, new agents:** code. See trust tiers.
- **Visual:** a theme (CSS tokens, fonts, avatar assets) is data. A full redesign is a static bundle served from a sandboxed origin/iframe under a strict CSP (connect to the Host only), consuming the same event API.

### Trust tiers

| Tier | Contains | Gate |
|---|---|---|
| 0 data | themes, prompts, roster, provider/voice endpoints, manifest toolkits | approval screen with a diff |
| 1 sandboxed code | skins (iframe + CSP), agents as separate processes over the bus | declared permissions, enforced by the Host |
| 2 in-process assemblies | native capabilities | first-party only, or signed and reviewed |

.NET has no in-process sandbox, so third-party agents never load as DLLs. They run out of process and speak the bus over a socket/WebSocket, with declared topics to subscribe and publish. That fits the loose-coupling rule and allows any language.

### Mandatory once outside contributors exist

- **Permissions that name the private data:** a replaced substrate sees every conversation; an agent on `Perception` reads everything said. The approval screen says so in plain words ("your conversations go to example.com").
- **Config as a reversible overlay** on the tier's appsettings. Uninstall removes the layer; the user's files are never edited.
- **Safe mode:** boot with all packs off (flag or hotkey) so a broken skin or agent cannot lock the person out.
- **Versioned contract:** `apiVersion`, bus topic schemas and the event API become a public surface; freeze deliberately.
- **Conflicts:** one active pack per exclusive slot (skin, voice, provider slot); the rest stack.
- **Distribution:** Steam Workshop delivers; signing and review are ours for tier 1 and above.

### Prove the surface before hardening

Build two or three first-party packs using only the public surface: a theme, a paid voice, a small out-of-process agent (e.g. a Discord listener). Every private back door needed is a missing primitive. Show the draft contract to a few would-be contributors early.

## 4. Cross-Morrow collaboration (social)

Owners add each other as friends; their Morrows can consult one another. Not a pack: identity and transport are platform, only what flows over them is contributed.

### Pieces

1. **Identity and friendship:** accounts, friend lists, two-sided consent. Platform infrastructure; a pack must never own identity.
2. **Transport:** a network slot filled by the first-party relay (the same metered relay planned for Android in the archive-inversion note). Alternatives (peer-to-peer, self-hosted) later.
3. **Shared toolkit/pack pool:** distribution only. A friend's approved pack is offered like a Workshop item; the receiver approves locally. Sharing never activates anything. Needs identity and relay but no shared memory, so it is the low-risk first milestone.
4. **Shared memory:** see below.

### Shared memory is query-time federation, never replication

The memory stays on the owner's side. The friend's Morrow asks, the owner's Morrow decides and answers, the answer is ephemeral. Block the friend and nothing remains on their end.

- **Enforce before retrieval:** restrict the search to permitted categories/topics first, then retrieve. A private passage never retrieved cannot be leaked by any prompt, including an injected friend query.
- **Deterministic gate:** code reading a policy; no LLM decides what is shareable.
- **Derived passages only:** answers come from curated shelf passages. Raw utterances (ground truth) never cross.
- **Owner-side audit:** a visible log of what each friend's Morrow asked and what came back, a live indicator while a query is served, and rate limits so a friend cannot enumerate memory with many small queries.
- **Provenance:** every shared passage records whose Morrow wrote it; kept in a separate, labelled ring so a friend's claim is never mistaken for the person's own memory.
- **Untrusted input:** shared text is prompt-injection surface, treated like web content.

### "Never copies" holds only inside the system

- An answer arriving at the friend's Morrow is fed to their Intent prompt and by default would flow into their turn log, archive, Reflection, fact extractor and Hindsight. Results carry an `ephemeral` tag and the receiving side must exclude them from every persistence and derivation path. Enforceable for first-party agents; a pack or agent could bypass it, so the permission model splits "may query friends" from "may persist what came back".
- The friend's person sees the answer and can repeat it; their own utterance is then stored as ground truth. Software cannot prevent that.
- The answer goes to whatever substrate the friend runs. If that is a cloud vendor, data leaves the owner's circle. Policy can include "only answer friends on local substrates", or the consent screen must say so.
- Availability is the cost of no copies: an offline owner cannot answer. Choose between accepting that and short-lived encrypted holds at the relay with a hard expiry (which is a copy, so opt-in).
- Revocation is trivial for queries (block, done); expiring grants for anything cached.

### The privacy-policy toolkit

Policy is **data**, enforced in code; the toolkit is its **editor** ("share my music taste with Alice, keep work private").

- **Default deny.** Changes are confirmed as a plain-language diff ("Alice's Morrow may ask about: music, games. Not: work, health") before applying.
- **Loosening is gated harder than tightening.** "Make everything private" applies at once; any widening needs confirmation on the owner's screen, so an injected "share all with Bob" cannot succeed silently.
- **Key on the existing closed category/topic vocabulary** (the Cataloger's hardcoded set): friend x category/topic gives allow / ask each time / deny, plus per-passage pin and per-passage never-share. No new classification.
- **"Ask each time"** for grey zones: the owner is prompted per query.

### Decide now, before the passage store schema freezes

- Category/topic fields are the policy key.
- An `ephemeral` provenance tag on anything arriving from outside.
- A scope on every stored passage (`private` default, `shared:<friend or group>`, `public`) and a scope dimension in the pack permission model.

## 5. Suggested order

1. Pack container, config-overlay loader, safe mode, permission model, approval screen (open manifest, slots as the extension point). Delivers themes, voices, providers and roster edits on its own; toolkit manifests become a subset.
2. `native` verb, capability registry, option schemas, hot reload (section 1).
3. Skin sandbox.
4. Out-of-process agent bridge.
5. Toolsmith, tier 0 only (section 2).
6. Scope and provenance fields in the passage store (cheap, do early even if unused).
7. Identity, relay, friend-shared packs.
8. Query-time memory federation and the privacy-policy toolkit.

Step 6 is out of order on purpose: it is a schema decision that gets expensive to retrofit.

## 6. Open questions

- Who reviews tier 1 Workshop items, and what does signing cost in effort?
- Who pays for relay traffic between friends on a metered relay?
- Do shared answers ever get an opt-in short-lived cache at the relay, or is "offline means unavailable" final?
- How is a pack's `apiVersion` bumped without breaking installed packs (deprecation window)?
- Does `ask each time` block the friend's turn, or answer "checking with them" and follow up like a toolkit result?

## 7. Starting points in the code (as of 2026-09-19)

- **Toolkit pipeline:** `ToolkitManagerAgent` routes by e5 cosine against `ToolkitDescriptor.Triggers` (`RouteFloor` 0.87, `RouteMargin` 0.03), publishes `ToolkitRequest`; `ToolkitHandlerAgent` dispatches by `IToolkit.Name`; the result comes back as a `Topics.Perception` with `TriggeredByKey="toolkit"`. All in `src/EciCas.Agents/Toolkit/`.
- **Hardcoded descriptors:** `src/EciCas.Host/Startup/ToolkitRegistration.cs` (`All(...)`), filtered by `ToolkitOptions.Allowed` / `Allows` (Budget lists `search`, `guide`). This is what section 1 replaces.
- **Manifests today:** `ToolkitManifest.cs` (verbs `http_call`, `speak_text`), `ManifestToolkit.cs`, `ManifestToolkitLoader.cs` (`Approved` gate, pending/invalid reporting). The `native` verb extends these.
- **Search and read:** `SearchToolkit` (terse hits, hot-hit pick by e5, page extract), `PageReader`, `ReaderGuard` (SSRF guard at connect time). These are the first candidates to become capabilities `web_search` / `read_page`.
- **Slots that already exist as config:** `Substrates:Providers` (endpoint, model, `ApiKeyEnvironmentVariable`), `BundleRoster`, per-tier `appsettings.{Tier}.json`, knobs via `KnobRegistration`.
- **UI:** Next.js front end in `morrow-eci/`, talking to the Host at http://localhost:5179 through the event API. `ReferencesPanel`, `EventLogEntry` (now with an "Intent input" debug line), `types/events.ts`.
- **Not built / loose ends from this session:** no standalone "read this URL" toolkit (the page read only runs inside search); Free tier still has toolkits fully disabled; the route margin, the toolkit-turn ack and the Budget search path have not been verified end to end in the running app.
- **Passage store:** scope and provenance fields do not exist yet (section 4, step 6). Category/topic vocabulary lives with the Cataloger.
