# Phase 0.6 — as built

Date: 2026-08-24. Session picked up from `claude/HANDOVER-phase-0.6.md`.

Daniel's call at the top of the session, when asked which agent to bring
to life next: **"let's be ambitious and do all remaining agents. we have
already done intent as a substrate. remember to implement archive
agent."** So this phase closed out §13.4's replacement sequence entirely
rather than taking the handover's recommended one-at-a-time order.

**Test suite: 607 passed, 13 skipped, 1 known pre-existing failure**
(`test_budget_mode.py::TestManifestConfig::test_it_reads_the_shipped_manifest`
— the spend-cap value reflecting Daniel's live-testing config, still left
alone deliberately). Baseline coming in was 451/13/1; Phase 0.6 added 156
tests.

No mocks remain in the roster. Every one of the eleven roles now has a
real implementation running in the shipped manifest.

---

## 0. The reported bug: "consolidate never seems to do anything"

Daniel's own report, and the thing that started the session. His
suspicion was that Consolidator wasn't connected to the message queue.

**It was connected.** Governance's `_conclude()` holds a direct reference
and calls `consolidator.observe(record)` on every concluded event
(`agents/governance/agent.py`), and `recovery/bootstrap.py` wires it.
What was actually happening:

* `batch_size_events: 25` (§15's default). A console session concludes a
  handful of events and never reaches the threshold, so consolidation
  correctly did nothing — and looked broken doing it.
* `synchronous: false`, so even at the threshold the reconcile runs on a
  background worker and its `EpochWritten` ping lands after the console
  has already printed its trace slice.
* **Nothing ever called `flush()`.** The worker is a daemon thread. A
  partial batch died at process exit, and a batch dispatched moments
  before exit could be killed mid-reconcile. Every short session silently
  lost its own long-term memory, with no metric recording it.

Also worth recording: Daniel expected Consolidator to receive a message
from Governance *in parallel with Action*. That was an explicitly
rejected design in v0.35g — a Consolidator fed in parallel cannot see
pipeline-final information (the security verdict, the final proposal, a
block). The post-Action hand-off stands.

### What was built

`ConsolidatorBase` gained two methods:

* `consolidate_now()` — force a pass over whatever is buffered. Returns
  `False` on an empty batch so a caller can say "nothing to do" rather
  than report work that never happened. Deliberately the *only* way a
  pass runs early: nothing lowers the threshold behind the operator's
  back.
* `shutdown(timeout)` — consolidate the partial batch, then drain the
  worker. This is the actual bug fix.

`tools/console.py` gained:

* a `consolidate` command (also `consolidate now`, `reconcile`) that
  forces a pass and prints what happened — or, on an empty batch, prints
  the observed count and the threshold, so the answer to "why does this
  do nothing" is in the output rather than in the source;
* a `--consolidate-every N` flag that overrides the threshold **for that
  session only**. Not a manifest edit: the shipped threshold is a
  deliberate cost decision, and lowering it on disk to make a demo
  visible is how a cost control quietly becomes a cost problem;
* a banner line stating the threshold, and a `shutdown()` call at exit.

13 new tests (`tests/test_phase05_consolidator.py`, appended as
`TestPartialBatch` / `TestConsoleConsolidateCommand`).

---

## 1. Security — live (§5.6)

The handover called this the highest-value target and it was: `SecurityMock`
always cleared green, so the hard stop the whole architecture leans on had
never once stopped anything.

### The open question, answered: what `security_rules.json` looks like

A **closed, declarative pattern list**. Not a DSL, not a scripting hook,
not a bare keyword bag. `agents/security/rules.py` carries the full
reasoning; in short:

* §5.6 demands every verdict be justifiable from the rules file and that
  single event, by a human reading both. A DSL with control flow stops
  being readable exactly when it becomes useful.
* A keyword list cannot express "unless", and nearly every real rule
  needs one ("refuse to give an address, UNLESS it is our own").
* Regex is the smallest thing that covers the real cases with one obvious
  meaning per rule.

Shape:

```json
{"id": "...", "verdict": "red|yellow", "concern": "...",
 "description": "...", "any": [...], "all": [...], "unless": [...]}
```

A rule fires when (any of `any`) AND (all of `all`) AND (none of
`unless`). Design decisions worth not relitigating:

* **Order-independent.** Every rule is tested, highest verdict wins. A
  rules file whose meaning depends on line order is one nobody can safely
  edit.
* **No green rules.** Green is the absence of a match. A green rule could
  only be an attempt to cancel another one, which order-independence
  makes meaningless — the loader rejects it and points at `unless`.
* **A rule with no conditions matches nothing.** The safe reading of a
  malformed rule on the safety path is "does not fire", never "fires on
  everything".
* **Patterns compile at load time**, so a typo is a bootstrap failure
  rather than an exception on the safety path mid-conversation.
* **Every rule needs a `concern`** — a non-green verdict travels to a
  reasoner that has to act on it, and "no" without a reason isn't
  actionable.

### The second open question, answered: concern text is templated

`meta.security_concern` comes from the matching rule's own `concern`
field, not free-form. Security must stay auditable; free-form implies a
writer, and a writer in this seat is the thing §5.6 forbids. The concern
is drawn only from the **decisive** rules (those matching the winning
verdict) — a red action that also tripped a yellow advisory is explained
by the red. `meta.security_rules_matched` carries every match, so the
audit trail stays complete while the explanation stays narrow.

### The agent

`SecurityAgent` (`agents/security/agent.py`), alongside the retained
`SecurityMock`. Still **no LLM, and there will not be one** — a reasoner
here would trade the audit trail for judgment the ecosystem already has
in Intent. Evaluates `meta.proposed_action` (v0.35's input contract),
falling back to content. Strips any stale `security_concern` /
`security_rules_matched` before writing its own — the exact confusion
v0.34's closed enum was introduced to end. Counts verdicts without
enforcing a distribution: §5.6's ~90/9/1 shape is a property of a good
rules file, not a quota, and the metrics are how you find out you don't
have one.

### Fail-closed at bootstrap

`roles.security.mock: false` with a missing, unparseable or empty rules
file **stops the bootstrap** (`BootstrapError`), exactly as an
unreachable credential does for a cognitive role. The reasoning is
recorded in the code: Security's only failure mode that matters is
answering green when it shouldn't, and that is indistinguishable from the
mock — so a Security that cannot load its rules must not boot at all.

`_resolve_config_path()` finds a bare filename relative to CWD, the
manifest, `./config`, the repo root, or the config directory shipping
with the source tree. That last fallback matters: every test fixture
copies the manifest to a tmp dir, and without it they'd all strand the
shipped rule set.

### The shipped rule set

`config/security_rules.json`, v0.6.0, 12 rules. 7 red, 5 yellow:
credentials, impersonation, irreversible world-effects, autonomous
spending, weapons/precursors, self-harm *method* detail, covert
surveillance, and bypassing this system's own checks (red); system
internals, third-party contact, private location, medical dosing
(yellow). Tested as behaviour rather than as JSON — ordinary speech
clears, clear violations are red, judgment calls are yellow, and talking
*about* a hard subject is explicitly not blocked (method detail is; the
subject isn't — the difference between a safety rule and a taboo).

54 tests (`tests/test_phase06_security.py`).

---

## 2. Action — live (§5.7)

§13.4's last mock. Going live here is **not** "Action gains judgment" —
it is "Action finally has somewhere to put things".

`agents/action/sinks.py` defines the seam. A sink receives the envelope
and emits it; it is never handed the pipeline's reasoning, so there is
nothing to author *with*. The invariant survives by construction rather
than by docstring.

* `NullSink` — records, emits nowhere. This is what `ActionMock`'s
  `executed` list always was, given a name.
* `StreamSink` — stdout/stderr. The label (`[Prompt/curious]`) is a
  display affordance derived from the envelope's own type and Impulse's
  `meta.expression`; Action still chooses nothing.
* `FileSink` — one JSON object per emitted action. Deliberately separate
  from Archive's queue log: that records every hop including the ones
  that never reached the world; this records only what the world saw.
* `CallbackSink` — the seam a product layer attaches to (TTS, avatar,
  websocket) without any of it becoming a dependency of this repo.

A sink signals failure by **raising**, not by returning a boolean — v0.33's
failure contract needs a clean boundary between "emitted" and "did not",
and a boolean invites half-success.

`callback` is deliberately **not** manifest-configurable. A deployment
file naming a Python callable to import would make the manifest an
execution vector, in the one role whose job is to affect the world.

`ActionAgent` subclasses `ActionMock` rather than duplicating it — the
executed/blocked logs, the failure envelope and `force_next_failures`
are the *role*, never the mock. Fan-out is per-sink, not all-or-nothing:
one broken channel doesn't stop the others, and the failure report is
what lets the human hear about it through the channel that still works.
The Failure envelope carries the **original content**, never an error
message — Governance's fallback quotes it through Intent, so a stack
trace there would put Action's words in the persona's mouth. The
diagnosis rides in `meta.action_errors`.

Bootstrap: an unknown sink type stops the boot (a typo must not silently
become silence); an empty sink list warns loudly and runs a null sink
(somebody may want a headless deployment; nobody wants an accidentally
mute one). Relative file-sink paths resolve against `storage.root`, not
CWD.

38 tests (`tests/test_phase06_action.py`).

---

## 3. Personality / Knowledge — live (archive-lookup family)

v0.35b shipped this family mock-first deliberately and left one thing
open: judging which of N records **bears on** the event in front of you.
That is the one thing here not expressible as a rule, because relevance
is about meaning.

`agents/archive_lookup/live.py` — `ArchiveLookupAgent`, one class, two
configured instances, exactly as the mock tier is. The contract
(`contract.py`) already covered everything needed and was not changed.

The model is asked for a relevance judgment over a **bounded, supplied**
set. It is explicitly not asked to recall anything: the records in the
prompt are the only permissible source. A lookup answering from
parametric knowledge has quietly become a second Analytics while still
being labelled memory — the failure that would make Intent's bundle
untrustworthy in a way no test catches.

* **Empty-store short-circuit.** No records → no substrate call, reported
  as `deterministic` rather than as a fallback. Not merely an
  optimisation: this runs twice on *every* event, and reasoning over zero
  records is spending money to be told there is nothing there. Reporting
  it as degraded would make a healthy system look like an outage.
* **Silence, never invention**, on every degraded path — outage, bad
  JSON, budget mode. This family gates nothing, so a bad answer costs
  more than no answer.
* `records_considered` in diagnostics: `relevant: false` means something
  quite different over 8 records than over 1.
* Read-only survives going live — still a `_ReadOnlyArchive` view with no
  write method to reach for, on the tier that would actually have
  something to write.

Bootstrap now honours `mock: false` instead of reporting it and running
the mock anyway; an unusable substrate stops the boot like any cognitive
role. Both roles flipped to `mock: false` in the shipped manifest, both
on `fast-reflex`.

**Budget tiers learned about them** (`budget/tiers.py`) — this is the
first time the family is tier-relevant, since it is two more calls on
every event. Minimal mocks them entirely (its promise is booting with no
credentials at all, which a live lookup on a hosted slot would quietly
break); Budget puts them on `local-fast`; Default/Super on `fast-reflex`.
Both members always get identical treatment — a tier that split them
would assert a difference the architecture denies.

31 tests (`tests/test_phase06_lookup_live.py`), plus fixture updates
across 12 existing suites to mock the now-live family.

---

## 4. Archive — now an agent (§5.8)

Daniel's explicit ask. Archive was the only one of the eleven with no
presence on the bus at all.

The store is **not** replaced. `agents/archive/store.py` is unchanged;
`write` and `query` are §5.8's two endpoints and they work. What was
missing:

* a writer had to **hold the store** to write — a much stronger grant
  than "ask Archive to append this", and one with no read-only view
  available the way the lookup family gets one;
* nothing inside Archive was observable from outside it. An epoch landing
  in long-term memory — arguably the most significant thing this system
  does that isn't speech — produced no bus event at all.

`agents/archive/agent.py` — `ArchiveAgent` wraps the store and adds a
door beside it. New topic `events.archive` (business traffic, logged).
Write requests arrive as `Write`/`ArchiveWrite` envelopes carrying one
instruction object or a list. Every completed request publishes an
`ArchiveWritten` receipt on `system.control` — **including when
everything was dropped**, because an instruction that vanished silently
is exactly the failure this agent exists to make visible.

Delegation is total (`write`, `query`, `query_queue`, `log_event`, drive
vectors, `root`), so an agent handed this instead of the raw store cannot
tell the difference. That's what makes adoption a non-event.

**Reads stay synchronous.** A query over the bus needs a reply channel,
and inventing one would duplicate the direct `query` every reader already
has.

**Consolidator was deliberately not migrated.** It is the sole writer of
long-term memory, its writes are synchronous by design, and it uses the
executed/dropped counts to report what a pass lost. Fire-and-forget
messaging would trade a fact for a hope in the one place this system
keeps an auditable record. Pinned as a test so that "move everything onto
the bus" has to be a decision somebody makes on purpose.

`roles.archive.mock` selects whether the **door** exists, not whether
memory works — the store is constructed in step 2 either way, so mocking
this role returns to the pre-0.6 state in one manifest line.

23 tests (`tests/test_phase06_archive.py`).

---

## 5. Manifest changes

| role | was | now |
|---|---|---|
| `security` | `mock: true` | `mock: false`, `rules: security_rules.json` |
| `action` | `mock: true` | `mock: false`, `sinks: [stdout, file]` |
| `personality` | `mock: true` | `mock: false`, `fast-reflex`, `temperature: 0.2` |
| `knowledge` | `mock: true` | `mock: false`, `fast-reflex`, `temperature: 0.2` |
| `archive` | `mock: true` | `mock: false` (bus door on) |

Untouched, as instructed: the `substrates:` block's deliberate preprod
stress-test swap (fast-reflex/deep-reasoning on `gpt-5.4-nano`), and
`local-fast` pointing at `localhost:11434`.

---

## 6. Still open

* **Consolidator's live tier** exists (`agents/consolidator/live.py`,
  `ConsolidatorAgent`) and the shipped manifest runs it. The handover's
  framing that it was mock-only was out of date; what it has never had is
  *exercise* — nothing in the offline suite drives real reconciliation
  reasoning end to end, and the fixed threshold means live substrate
  testing rarely reaches it either. The `--consolidate-every 3` flag
  added above is the cheapest way to actually watch it work.
* **Whether Analytics should stop expressing `proceed`** — flagged open
  since v0.35, still never decided. Small either way; worth a deliberate
  call rather than a permanent question mark.
* **Real rotation across an Intent fleet** (N>1) — still Phase 2+.
* **The clickable consolidation doodle** — new, from Daniel this session.
  Written up in `docs/ideas/consolidation-doodle.md`, including his dedup
  rule: the first click on an epoch reconciles as "the user found it
  interesting that I share what I've learned", every repeat is a
  duplicate Consolidator drops. Depends on `EpochWritten` gaining a
  payload, which couples it to Consolidator's live tier.
* **Security's rule set will need tuning against real traffic.** 12 rules
  is a starting position, not a finished policy. The verdict counters on
  `SecurityAgent.metrics` are the instrument: if ordinary conversation is
  producing yellows, the rules file is wrong, not the engine.
* **`spoken.jsonl` has no rotation.** The transcript grows without bound.
  Fine at this scale; a real deployment wants the same date-partitioning
  the queue log already has.
