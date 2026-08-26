"""
Phase 0.5 / v0.35a-d — the parallel fan-out and Governance's bundling.

The topology break. Sensory stops relaying to Impulse alone and fans out
to four agents at once with no Governance hop; Governance buffers all
four answers and sends Intent one bundle; the Critical reflex skips
cognition on the way in; and once Action has run, Governance hands
Consolidator one complete record of the event and forgets it.

What this suite is really guarding is the set of properties that are easy
to lose in a merge: that the fan-out is genuinely ungated, that four
answers to one event stay distinguishable from one answer to four, that
an escalation raised on one copy survives being merged with three that
weren't, and that Governance still holds nothing across events.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from agents.governance import routing
from agents.governance.buffer import DEFAULT_WORKERS, BundleBuffer
from bus.envelope import VERDICT_GREEN, Envelope
from recovery.bootstrap import Recovery

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"
PROMPT = "Hello there, are you awake?"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, **role_overrides) -> Path:
    with open(MANIFEST_PATH) as f:
        manifest = yaml.safe_load(f)
    manifest["storage"]["root"] = str(tmp_path / "archive")
    manifest["budget_tier"] = "custom"
    # Phase 0.6 gave the archive-lookup family a live tier, so the shipped
    # manifest declares personality/knowledge real too. Mocked here for the
    # same reason the others are: these tests must run with no credentials.
    for role in ("analytics", "intent", "consolidator",
                 "personality"):
        manifest["roles"][role]["mock"] = True
    manifest["roles"]["consolidator"]["synchronous"] = True
    for key, value in role_overrides.items():
        manifest["roles"][key].update(value)
    tmp_path.mkdir(parents=True, exist_ok=True)
    out = tmp_path / "ecosystem-manifest.yaml"
    with open(out, "w") as f:
        yaml.safe_dump(manifest, f)
    return out


def _boot(tmp_path: Path, **overrides):
    eco = Recovery(str(_manifest(tmp_path, **overrides))).bootstrap()
    eco.bus.reset_trace()
    return eco


def _hops(eco, event_id):
    return [(e.source, e.destination, e.type) for e in eco.bus.trace()
            if e.event_id == event_id]


# ---------------------------------------------------------------------------
# v0.35a — the fan-out
# ---------------------------------------------------------------------------

class TestFanOut:
    def test_one_ingest_produces_four_copies(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        out = [e for e in eco.bus.trace()
               if e.event_id == event_id and e.source == "Sensory"]
        # Phase 0.8: Knowledge removed (swarm replaces it). Impulse is
        # still first and synchronous; the other two's order isn't guaranteed.
        destinations = [e.destination for e in out]
        assert destinations[0] == "Impulse"
        assert set(destinations[1:]) == {"Analytics", "Personality"}
        assert len(destinations) == 3

    def test_every_copy_carries_the_same_event_id_and_content(self, tmp_path):
        """Four answers to one event have to stay distinguishable from one
        answer to four — the event_id is the only thing that does that."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        out = [e for e in eco.bus.trace()
               if e.event_id == event_id and e.source == "Sensory"]
        assert {e.event_id for e in out} == {event_id}
        assert {str(e.content) for e in out} == {PROMPT}

    def test_the_fan_out_has_no_governance_hop(self, tmp_path):
        """The one deliberate exception to universal routing (v0.35a/c).
        Nothing from Sensory touches Governance on the way in."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        from_sensory = [e for e in eco.bus.trace()
                        if e.event_id == event_id and e.source == "Sensory"]
        assert all(e.destination != "Governance" for e in from_sensory)

    def test_impulse_is_published_to_first(self, tmp_path):
        """Deliberate ordering: Impulse is the only agent that can open the
        Critical fast path, so on a synchronous bus its reflex is already
        on the wire before the other three are dispatched at all."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        first = [e for e in eco.bus.trace()
                 if e.event_id == event_id and e.source == "Sensory"][0]
        assert first.destination == "Impulse"

    def test_all_four_workers_actually_answer(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        answered = {e.source for e in eco.bus.trace()
                    if e.event_id == event_id and e.destination == "Governance"
                    and e.source in DEFAULT_WORKERS}
        assert answered == set(DEFAULT_WORKERS)


# ---------------------------------------------------------------------------
# v0.35c — buffering and bundling
# ---------------------------------------------------------------------------

class TestBundling:
    def test_the_bundle_fires_once_on_the_fourth_answer(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        bundles = [e for e in eco.bus.trace()
                   if e.event_id == event_id and e.type == "Bundle"]
        assert len(bundles) == 1
        assert eco.governance.metrics["held"] == 2      # the first two waited

    def test_the_bundle_carries_all_three_recommendations_plus_the_reflex(self, tmp_path):
        """v0.35c bundled all four slots as named blocks. Daniel
        (2026-08-24) asked for that trimmed to what Intent actually needs:
        the three analytical answers as one array, sender-tagged — no
        per-worker tier/diagnostics riding along — plus Impulse's felt
        reaction, which was never a recommendation and keeps its own slot."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        bundle = [e for e in eco.bus.trace()
                  if e.event_id == event_id and e.type == "Bundle"][0]
        senders = {entry["sender"] for entry in bundle.meta["recommendations"]}
        # Analytics always has something to say; Personality/Knowledge may
        # be silent on a fresh archive (empty findings are omitted, not
        # sent as noisy empty entries — see EventState.recommendations()).
        assert "Analytics" in senders
        assert senders <= {"Analytics", "Personality"}
        for entry in bundle.meta["recommendations"]:
            # 2026-08-25: proceed/concern removed entirely — sender and
            # keywords are the whole shape now. Nothing left to weigh,
            # nothing left to gate.
            assert set(entry) == {"sender", "keywords"}
        assert "reflex" in bundle.meta

    def test_the_bundle_payload_is_the_human_verbatim(self, tmp_path):
        """Intent must see what was actually said, never a worker's
        restatement of it — the answers ride in meta."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        bundle = [e for e in eco.bus.trace()
                  if e.event_id == event_id and e.type == "Bundle"][0]
        assert str(bundle.content) == PROMPT

    def test_the_bundle_carries_no_gate_at_all(self, tmp_path):
        """2026-08-25 (Daniel): proceed/concern removed from the bundle
        entirely — Analytics held no real veto after v0.35e moved gating
        to Security/Intent, but Governance's own routing code was still
        reading `analytics.get("proceed")` to fork Intent's ADVISE/REFUSE
        register. That was a real, live gate riding on an agent that was
        supposed to be "as dumb as Personality and Knowledge." The only
        gate left in this system is Security's red verdict."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        bundle = [e for e in eco.bus.trace()
                  if e.event_id == event_id and e.type == "Bundle"][0]
        assert "proceed" not in bundle.meta
        assert "concern" not in bundle.meta
        analytics_entry = next(e for e in bundle.meta["recommendations"]
                               if e["sender"] == "Analytics")
        assert "proceed" not in analytics_entry
        assert "concern" not in analytics_entry

    def test_intent_voices_exactly_once_per_event(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        spoken = [e for e in eco.bus.trace()
                  if e.event_id == event_id and e.source == "Intent"]
        assert len(spoken) == 1

    def test_security_never_sees_the_bundle_or_intents_diagnostics(self, tmp_path):
        """Security decides 'is this against the rules' from the proposed
        action and severity alone (§5.6) — it has no business seeing
        Analytics' recommendation, Personality's/Knowledge's findings, or
        Intent's own tier/decided_by diagnostics (Daniel, 2026-08-24)."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        to_security = [e for e in eco.bus.trace()
                       if e.event_id == event_id and e.destination == "Security"]
        assert to_security
        for envelope in to_security:
            noisy_keys = {"recommendations", "bundle", "analytics", "personality",
                         "intent", "declined", "reflex"}
            assert not (noisy_keys & set(envelope.meta))
            # What Security's own verdict-forming DOES need to survive the
            # trip so Action can eventually resolve content:
            assert set(envelope.meta) <= {"proposed_action", "governance"}

    def test_a_duplicate_report_after_bundling_is_dropped(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        before = eco.governance.metrics["bundles"]
        eco.bus.publish("events.governance", Envelope(
            source="Personality", destination="Governance", type="Findings",
            content="late", event_id=event_id, meta={"personality": {}}))
        assert eco.governance.metrics["bundles"] == before

    def test_an_escalation_on_one_copy_survives_the_merge(self, tmp_path):
        """The bug this nearly shipped with. Each worker replies to its own
        copy of the event, so the bundle is built from whichever answer
        arrived last — and if that one inherited "Neutral" while Impulse
        raised "Elevated", the raised tag would silently vanish. §3 says a
        tag may be raised by anyone and lowered by no one."""
        eco = _boot(tmp_path)
        eco.impulse.vectors["urgency"] = 0.95
        event_id = eco.sensory.ingest("something urgent", source_type="prompt")
        bundle = [e for e in eco.bus.trace()
                  if e.event_id == event_id and e.type == "Bundle"][0]
        assert bundle.severity == "Elevated"

    def test_governance_holds_nothing_once_an_event_concludes(self, tmp_path):
        """§5.1's per-event statutory reset, enforced by deletion rather
        than by discipline."""
        eco = _boot(tmp_path)
        for i in range(3):
            eco.sensory.ingest(f"prompt {i}", source_type="prompt")
        assert len(eco.governance.buffer) == 0
        assert eco.governance.metrics["concluded"] == 3

    def test_two_interleaved_events_never_mix_slots(self, tmp_path):
        """A buffer keyed on the wrong thing would bundle across events."""
        buffer = BundleBuffer()
        a, b = buffer.get("aaa"), buffer.get("bbb")
        a.slots["Impulse"] = {"reflex": "A"}
        b.slots["Impulse"] = {"reflex": "B"}
        assert buffer.get("aaa").slots["Impulse"]["reflex"] == "A"
        assert not a.ready() and not b.ready()

    def test_an_incomplete_bundle_holds_rather_than_routing(self, tmp_path):
        eco = _boot(tmp_path)
        eco.bus.publish("events.governance", Envelope(
            source="Analytics", destination="Governance", type="Recommend",
            content="partial", meta={"analytics": {"recommendation": "partial"}}))
        assert eco.governance.metrics["bundles"] == 0
        assert eco.governance.metrics["held"] == 1


# ---------------------------------------------------------------------------
# v0.35d — the Critical fast path
# ---------------------------------------------------------------------------

class TestCriticalReflex:
    def test_critical_goes_straight_to_security(self, tmp_path):
        """Named-but-deferred since v0.34, confirmed by v0.35d as routing
        THROUGH Governance rather than around it — it skips cognition, not
        the router."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                                      severity="Critical")
        hops = _hops(eco, event_id)
        assert ("Impulse", "Governance", "prompt") in hops
        assert any(dst == "Security" for _, dst, _ in hops)
        assert eco.governance.metrics["reflexes"] == 1

    def test_critical_still_completes_the_bundle_and_intent_voices_second(self, tmp_path):
        """v0.35 double-action (Daniel, 2026-08-24): a Critical no longer
        discards the other three fan-out answers. The reflex fires first
        as action #1 (spontaneous); the fan-out still completes behind it,
        Governance still bundles all four slots, and Intent still voices
        — as a second, considered action #2 that knows the reflex already
        happened."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                                      severity="Critical")
        hops = _hops(eco, event_id)
        assert [h for h in hops if h[2] == "Bundle"]
        assert [h for h in hops if h[0] == "Intent"]

    def test_the_other_three_answers_are_bundled_not_discarded(self, tmp_path):
        eco = _boot(tmp_path)
        eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                           severity="Critical")
        assert eco.governance.metrics["bundles"] == 1

    def test_exactly_one_reflex_and_one_bundle_conclude_the_event_once(self, tmp_path):
        """The reflex reaching Action does NOT conclude the event — only
        the second, bundle-derived action does. One Critical event should
        therefore produce exactly one reflex, one bundle, and one
        conclusion (not two)."""
        eco = _boot(tmp_path)
        eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                           severity="Critical")
        assert eco.governance.metrics["reflexes"] == 1
        assert eco.governance.metrics["bundles"] == 1
        assert eco.governance.metrics["concluded"] == 1

    def test_reflex_reaches_action_before_intents_second_action(self, tmp_path):
        """Two hops reach Action for one Critical event: the reflex first
        (spontaneous), Intent's bundled voicing second (considered)."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                                      severity="Critical")
        hops = _hops(eco, event_id)
        action_hops = [h for h in hops if h[1] == "Action"]
        assert len(action_hops) == 2
        # The reflex's Action hop has to come from Security (the fast
        # path), and it has to precede Intent's bundled voicing in the
        # trace, since Intent can't voice until the bundle is complete.
        intent_index = next(i for i, h in enumerate(hops) if h[0] == "Intent")
        first_action_index = next(i for i, h in enumerate(hops) if h[1] == "Action")
        assert first_action_index < intent_index

    def test_intent_is_told_the_reflex_already_acted(self, tmp_path):
        """Governance carries reflex_already_acted / reflex_action on the
        bundle envelope Intent receives, so a live substrate can
        acknowledge the double action instead of talking over its own
        reflex."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                                      severity="Critical")
        bundle_envelopes = [e for e in eco.bus.trace()
                            if e.event_id == event_id and e.destination == "Intent"]
        assert bundle_envelopes
        bundle_envelope = bundle_envelopes[0]
        assert bundle_envelope.meta.get("reflex_already_acted") is True
        assert bundle_envelope.meta.get("reflex_action")

    def test_a_critical_reflex_still_reaches_action_through_security(self, tmp_path):
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                                      severity="Critical")
        assert any(dst == "Action" for _, dst, _ in _hops(eco, event_id))

    def test_action_speaks_the_reflex_reaction_not_securitys_verdict_word(self, tmp_path):
        """Bug found 2026-08-24: the reflex's outbound-to-Security meta
        never carried proposed_action (Intent hasn't run yet on this fast
        path), so once Security cleared, SPEAK's content resolution fell
        through to Security's own reply content — the literal word
        "Green" — instead of the reflex's actual reaction. Every Critical
        event was making the persona say "Green" to the human."""
        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                                      severity="Critical")
        reflex_speech = [e for e in eco.bus.trace()
                         if e.event_id == event_id and e.source == "Impulse"][0].meta["reflex"]
        action_hops = [e for e in eco.bus.trace()
                       if e.event_id == event_id and e.destination == "Action"]
        assert action_hops
        first_action = action_hops[0]
        assert first_action.content == reflex_speech
        assert first_action.content not in ("Green", "green")

    def test_impulse_alone_can_never_open_the_fast_path(self, tmp_path):
        """The invariant the whole reflex depends on: drive-vector state,
        however extreme, is capped at Elevated. Only an external Sensory
        signal can say Critical."""
        eco = _boot(tmp_path)
        for vector in eco.impulse.vectors:
            eco.impulse.vectors[vector] = 1.0
        event_id = eco.sensory.ingest("nothing special", source_type="prompt")
        assert eco.governance.metrics["reflexes"] == 0
        assert [e for e in eco.bus.trace()
                if e.event_id == event_id and e.type == "Bundle"]

    def test_the_reflex_instruction_quotes_the_input_and_the_reaction(self):
        env = Envelope(source="Impulse", destination="Governance", type="prompt",
                       content="fire in the kitchen", severity="Critical",
                       meta={"reflex": "Terse, protective reaction."})
        decision = routing.decide(env)
        assert decision.route is routing.REFLEX
        assert "fire in the kitchen" in decision.content
        assert "Terse, protective reaction." in decision.content
        assert decision.diagnostics["critical_reflex"] is True


# ---------------------------------------------------------------------------
# v0.35g — the Consolidator hand-off
# ---------------------------------------------------------------------------

class TestConsolidatorHandOff:
    def test_the_record_arrives_only_after_action(self, tmp_path):
        seen = []

        eco = _boot(tmp_path)
        original = eco.consolidator.observe

        def spy(record):
            seen.append((record, len(eco.action.executed)))
            return original(record)

        eco.consolidator.observe = spy
        eco.governance.consolidator = eco.consolidator
        eco.sensory.ingest(PROMPT, source_type="prompt")

        assert len(seen) == 1
        _, actions_at_handoff = seen[0]
        assert actions_at_handoff == 1        # Action had already run

    def test_the_record_carries_exactly_the_settled_contents(self, tmp_path):
        eco = _boot(tmp_path)
        seen = []
        eco.consolidator.observe = lambda record: seen.append(record)
        eco.sensory.ingest(PROMPT, source_type="prompt")

        record = seen[0]
        assert set(record) == {"event_id", "sensory", "security", "intent_final"}
        assert record["sensory"] == PROMPT
        assert record["intent_final"]
        assert record["security"]["verdict"] == VERDICT_GREEN

    def test_a_critical_events_record_carries_the_reflex_action(self, tmp_path):
        """A reflex that actually acted on the world is not a redundant
        reading (unlike Impulse's ordinary reflex text) — it's something
        the persona did, so Consolidator's record carries it."""
        eco = _boot(tmp_path)
        seen = []
        eco.consolidator.observe = lambda record: seen.append(record)
        eco.sensory.ingest("fire in the kitchen", source_type="prompt",
                           severity="Critical")

        record = seen[0]
        assert "reflex_action" in record
        assert record["reflex_action"]

    def test_the_record_excludes_what_the_spec_says_it_excludes(self, tmp_path):
        """Impulse's reflex, Analytics' recommendation text and the two
        lookups' findings are all redundant for Consolidator's purposes —
        those agents only surface what Archive already holds, or stay
        neutral and never touch it."""
        eco = _boot(tmp_path)
        seen = []
        eco.consolidator.observe = lambda record: seen.append(record)
        eco.sensory.ingest(PROMPT, source_type="prompt")

        flat = str(seen[0])
        assert "reflex" not in flat
        assert "drive_vectors" not in flat
        assert "findings" not in flat

    def test_consolidator_hears_only_from_governance_never_mid_event(self, tmp_path):
        eco = _boot(tmp_path)
        calls = []
        eco.consolidator.observe = lambda record: calls.append(record)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        assert len(calls) == 1          # once, at the end — not per hop

    def test_intent_hands_over_nothing_itself(self, tmp_path):
        """v0.35f's first cut had Intent forward concluded events directly.
        Governance owns that now — and if both did it, every event would
        be consolidated twice."""
        eco = _boot(tmp_path)
        calls = []
        eco.consolidator.observe = lambda record: calls.append(record)
        eco.sensory.ingest(PROMPT, source_type="prompt")
        assert len(calls) == 1
        assert not hasattr(eco.intent, "hand_to_consolidator")


# ---------------------------------------------------------------------------
# Noise reduction (Daniel, 2026-08-24) — recommendations survive Security
# without Security ever seeing them
# ---------------------------------------------------------------------------

class TestRecommendationsSurviveSecurity:
    """Security's outbound meta was trimmed to just `proposed_action` (see
    TestBundling.test_security_never_sees_the_bundle_or_intents_diagnostics).
    That only works if Governance re-derives what Intent still needs for
    REVIEW/REVISE from its own per-event state, rather than trusting it to
    come back through Security's echo — this is the test that would catch
    a regression where recommendations quietly went missing on that path."""

    def test_a_yellow_verdict_still_carries_recommendations_to_intent(self, tmp_path, monkeypatch):
        from agents.security.agent import SecurityAgent, SecurityMock

        def always_yellow(self, envelope):
            meta = dict(envelope.meta)
            meta["verdict"] = "yellow"
            out = envelope.reply(source="Security", destination="Governance",
                                 type="Verdict", content="Yellow", meta=meta)
            self.bus.publish("events.governance", out)

        # Both tiers: the shipped manifest runs Security real as of Phase
        # 0.6, and this test is about what Governance does with a yellow —
        # not about which rule produced one.
        monkeypatch.setattr(SecurityMock, "on_event", always_yellow)
        monkeypatch.setattr(SecurityAgent, "on_event", always_yellow)

        eco = _boot(tmp_path)
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")
        reviews = [e for e in eco.bus.trace()
                  if e.event_id == event_id and e.type == "Review"]
        assert reviews
        recommendations = reviews[0].meta.get("recommendations")
        assert recommendations
        assert any(r["sender"] == "Analytics" for r in recommendations)


# ---------------------------------------------------------------------------
# v0.35 double-action — Intent's prompt carries the reflex acknowledgment
# ---------------------------------------------------------------------------

class TestDoubleActionPrompt:
    """Wiring check for the gap the phase 0.5 handover flagged: Governance
    computing reflex_already_acted / reflex_action is inert unless Intent's
    prompt actually surfaces it to the model."""

    def test_advise_prompt_includes_the_double_action_note_when_flagged(self):
        from bus.envelope import Envelope as _Envelope
        from agents.intent import contract as intent_contract

        env = _Envelope(source="Governance", destination="Intent", type="Bundle",
                        content="fire in the kitchen")
        persona = intent_contract.PersonaState(anchors=intent_contract.DEFAULT_CORE_ANCHORS)
        prompt = intent_contract.build_prompt(
            intent_contract.Task.ADVISE, env, persona,
            recommendation="proceed, warm reply appropriate",
            reflex_already_acted=True,
            reflex_action="Terse, protective reaction: stepped in immediately.",
        )
        assert "already acted" in prompt.lower()
        assert "stepped in immediately" in prompt

    def test_advise_prompt_omits_the_note_when_not_flagged(self):
        from bus.envelope import Envelope as _Envelope
        from agents.intent import contract as intent_contract

        env = _Envelope(source="Governance", destination="Intent", type="Bundle",
                        content="hello there")
        persona = intent_contract.PersonaState(anchors=intent_contract.DEFAULT_CORE_ANCHORS)
        prompt = intent_contract.build_prompt(
            intent_contract.Task.ADVISE, env, persona,
            recommendation="proceed, warm reply appropriate",
        )
        assert "already acted" not in prompt.lower()


# ---------------------------------------------------------------------------
# Hardening — defects found by an adversarial review pass, 2026-08-24
# ---------------------------------------------------------------------------

class TestBufferDoesNotLeak:
    """Governance's per-event buffer used to mint an entry for EVERY
    envelope it saw, including ones it was about to drop — and released
    only on the path to Action. In a 24/7 process that is unbounded
    growth holding verbatim user content, and it breaks §5.1's "no state
    across events" outright."""

    def test_an_unroutable_envelope_creates_no_entry(self, tmp_path):
        eco = _boot(tmp_path)
        for i in range(50):
            eco.bus.publish("events.governance", Envelope(
                source="Nobody", destination="Governance", type="Whatever",
                content=f"junk {i}"))
        assert eco.governance.metrics["dropped"] == 50
        assert len(eco.governance.buffer) == 0

    def test_a_missing_worker_degrades_to_a_counted_diagnostic(self, tmp_path):
        """If a worker isn't subscribed at all, its events can never
        bundle. That is a misconfiguration — it must degrade into a
        counted, bounded diagnostic rather than into a memory leak."""
        from agents.governance.agent import MAX_IN_FLIGHT_EVENTS
        eco = _boot(tmp_path)
        eco.bus._subscribers["events.personality"] = []      # Personality goes dark

        for i in range(MAX_IN_FLIGHT_EVENTS + 40):
            eco.sensory.ingest(f"prompt {i}", source_type="prompt")

        assert eco.governance.metrics["concluded"] == 0    # nothing completes
        assert len(eco.governance.buffer) <= MAX_IN_FLIGHT_EVENTS
        assert eco.governance.metrics["incomplete"] > 0

    def test_a_normal_run_never_evicts_anything(self, tmp_path):
        eco = _boot(tmp_path)
        for i in range(30):
            eco.sensory.ingest(f"prompt {i}", source_type="prompt")
        assert eco.governance.metrics["incomplete"] == 0
        assert len(eco.governance.buffer) == 0


class TestConcludeIsIdempotent:
    def test_an_action_failure_does_not_consolidate_the_event_twice(self, tmp_path):
        """emit() publishes synchronously, so a failing Action re-enters
        Governance and concludes the event from inside the frame that was
        about to conclude it. Without idempotence, long-term memory
        double-counted the event and the batch threshold tripped early."""
        eco = _boot(tmp_path)
        seen = []
        eco.consolidator.observe = lambda record: seen.append(record)

        eco.action.force_next_failures = 1
        event_id = eco.sensory.ingest(PROMPT, source_type="prompt")

        assert len(seen) == 1
        assert seen[0]["event_id"] == event_id
        assert eco.governance.metrics["concluded"] == 1
