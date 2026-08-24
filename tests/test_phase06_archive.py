"""
Phase 0.6 — Archive as an agent (§5.8).

Archive was the only one of the eleven roles with no presence on the bus.
The store was never the problem: `write` and `query` are §5.8's two
endpoints and they work. What was missing was a door that didn't require
the caller to hold the store, and any way at all to observe from outside
that something had been written to long-term memory.

The line this suite holds is that the door is an ADDITION. Every test
that would have passed before Phase 0.6 must still pass: the store is
unchanged, direct callers are unchanged, and Consolidator specifically is
still on the direct path (see agents/archive/agent.py's header for why
that is a decision rather than an omission).
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from agents.archive.agent import ArchiveAgent
from agents.archive.store import ArchiveStore
from bus.envelope import Envelope
from bus.pubsub import BUSINESS_TOPICS, EmbeddedBus
from recovery.bootstrap import Recovery

MANIFEST_PATH = Path(__file__).resolve().parents[1] / "manifests" / "ecosystem-manifest.yaml"


def build(tmp_path):
    store = ArchiveStore(root=str(tmp_path / "archive"))
    bus = EmbeddedBus(archive=store)
    receipts = []
    bus.subscribe("system.control", receipts.append)
    return ArchiveAgent(bus, store), receipts


def write_request(content, source="Analytics", type="Write"):
    return Envelope(source=source, destination="Archive", type=type,
                    content=content)


ONE_WRITE = {"store": "knowledge", "tag": "weather",
             "content": "Oslo is cold in August, apparently"}


# ---------------------------------------------------------------------------
# The bus door
# ---------------------------------------------------------------------------

class TestWritesOverTheBus:
    def test_a_write_request_reaches_the_store(self, tmp_path):
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive", write_request(ONE_WRITE))
        records = agent.query("knowledge")
        assert len(records) == 1
        assert records[0]["content"] == ONE_WRITE["content"]

    def test_several_instructions_in_one_request(self, tmp_path):
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive", write_request(
            [ONE_WRITE, {**ONE_WRITE, "content": "and windy"}]))
        assert len(agent.query("knowledge")) == 2

    def test_a_single_object_is_accepted_as_one_write(self, tmp_path):
        """Both shapes are natural things for a caller to send and neither
        is ambiguous."""
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive", write_request(ONE_WRITE))
        assert agent.metrics["executed"] == 1

    def test_prose_yields_no_write_rather_than_a_guessed_one(self, tmp_path):
        """Archive is the last place in this system that should be
        inferring intent."""
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive",
                          write_request("please remember that I like tea"))
        assert agent.metrics["executed"] == 0
        assert agent.query("knowledge") == []

    def test_an_unknown_store_is_dropped_never_rerouted(self, tmp_path):
        """A misfiled memory is worse than a lost one, and the count is
        what tells you the contract drifted."""
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive", write_request(
            {"store": "somewhere-else", "content": "x"}))
        assert agent.metrics["dropped"] == 1
        assert agent.query("knowledge") == []
        assert agent.query("identity") == []

    def test_a_non_write_envelope_is_ignored_and_counted(self, tmp_path):
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive",
                          write_request(ONE_WRITE, type="Query"))
        assert agent.metrics["ignored"] == 1
        assert agent.metrics["requests"] == 0

    def test_reads_stay_synchronous(self, tmp_path):
        """A request whose whole value is the answer needs a reply
        channel; inventing one here would duplicate the direct `query`
        every reader already has."""
        agent, _ = build(tmp_path)
        agent.write("knowledge", {"content": "already here"})
        agent.bus.publish("events.archive",
                          write_request(ONE_WRITE, type="Query"))
        assert len(agent.query("knowledge")) == 1


class TestReceipts:
    def test_a_completed_write_publishes_a_receipt(self, tmp_path):
        agent, receipts = build(tmp_path)
        agent.bus.publish("events.archive", write_request(ONE_WRITE))
        assert [r.type for r in receipts] == ["ArchiveWritten"]
        assert receipts[0].meta["executed"] == 1

    def test_a_receipt_is_published_even_when_everything_was_dropped(self, tmp_path):
        """An instruction that vanished silently is exactly the failure
        this agent exists to make visible."""
        agent, receipts = build(tmp_path)
        agent.bus.publish("events.archive", write_request(
            {"store": "nowhere", "content": "x"}))
        assert receipts[0].meta == {"executed": 0, "dropped": 1}

    def test_the_receipt_goes_back_to_the_requester(self, tmp_path):
        agent, receipts = build(tmp_path)
        agent.bus.publish("events.archive",
                          write_request(ONE_WRITE, source="Impulse"))
        assert receipts[0].destination == "Impulse"

    def test_the_receipt_keeps_the_event_id(self, tmp_path):
        agent, receipts = build(tmp_path)
        request = write_request(ONE_WRITE)
        agent.bus.publish("events.archive", request)
        assert receipts[0].event_id == request.event_id

    def test_receipts_are_control_plane_not_business_events(self, tmp_path):
        """Bookkeeping about the system, not a step in an event's life. It
        must not appear in the queue log as though memory had had a
        thought."""
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive", write_request(ONE_WRITE))
        logged = agent.query_queue()
        assert all(r["type"] != "ArchiveWritten" for r in logged)

    def test_the_write_topic_is_a_business_topic(self, tmp_path):
        """The REQUEST is business traffic and is logged; only the receipt
        is control-plane."""
        assert "events.archive" in BUSINESS_TOPICS


# ---------------------------------------------------------------------------
# The store is unchanged
# ---------------------------------------------------------------------------

class TestDelegation:
    def test_an_agent_handed_this_cannot_tell_it_from_the_store(self, tmp_path):
        agent, _ = build(tmp_path)
        agent.write("identity", {"kind": "note", "content": "a value"})
        assert agent.query("identity")[0]["content"] == "a value"

    def test_execute_writes_still_works_directly(self, tmp_path):
        """Consolidator's path. Untouched by design — it is the sole
        writer of long-term memory and it needs the counts back
        synchronously."""
        agent, receipts = build(tmp_path)
        result = agent.execute_writes([ONE_WRITE])
        assert result == {"executed": 1, "dropped": 0}
        # And it publishes nothing: the direct path is not the bus path.
        assert receipts == []

    def test_drive_vectors_round_trip(self, tmp_path):
        agent, _ = build(tmp_path)
        agent.set_drive_vectors({"curiosity": 0.7})
        assert agent.get_drive_vectors() == {"curiosity": 0.7}

    def test_it_still_logs_bus_hops(self, tmp_path):
        agent, _ = build(tmp_path)
        agent.bus.publish("events.archive", write_request(ONE_WRITE))
        assert agent.query_queue()

    def test_it_exposes_the_root(self, tmp_path):
        agent, _ = build(tmp_path)
        assert str(tmp_path) in str(agent.root)


# ---------------------------------------------------------------------------
# Bootstrap
# ---------------------------------------------------------------------------

class TestBootstrap:
    def _manifest(self, tmp_path, **archive_role):
        manifest = yaml.safe_load(MANIFEST_PATH.read_text())
        manifest["storage"]["root"] = str(tmp_path / "archive")
        manifest["budget_tier"] = "custom"
        for role in ("analytics", "intent", "consolidator",
                     "personality", "knowledge"):
            manifest["roles"][role]["mock"] = True
        if archive_role:
            manifest["roles"]["archive"] = {"tier": "deterministic",
                                            **archive_role}
        path = tmp_path / "manifest.yaml"
        path.write_text(yaml.safe_dump(manifest))
        return str(path)

    def test_the_shipped_manifest_gives_archive_a_bus_door(self, tmp_path):
        eco = Recovery(self._manifest(tmp_path)).bootstrap()
        assert isinstance(eco.archive_agent, ArchiveAgent)

    def test_the_store_is_still_the_store(self, tmp_path):
        """Roles are handed the store exactly as before. Adopting the door
        must be a non-event for existing callers."""
        eco = Recovery(self._manifest(tmp_path)).bootstrap()
        assert isinstance(eco.archive, ArchiveStore)
        assert eco.archive_agent.store is eco.archive

    def test_mock_true_means_no_bus_door_not_no_memory(self, tmp_path):
        """The store is constructed in step 2 either way — mocking this
        role returns to the pre-0.6 state, it does not disable memory."""
        eco = Recovery(self._manifest(tmp_path, mock=True)).bootstrap()
        assert eco.archive_agent is None
        eco.archive.write("knowledge", {"content": "still works"})
        assert eco.archive.query("knowledge")

    def test_writes_over_the_bus_work_end_to_end(self, tmp_path):
        eco = Recovery(self._manifest(tmp_path)).bootstrap()
        eco.bus.publish("events.archive", write_request(ONE_WRITE))
        assert eco.archive.query("knowledge")

    def test_consolidator_still_writes_directly(self, tmp_path):
        """The one caller deliberately not migrated. Stated as a test so
        that 'move everything onto the bus' is a decision somebody has to
        make on purpose."""
        eco = Recovery(self._manifest(tmp_path)).bootstrap()
        assert eco.consolidator.archive is eco.archive
