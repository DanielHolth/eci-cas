"""
Phase 0.5 / v0.35b — the archive-lookup family (Personality, Knowledge).

The point of this suite is that there is no "Personality suite" and no
"Knowledge suite". They are one class, configured twice, so almost every
test here is parameterized over both roles — and a test that can't be
written that way is a sign the family has quietly become two things.

Mock-first per §13.1 (Daniel, 2026-08-24), so what's under test is the
SHAPE: the read-only posture, the shared keyword contract, the bounded
single-store read, the bundle slot, and failing toward silence. The
retrieval judgment itself arrives with the live tier.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from agents.archive_lookup import contract
from agents.archive_lookup.agent import ArchiveLookupMock
from agents.archive_lookup.base import (
    DEFAULT_BRIEFS,
    ROLE_STORES,
    ROLE_TOPICS,
    ArchiveLookupBase,
    _ReadOnlyArchive,
)
from agents.archive.store import ArchiveStore
from bus.envelope import Envelope
from bus.pubsub import BUSINESS_TOPICS, EmbeddedBus
from recovery.bootstrap import Recovery

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"

ROLES = ["Personality"]


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _manifest(tmp_path: Path, **overrides) -> Path:
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
    for key, value in overrides.items():
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


def _standalone(tmp_path, role: str, **kwargs):
    archive = ArchiveStore(root=str(tmp_path / "archive"))
    bus = EmbeddedBus(archive=archive)
    return ArchiveLookupMock(bus, archive, role=role, **kwargs), bus, archive


def _event(content: str = "who is Maria?") -> Envelope:
    return Envelope(source="Sensory", destination="Personality", type="prompt",
                    content=content)


# ---------------------------------------------------------------------------
# One class, two configurations
# ---------------------------------------------------------------------------

class TestTheFamilyIsOneClass:
    def test_both_roles_are_the_same_class(self, tmp_path):
        eco = _boot(tmp_path)
        assert isinstance(eco.personality, ArchiveLookupBase)

    def test_personality_has_correct_store_kind(self, tmp_path):
        eco = _boot(tmp_path)
        assert eco.personality.store_kind == "identity"

    def test_a_third_member_needs_no_new_class(self, tmp_path):
        """The family test. Adding a member is a configuration, not a
        subclass — if this ever needs a new file, the family has stopped
        being a family."""
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus(archive=archive)
        agent = ArchiveLookupMock(bus, archive, role="Whatever",
                                  store_kind="knowledge",
                                  topic="events.knowledge",
                                  brief="look up something else")
        assert agent.store_kind == "knowledge"

    def test_an_unknown_role_with_no_store_is_refused(self, tmp_path):
        archive = ArchiveStore(root=str(tmp_path / "archive"))
        bus = EmbeddedBus(archive=archive)
        with pytest.raises(ValueError, match="Unknown archive-lookup role"):
            ArchiveLookupMock(bus, archive, role="Nonsense")

    @pytest.mark.parametrize("role", ROLES)
    def test_every_declared_role_has_a_store_a_topic_and_a_brief(self, role):
        assert role in ROLE_STORES and role in ROLE_TOPICS
        assert DEFAULT_BRIEFS[role].strip()

    @pytest.mark.parametrize("role", ROLES)
    def test_each_topic_is_a_registered_business_topic(self, role):
        assert ROLE_TOPICS[role] in BUSINESS_TOPICS


# ---------------------------------------------------------------------------
# Read-only, by construction
# ---------------------------------------------------------------------------

class TestReadOnly:
    @pytest.mark.parametrize("role", ROLES)
    def test_the_agent_holds_no_write_surface_at_all(self, tmp_path, role):
        """Not a convention, not a docstring — the object the agent holds
        has `query` and nothing else, so there is no write method to call
        by accident."""
        agent, _, _ = _standalone(tmp_path, role)
        assert isinstance(agent.archive, _ReadOnlyArchive)
        for forbidden in ("write", "execute_writes", "log_event",
                          "set_drive_vectors"):
            assert not hasattr(agent.archive, forbidden)

    @pytest.mark.parametrize("role", ROLES)
    def test_a_full_event_writes_nothing_anywhere(self, tmp_path, role):
        agent, bus, archive = _standalone(tmp_path, role)
        before = {kind: len(archive.query(kind))
                  for kind in ("identity", "knowledge", "temp_log")}
        bus.publish(agent.topic, _event())
        after = {kind: len(archive.query(kind))
                 for kind in ("identity", "knowledge", "temp_log")}
        assert before == after

    def test_only_consolidator_gets_the_real_archive(self, tmp_path):
        eco = _boot(tmp_path)
        assert hasattr(eco.consolidator.archive, "write")
        assert not hasattr(eco.personality.archive, "write")


# ---------------------------------------------------------------------------
# The shared output contract
# ---------------------------------------------------------------------------

class TestContract:
    def test_a_well_formed_answer_parses(self):
        findings = contract.parse("mother: Maria, family, relationships")
        assert findings.findings == "mother: Maria, family, relationships"
        assert findings.relevant is True

    def test_empty_findings_are_never_marked_relevant(self):
        findings = contract.parse("")
        assert findings.relevant is False

    def test_an_unreadable_relevant_flag_defaults_to_silence(self):
        findings = contract.parse("NONE")
        assert findings.relevant is False
        assert findings.findings == ""

    def test_none_is_case_insensitive(self):
        findings = contract.parse("none")
        assert findings.relevant is False

    def test_the_fallback_is_silence_not_invention(self):
        findings = contract.fallback("scripted outage")
        assert findings.findings == ""
        assert findings.relevant is False
        assert findings.diagnostics["degraded"] is True

    def test_the_prompt_says_so_when_the_store_is_empty(self):
        prompt = contract.build_prompt("hello", [], brief="b")
        assert "(nothing recorded yet)" in prompt

    def test_the_prompt_carries_the_records_and_the_brief(self):
        prompt = contract.build_prompt("hello", ["mother: Maria"], brief="BRIEF")
        assert "BRIEF" in prompt and "Maria" in prompt


# ---------------------------------------------------------------------------
# Single-event scope
# ---------------------------------------------------------------------------

class TestSingleEventScope:
    @pytest.mark.parametrize("role", ROLES)
    def test_the_agent_keeps_no_cross_event_state(self, tmp_path, role):
        """No working window, no temp log, no persona — this family sees
        one event and forgets it (v0.35b)."""
        agent, bus, _ = _standalone(tmp_path, role)
        for i in range(3):
            bus.publish(agent.topic, _event(f"prompt {i}"))
        assert not hasattr(agent, "_history")
        assert not hasattr(agent, "_temp_log")
        assert not hasattr(agent, "persona")

    @pytest.mark.parametrize("role", ROLES)
    def test_the_read_is_bounded(self, tmp_path, role):
        seen = {}
        agent, bus, archive = _standalone(tmp_path, role, query_limit=3)

        original = archive.query

        def spy(kind, predicate=None, limit=None):
            seen["kind"], seen["limit"] = kind, limit
            return original(kind, predicate=predicate, limit=limit)

        archive.query = spy
        bus.publish(agent.topic, _event())
        assert seen["limit"] == 3
        assert seen["kind"] == ROLE_STORES[role]

    def test_each_agent_reads_only_its_own_store(self, tmp_path):
        eco = _boot(tmp_path)
        kinds = []
        original = eco.archive.query

        def spy(kind, *a, **kw):
            kinds.append(kind)
            return original(kind, *a, **kw)

        eco.archive.query = spy
        eco.bus.publish("events.personality", _event())
        assert set(kinds) == {"identity"}


# ---------------------------------------------------------------------------
# Emission — the bundle slot
# ---------------------------------------------------------------------------

class TestEmission:
    @pytest.mark.parametrize("role", ROLES)
    def test_findings_go_to_governance_never_to_intent(self, tmp_path, role):
        """These agents are one slot each in a bundle somebody else
        assembles (v0.35c). They never talk to Intent directly, and never
        to each other."""
        agent, bus, _ = _standalone(tmp_path, role)
        bus.publish(agent.topic, _event())
        out = [e for e in bus.trace() if e.source == role]
        assert len(out) == 1
        assert out[0].destination == "Governance"
        assert out[0].type == "Findings"

    @pytest.mark.parametrize("role", ROLES)
    def test_the_findings_ride_in_a_role_named_meta_slot(self, tmp_path, role):
        agent, bus, _ = _standalone(tmp_path, role)
        bus.publish(agent.topic, _event())
        out = [e for e in bus.trace() if e.source == role][0]
        slot = out.meta[role.lower()]
        assert set(slot) >= {"tier", "findings", "relevant", "decided_by"}

    @pytest.mark.parametrize("role", ROLES)
    def test_the_event_id_is_preserved_for_bundling(self, tmp_path, role):
        """Governance buffers the four parallel answers by event_id —
        a lookup that lost it could never be bundled."""
        agent, bus, _ = _standalone(tmp_path, role)
        event = _event()
        bus.publish(agent.topic, event)
        out = [e for e in bus.trace() if e.source == role][0]
        assert out.event_id == event.event_id

    @pytest.mark.parametrize("role", ROLES)
    def test_severity_is_inherited_never_raised(self, tmp_path, role):
        """A lookup reports what memory holds; it does not get to raise
        the alarm level of an event (§3's OR-upscale-only rule is for
        agents that assess, and this family doesn't)."""
        agent, bus, _ = _standalone(tmp_path, role)
        event = _event()
        event.severity = "Elevated"
        bus.publish(agent.topic, event)
        out = [e for e in bus.trace() if e.source == role][0]
        assert out.severity == "Elevated"


# ---------------------------------------------------------------------------
# The mock tier's honesty
# ---------------------------------------------------------------------------

class TestMockTier:
    @pytest.mark.parametrize("role", ROLES)
    def test_an_empty_store_answers_with_silence(self, tmp_path, role):
        agent, bus, _ = _standalone(tmp_path, role)
        bus.publish(agent.topic, _event())
        out = [e for e in bus.trace() if e.source == role][0]
        assert out.meta[role.lower()]["relevant"] is False

    def test_the_mock_never_claims_relevance_it_cannot_assess(self, tmp_path):
        """It reports that records exist, not that they matter — the
        distinction the live tier is actually for. Overstating it here
        would make the fan-out tests pass for the wrong reason."""
        eco = _boot(tmp_path)
        eco.bus.publish("events.personality", _event())   # identity is seeded
        out = [e for e in eco.bus.trace() if e.source == "Personality"][0]
        slot = out.meta["personality"]
        assert slot["records_seen"] >= 1
        assert slot["relevant"] is False

    def test_the_mock_costs_nothing(self, tmp_path):
        eco = _boot(tmp_path)
        eco.bus.publish("events.personality", _event())
        assert eco.personality.metrics["llm_calls"] == 0


# ---------------------------------------------------------------------------
# Bootstrap
# ---------------------------------------------------------------------------

class TestBootstrap:
    def test_both_roles_are_provisioned_and_subscribed(self, tmp_path):
        eco = _boot(tmp_path)
        assert eco.personality.tier == "mock"
        eco.bus.publish("events.personality", _event())
        assert eco.personality.metrics["events"] == 1

    def test_the_shipped_manifest_declares_them_live(self):
        """Phase 0.6 flipped these. Kept as an assertion rather than
        deleted: the v0.35b state (mock-first, live tier deferred) is
        exactly the kind of thing that quietly comes back."""
        with open(MANIFEST_PATH) as f:
            manifest = yaml.safe_load(f)
        assert manifest["roles"]["personality"]["mock"] is False


# ---------------------------------------------------------------------------
# Vendor independence (§10.2)
# ---------------------------------------------------------------------------

class TestVendorIndependence:
    def test_the_family_names_no_vendor_and_no_model(self):
        import agents.archive_lookup.base as base_mod
        import agents.archive_lookup.contract as contract_mod
        for module in (base_mod, contract_mod):
            source = Path(module.__file__).read_text().lower()
            for vendor in ("anthropic", "openai", "claude-", "gpt-", "llama"):
                assert vendor not in source, (
                    f"{module.__name__} names a vendor: {vendor}")
