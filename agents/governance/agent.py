"""
Governance — REAL, deterministic tier (§5.1, spec v0.35).

The non-thinking backbone, taken at its word. No persona, no opinions,
never explains itself, and since v0.34 no substrate: every hop it handles
is settled by the envelope alone, so there is nothing for a model to
decide and nothing for one to write.

v0.35 made it the UNIVERSAL ROUTER — every hop in the pipeline passes
through here except the one Sensory fan-out (v0.35a), which is
deliberately ungated. See agents/governance/routing.py for the table and
agents/governance/buffer.py for the one thing this role now holds.

Three jobs, all mechanical
---------------------------
  1. Buffer and bundle. Impulse/Analytics/Personality answer the same
     event in parallel; Governance collects all three and sends ONE
     bundled message to Intent (v0.35c) — and, since 2026-08-29, the same
     bundle forked to Consolidator, so both see the identical evidence
     (raw event + the knowledge swarm's retrieval) when they reason about
     it. It assembles the envelope and writes none of its contents.

  2. Dispatch on Security's verdict. Green releases to Action; yellow and
     red both go to Intent as of v0.35e (Analytics is isolated from
     Security in every way — Daniel, 2026-08-24). Anything unreadable is
     treated as yellow, so the pipeline's one irreversible step is
     reachable by exactly one value. Every non-green verdict spends one
     clearance attempt, and when the budget is gone the event is BLOCKED
     rather than re-asked — the bound that keeps this from live-locking.

  3. Conclude the event. Once Action has run, Governance releases the
     buffered per-event state and forgets the event (§5.1).

Two properties are enforced here rather than trusted to callers:

  Severity is never touched. Outbound envelopes are built with
  Envelope.reply() and no severity argument, so the tag computed upstream
  propagates unchanged (§3's OR-upscale-only rule).

  The control plane is identical to the business path in cost and
  mechanism. BootCheck / SystemCheck are answered by the same native
  code, which is what lets Recovery bootstrap and health-check the
  ecosystem with every model endpoint on earth offline (§9).
"""
from __future__ import annotations

import threading
from typing import Any, Dict, Optional, Set

from bus.envelope import Envelope, new_event_id
from bus.pubsub import EmbeddedBus

from agents.governance import routing
from agents.governance.buffer import DEFAULT_WORKERS, BundleBuffer, EventState
from agents.governance.routing import RoutingDecision

#: Hard cap on entries the buffer will hold. See _evict_stale — this is a
#: backstop against a misconfigured worker set, not a working limit.
MAX_IN_FLIGHT_EVENTS = 256

#: How many revision attempts a red verdict buys. Mirrors
#: agents.intent.contract.MAX_REVISION_PASSES — imported lazily below so
#: this module keeps no import-time dependency on Intent's package.
def _max_revision_passes() -> int:
    from agents.intent.contract import MAX_REVISION_PASSES
    return MAX_REVISION_PASSES


class Governance:
    """The dispatcher. One tier, no substrate, no state across events."""

    tier = "deterministic"

    def __init__(self, bus: EmbeddedBus, *,
                 expected_workers: Optional[Set[str]] = None,
                 impulse=None,
                 structured_store=None, budget_tier: str = "default"):
        self.bus = bus
        #: The four parallel answers this router waits for (v0.35a).
        self.buffer = BundleBuffer(expected_workers or DEFAULT_WORKERS)
        #: Read-only use: the blocked incident asks Impulse for the
        #: expression its CURRENT appraisal state implies. Governance
        #: never sets a drive vector itself; the frustration nudge goes
        #: over the control plane like every other cross-agent signal.
        self.impulse = impulse
        self._structured_store = structured_store
        self._budget_tier = budget_tier

        # 2026-08-25 (Daniel): Sensory's cognitive fan-out is now genuinely
        # concurrent (agents/sensory/agent.py), so Analytics'/Personality's/
        # Knowledge's replies can call on_event() from three different
        # threads at once, for the SAME event_id. Everything below —
        # buffer.get(), the slot write, ready()/decide(), emit(), _conclude()
        # — is a read-modify-write over one EventState, and none of it was
        # written with that in mind. One RLock, held for the whole business-
        # event section, is the minimal fix: it's reentrant because emit()
        # can publish synchronously and re-enter on_event on the SAME
        # thread (e.g. a failing Action loops back to Governance before
        # returning) — a plain Lock would deadlock there. Nothing this
        # guards ever calls a substrate or blocks on I/O, so serializing it
        # costs nothing; the actual parallelism win is upstream, in the
        # slow calls that happen before a worker ever reaches this lock.
        self._lock = threading.RLock()

        # Observability counters ONLY. Never read by decide(): Governance's
        # per-event statutory context reset (§5.1) means no decision may
        # depend on anything that happened in a previous event.
        self.metrics: Dict[str, int] = {
            "events": 0, "routed": 0, "dropped": 0, "verdicts_inferred": 0,
            "bundles": 0, "held": 0, "incomplete": 0,
            "reflexes": 0, "revisions": 0, "blocked": 0, "concluded": 0,
        }
        self.bus.subscribe("events.governance", self.on_event)
        self.bus.subscribe("system.diagnostic", self.on_diagnostic)

    # ---- Control plane: Recovery's synthetic pings (§9, §11 Level 2) -----

    def on_diagnostic(self, envelope: Envelope) -> None:
        if envelope.destination != "Governance":
            return  # e.g. Analytics' reply back to Recovery — not ours

        if envelope.type == "BootCheck":
            # §9.1 step 6: verify full pass-through to Governance and back.
            out = envelope.reply(source="Governance", destination="Recovery",
                                 type="BootCheckAck", content="alive")
            self.bus.publish("system.diagnostic", out)
        elif envelope.type == "SystemCheck":
            # §11 Level 2: routed to Analytics, which replies directly to
            # Recovery — Action is bypassed.
            out = envelope.reply(source="Governance", destination="Analytics",
                                 type="SystemCheck", content="liveness check")
            self.bus.publish("system.diagnostic", out)

    # ---- Business events --------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1
        trigger = routing.classify(envelope)

        if trigger is routing.Trigger.UNROUTABLE:
            # Nothing to hold and nothing to route. Critically, do NOT
            # create a buffer entry on the way to dropping this: an
            # unroutable envelope has no event to conclude, so an entry
            # minted here would never be released. That was a real leak —
            # unbounded growth, holding verbatim user content, in a
            # process designed to run 24/7.
            self.metrics["dropped"] += 1
            return

        # Everything from here down reads and mutates one EventState, and
        # (2026-08-25) may now be entered by more than one worker's reply
        # thread concurrently for the same event_id — see the RLock's own
        # comment in __init__ for why.
        with self._lock:
            state = self.buffer.get(envelope.event_id)

            if trigger is routing.Trigger.WORKER_REPORT:
                self._record_worker(envelope, state)
            elif trigger is routing.Trigger.INTENT_ADVICE:
                self._record_intent(envelope, state)
            elif trigger is routing.Trigger.SECURITY_VERDICT:
                self._record_verdict(envelope, state)

            decision = routing.decide(
                envelope,
                bundle_ready=state.ready(),
                revision_passes=state.revision_passes,
                max_revision_passes=_max_revision_passes(),
                sensory=state.sensory,
            )

            if decision is None:
                if trigger is routing.Trigger.WORKER_REPORT and not state.bundled:
                    # Waiting on the other answers. Not a drop — a hold.
                    self.metrics["held"] += 1
                    self._evict_stale(keep=envelope.event_id)
                    return
                # A duplicate report after bundling, or a worker answering
                # an event that already short-circuited.
                self.metrics["dropped"] += 1
                return

            if decision.diagnostics.get("verdict_inferred"):
                self.metrics["verdicts_inferred"] += 1

            self._note_route(decision, state)
            out = self.emit(envelope, decision)

            # The event is over once something reached Action. Hand
            # Consolidator the whole arc, then forget it (§5.1).
            #
            # emit() publishes synchronously, so a failing Action
            # re-enters this method and concludes the event from INSIDE
            # the frame above before we get here. _conclude is therefore
            # idempotent — without that, an Action failure consolidated
            # the same event twice. The RLock permits this same-thread
            # re-entry; a plain Lock would deadlock on it.
            if decision.route.topic == "events.action":
                if state.concludes_on_action():
                    self._conclude(state)
                else:
                    # A Critical reflex just reached the human. The event
                    # is NOT over: the fan-out is still running behind it
                    # and Intent's considered reply is still to come.
                    # Remember what the reflex did so Intent can speak to
                    # it.
                    state.reflex_action = str(decision.content)
            return out

    # ---- Per-event bookkeeping (v0.35c/g) ---------------------------------

    def _record_worker(self, envelope: Envelope, state: EventState) -> None:
        """One of the four parallel answers (v0.35a). Governance stores
        the slot as the worker reported it — it never rewrites, ranks or
        summarises a contribution."""
        if state.bundled:
            return
        if not state.sensory:
            state.sensory = str(envelope.content)
        if not state.source_type:
            state.source_type = str(envelope.meta.get("source_type") or "")
        if state.ref_event_id is None and envelope.meta.get("ref_event_id") is not None:
            state.ref_event_id = envelope.meta.get("ref_event_id")
        # §3's OR-upscale-only rule has to survive bundling: see
        # EventState.severity for why taking the max here is load-bearing
        # rather than tidy.
        state.raise_severity(envelope.severity)
        slot = envelope.meta.get(envelope.source.lower())
        if slot is None:
            # Impulse doesn't write a role-named slot; its contribution IS
            # the reflex and drive vectors it already puts on every hop.
            slot = {"reflex": envelope.meta.get("reflex"),
                    "drive_vectors": envelope.meta.get("drive_vectors"),
                    "severity": envelope.severity}
        state.slots[envelope.source] = dict(slot) if isinstance(slot, dict) else {
            "findings": slot}

        # NOTE what does NOT happen here on a Critical: the other three
        # answers are NOT discarded. The reflex fires immediately (see
        # _note_route), and the fan-out still completes behind it, so
        # Intent gets its bundle and voices a second, reflex-aware
        # reaction. See EventState.reflex_fired.

    def _record_intent(self, envelope: Envelope, state: EventState) -> None:
        proposal = str(envelope.meta.get("proposed_action") or envelope.content)
        state.final_proposal = proposal
        if not state.sensory:
            state.sensory = proposal

    def _record_verdict(self, envelope: Envelope, state: EventState) -> None:
        verdict = routing.read_verdict(envelope)
        state.verdict = verdict
        concern = envelope.meta.get("security_concern") or envelope.meta.get("concern")
        if verdict != "green" and concern:
            state.security_concern = str(concern)[:300]

    def _note_route(self, decision: RoutingDecision, state: EventState) -> None:
        route_id = decision.route.id
        if route_id == routing.BUNDLE.id:
            state.bundled = True
            self.metrics["bundles"] += 1
        elif route_id == routing.REVISE.id:
            # BOTH non-green lanes spend an attempt. Bounding only REVISE
            # left the yellow lane unbounded — and Intent's fail-closed
            # answer on a yellow is a decline sentence that comes straight
            # back here for clearance, so a rule engine that yellows a
            # decline yellows it forever.
            state.revision_passes += 1
            self.metrics["revisions"] += 1
        elif route_id == routing.REFLEX.id:
            state.reflex_fired = True
            self.metrics["reflexes"] += 1
        elif route_id == routing.BLOCKED.id:
            state.blocked = True
            self.metrics["blocked"] += 1

    # ---- Emission ---------------------------------------------------------

    #: Routes that hand off to Security. Security decides "is this against
    #: the rules" (§5.6) from the proposed action and the event's severity
    #: alone — it has no business seeing Analytics' recommendation,
    #: Personality's/Knowledge's findings, or Intent's own diagnostics
    #: about how it decided (Daniel, 2026-08-24: "why does security need
    #: to see what analytics, personality and knowledge wrote?"). It
    #: doesn't, so it isn't shown them.
    _SECURITY_ROUTES = (routing.CLEAR.id, routing.REFLEX.id)

    def emit(self, envelope: Envelope, decision: RoutingDecision) -> Envelope:
        """Publish the decision.

        Reading meta.governance in a trace: it describes the hop it sits
        on ONLY where source == "Governance". Routes that carry meta
        forward hand the whole meta dict to the next agent, and Security
        echoes meta back on its verdict — so a stale block can ride along
        on a hop Governance didn't produce. Filter on source."""
        route = decision.route
        state = self.buffer.peek(envelope.event_id)

        if route.id in self._SECURITY_ROUTES:
            # Minimal by construction, not by convention: only what the
            # round trip through Security structurally needs to survive
            # (the proposed action, so SPEAK can resolve Action's content
            # once a green verdict comes back) rides along. Everything
            # else Intent still needs on the far side of a yellow/red
            # verdict — the bundle's recommendations, security's own
            # concern, the revision count — is re-derived below from
            # Governance's own per-event state rather than trusted to
            # survive Security's echo.
            meta = {}
            if route.id == routing.REFLEX.id:
                # On the reflex fast path Intent hasn't run — there is no
                # Intent-authored proposed_action yet. The thing actually
                # about to reach the human is the reflex reaction itself
                # (Impulse's `reflex` phrase, already baked into
                # decision.content's template), so THAT is what has to
                # survive Security's echo as `proposed_action`, or SPEAK's
                # content resolution falls through to Security's own bare
                # verdict word ("Green") once it clears — a real bug found
                # 2026-08-24: every Critical reflex was speaking the
                # literal word "Green" to the human instead of its
                # reaction.
                reflex_text = str(envelope.meta.get("reflex") or "").strip()
                if reflex_text:
                    meta["proposed_action"] = reflex_text
            else:
                proposed_action = envelope.meta.get("proposed_action")
                if proposed_action is not None:
                    meta["proposed_action"] = proposed_action
        elif route.id == routing.BUNDLE.id:
            # 2026-08-25 (Daniel): NOT `dict(envelope.meta)`. This route
            # fires on whichever worker's reply happens to be the LAST to
            # complete the bundle — and until the fan-out ran on a thread
            # pool, that was always Knowledge (fixed dispatch order), so
            # carrying its meta forward looked harmless: the same stray
            # role-named slot (e.g. `meta["knowledge"]`) rode along on
            # every single run, unnoticed, because it was always the same
            # slot. True concurrency makes the arrival order genuinely
            # vary, so that stray slot started flipping between
            # "personality" and "knowledge" run to run — a real
            # arrival-order leak into what's supposed to be a clean bundle
            # (nothing downstream ever read it; Intent reads
            # `meta["recommendations"]`, never a role-named key). Starts
            # clean; the block below rebuilds everything Intent actually
            # needs from `state`.
            meta = {}
        elif route.carry_meta:
            meta = dict(envelope.meta)
        else:
            meta = {}
        governance_meta: Dict[str, Any] = {"tier": self.tier}
        governance_meta.update(decision.diagnostics)
        if decision.rationale:
            governance_meta["rationale"] = decision.rationale
        meta["governance"] = governance_meta

        if route.id == routing.BUNDLE.id and state is not None:
            # The three analytical answers, projected to one shared shape
            # (Daniel, 2026-08-24) — sender, keywords. None of Analytics'/
            # Personality's/Knowledge's own tier or diagnostics rides
            # along; Intent needs to know who said what, not how each of
            # them arrived at it.
            #
            # 2026-08-25: proceed/concern are gone. Analytics used to gate
            # Intent's ADVISE/REFUSE choice through this exact meta —
            # real, live logic, not a hypothetical remnant — even though
            # v0.35e had already moved the actual veto to Security/Intent.
            # That meant Analytics could still steer Intent into a REFUSE
            # register on nothing but its own unbiased-keywords opinion,
            # which is precisely the "dead gate" Daniel asked to remove:
            # the only real gate left in this system is Security's red
            # verdict. Task.from_envelope now always resolves BUNDLE to
            # ADVISE (see agents/intent/contract.py).
            meta["recommendations"] = state.recommendations()
            analytics = state.slots.get("Analytics") or {}
            if analytics.get("recommendation"):
                meta["recommendation"] = analytics["recommendation"]
            impulse = state.slots.get("Impulse") or {}
            if impulse.get("reflex"):
                meta["reflex"] = impulse["reflex"]
            if state.reflex_fired:
                # The human has ALREADY seen something happen. Intent is
                # about to be the second thing they see, and it should
                # know that — a persona that talks over its own reflex
                # reads as two disconnected voices rather than one mind
                # catching up with its own hands.
                meta["reflex_already_acted"] = True
                meta["reflex_action"] = state.reflex_action

            # Phase 0.8: Knowledge swarm — drill structured archive
            # using Analytics' path recommendations.
            if self._structured_store is not None:
                paths = analytics.get("knowledge_paths") or []
                if paths:
                    from agents.governance.knowledge_swarm import retrieve_per_path, format_for_intent
                    # Analytics already did the semantic work of matching
                    # loose phrasing ("kids") to the stored vocabulary
                    # ("children") when it picked these paths — reuse that
                    # keyword line here rather than re-deriving a weaker,
                    # literal-only signal from the raw input alone.
                    query = f"{state.sensory} {analytics.get('recommendation', '')}"
                    per_path_results = retrieve_per_path(
                        self._structured_store, paths, tier=self._budget_tier,
                        query=query)
                    all_results = []
                    swarm_detail = []
                    for path, records in per_path_results:
                        all_results.extend(records)
                        swarm_detail.append({
                            "path": path,
                            "count": len(records),
                            "detail": format_for_intent(records),
                        })
                    if all_results:
                        swarm_text = format_for_intent(all_results)
                        meta["knowledge_swarm"] = swarm_text
                        meta["knowledge_swarm_detail"] = swarm_detail
                        from agents.shared.recommendation import RecommendationEntry
                        recs = meta.get("recommendations", [])
                        recs.append(RecommendationEntry(
                            sender="Knowledge", keywords=swarm_text).to_dict())
                        meta["recommendations"] = recs

        if route.id == routing.REVISE.id:
            # This route carries the ORIGINAL REQUEST as its payload
            # (see routing.py), because Intent's prompt renders the payload
            # as "what the human said". The router's instruction rides in
            # meta instead.
            meta["router_instruction"] = routing.template_content(envelope, route)
            if state is not None:
                # What Security actually said, kept distinct from
                # Analytics' `concern` so Intent's prompt attributes each
                # correctly.
                if state.security_concern:
                    meta["security_concern"] = state.security_concern
                meta["revision_passes"] = state.revision_passes
                # Re-derived from Governance's own state, not trusted to
                # have survived Security's echo (which no longer carries
                # it — see _SECURITY_ROUTES above). By the time Security
                # reds something Intent already has every analytical read
                # of the event; this is what keeps that true.
                recommendations = state.recommendations()
                if recommendations:
                    meta["recommendations"] = recommendations

        if route.id == routing.BLOCKED.id:
            meta.update(self._blocked_meta(state))

        # Severity: inherited, never revised (§3). The ONE exception is
        # the bundle, and it is not Governance forming an opinion — it is
        # Governance refusing to lose one. The four parallel answers each
        # carry their own copy's tag; the bundle carries the highest, so
        # an escalation on any one of them survives being merged. See
        # EventState.severity.
        severity = None
        if route.id == routing.BUNDLE.id and state is not None:
            severity = state.severity

        if route.id == routing.BUNDLE.id:
            # Consolidator rides the same bundle Intent gets (2026-08-29):
            # the raw sensory content plus whatever the swarm already
            # retrieved as relevant to this event (meta["knowledge_swarm"]
            # / meta["knowledge_swarm_detail"]). It used to see the raw
            # Sensory envelope alone, fed by Sensory's own fan-out
            # (agents/sensory/agent.py's FAN_OUT) — moved here so it can
            # judge "does this match something already known" against the
            # SAME evidence Intent reasons over, instead of guessing at a
            # consistent subtopic/subject blind.
            #
            # Published BEFORE the Intent copy, not after: publish() on
            # this bus is synchronous and recursive (bus/pubsub.py), so
            # publishing to Intent first would run Intent's entire
            # reply-then-Security-then-Action subtree to completion before
            # control ever returned here — burying this hop deep in the
            # trace instead of right where the bundle was assembled.
            consolidator_meta = dict(meta)   # own copy — Envelope.reply
                                              # doesn't copy, and `meta` is
                                              # about to be reused below for
                                              # Intent's copy of the bundle
            if state is not None:
                # A ui_click's reference to the consolidation pass it's
                # about (docs/ideas/consolidation-doodle.md) — Intent has
                # no use for this, only Consolidator's dedup does.
                if state.source_type:
                    consolidator_meta["source_type"] = state.source_type
                if state.ref_event_id is not None:
                    consolidator_meta["ref_event_id"] = state.ref_event_id
            consolidator_out = envelope.reply(
                source="Governance",
                destination="Consolidator",
                type=route.type,
                content=decision.content,
                severity=severity,
                triggered_by=envelope.triggered_by,
                meta=consolidator_meta,
            )
            self.bus.publish("events.consolidator", consolidator_out)

        out = envelope.reply(
            source="Governance",
            destination=route.destination,
            type=route.type,
            content=decision.content,
            severity=severity,
            triggered_by=envelope.triggered_by,
            meta=meta,
        )
        self.bus.publish(route.topic, out)
        self.metrics["routed"] += 1

        if route.id == routing.BLOCKED.id:
            self._signal_frustration(out)
        return out

    # ---- The blocked incident (Daniel, 2026-08-24) ------------------------

    def _blocked_meta(self, state: Optional[EventState]) -> Dict[str, Any]:
        """A second red is an OUTCOME, not another loop.

        What reaches the human is deterministic and Governance-templated,
        because nothing here cleared Security and so nothing model-authored
        may be spoken. What makes it legible as more than an error message
        is the expression: Impulse's CURRENT appraisal state, read (never
        set) at this moment, so the face matches how the ecosystem
        actually feels rather than a canned sad emoji."""
        expression = "neutral"
        reader = getattr(self.impulse, "expression", None)
        if callable(reader):
            expression = reader()
        return {
            "expression": expression,
            "security_alert": True,
            "blocked": True,
            "revision_passes": state.revision_passes if state else 0,
            "security_concern": state.security_concern if state else "",
        }

    def _signal_frustration(self, envelope: Envelope) -> None:
        """Tell Impulse the exchange was blocked (control plane).

        A nudge, not a command: Impulse owns what a signal does to its own
        drive vectors, exactly as it owns what an event does. Governance
        publishes the fact and holds no reference to the result — same
        no-shared-mutable-state discipline as Consolidator's EpochWritten
        ping (v0.35g)."""
        self.bus.publish("system.control", Envelope(
            source="Governance", destination="Impulse", type="Frustration",
            content="an action was blocked twice and dropped",
            event_id=envelope.event_id,
        ))

    # ---- Concluding an event (v0.35g) -------------------------------------

    def _evict_stale(self, *, keep: str) -> None:
        """Drop the oldest in-flight entries if the buffer is growing.

        On a synchronous bus every event concludes inside the ingest()
        call that started it, so this should never fire — an entry that
        outlives its event means a worker isn't subscribed at all
        (a misconfiguration). The cap exists so that misconfiguration
        degrades into a counted diagnostic rather than into unbounded
        memory growth holding verbatim user content."""
        while len(self.buffer) > MAX_IN_FLIGHT_EVENTS:
            for event_id in self.buffer.in_flight:
                if event_id == keep:
                    continue
                self.buffer.release(event_id)
                self.metrics["incomplete"] += 1
                break
            else:                                     # pragma: no cover
                return

    def _conclude(self, state: EventState) -> None:
        """Action has run. Release the buffered bundle and forget the event.

        Consolidator doesn't wait on this any more (Phase 0.9 moved it to
        the BUNDLE fork, above) — but Reflection does (dispatch #4,
        2026-08-29): it needs the FINISHED arc (what was said, not just
        what was proposed), which only exists once Action has actually
        run, so this is its natural fork point, one event later than
        Consolidator's."""
        if self.buffer.peek(state.event_id) is None:
            # Already concluded — see the note at the call site. An Action
            # failure re-enters synchronously and concludes the event
            # before the outer frame returns.
            return
        self.bus.publish("events.reflection", Envelope(
            source="Governance", destination="Reflection", type="Concluded",
            content=state.sensory, severity=state.severity,
            event_id=state.event_id,
            meta={
                "final_proposal": state.final_proposal,
                "verdict": state.verdict or "green",
                "reflex_action": state.reflex_action,
            },
        ))
        self.buffer.release(state.event_id)
        self.metrics["concluded"] += 1


#: Retired alias. Phase 0 had a mock/real split for this role; v0.34
#: collapsed it to one deterministic implementation. Kept so an older
#: import doesn't break silently.
GovernanceMock = Governance
