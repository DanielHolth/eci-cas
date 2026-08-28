"""
The archive-lookup agent family (v0.35b) — one reusable class, not two
hand-copied implementations.

Personality and Knowledge are architecturally identical. Same shape, same
output contract, same never-write posture; they differ in exactly two
things — which Archive store they are pointed at, and the wording of
their brief. So they are two INSTANCES of this class, configured, rather
than two subclasses. Daniel flagged during the v0.35 design pass that
this is likely to become a family ("don't think Personality and Knowledge
are the only ones of this character"), and a third member should cost one
more instantiation, not one more file.

The shared contract
-------------------
  Read-only, by construction. Not by convention and not by docstring:
  the agent is handed a `_ReadOnlyArchive` view that exposes `query` and
  nothing else, so there is no write method on the object it holds to
  call by accident. Writing long-term memory is Consolidator's job and
  only Consolidator's (v0.35f/g, and §6's Memory Model before that).

  Single-event scope. No cross-event memory, no persona, no values, no
  conversation window. Purely "what does the archive say that bears on
  THIS event". That narrowness is what makes it cheap enough to run these
  in parallel with Impulse and Analytics on every event (v0.35a).

  Terse keyword output, in the one shared format
  (agents/archive_lookup/contract.py) — see that module on why the format
  is load-bearing rather than cosmetic.

Where this sits relative to Analytics
--------------------------------------
Analytics stays deliberately unique: it touches neither store this family
reads, keeps its own rolling working-queue window, and leans on its own
parametric ("worldly") knowledge while staying neutral. The dividing line
is worldly reasoning vs. local, archive-grounded retrieval — and as of
Daniel's 2026-08-24 call, Analytics is cut back further still: unbiased
analytical keywords for Intent's bundle, and nothing else.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus

from agents.archive_lookup import contract
from agents.archive_lookup.contract import Findings

#: role name -> (archive store, bus topic, default brief). Adding a
#: family member is an entry here plus a line in the manifest — no new
#: class, no new file.
ROLE_STORES: Dict[str, str] = {
    "Personality": "identity",
    "Knowledge": "knowledge",
}

ROLE_TOPICS: Dict[str, str] = {
    "Personality": "events.personality",
    "Knowledge": "events.knowledge",
}

#: Roles backed by StructuredStore instead of the legacy JSON ArchiveStore
#: (Phase 0.9). Personality's own record is tiny — one persona's worth of
#: facts, never a whole family's — so it never needs the swarm's path
#: fan-out or relevance ranking Knowledge uses; one fixed category/topic
#: slice is the entire contract. Consolidator writing to this slice is a
#: separate, not-yet-implemented change — this just gives it somewhere to
#: land.
STRUCTURED_LOOKUP_PATHS: Dict[str, Dict[str, str]] = {
    "Personality": {"kind": "identity", "category": "Personality", "topic": "profile 1"},
}

DEFAULT_BRIEFS: Dict[str, str] = {
    "Personality": (
        "You look up who this persona has been. Report anything in its "
        "recorded identity — stated values, boundaries, past conclusions "
        "about itself — that bears on the event in front of you: whether "
        "it is in character, whether it touches something already said."
    ),
    "Knowledge": (
        "You look up what this system has been told. Report anything in "
        "its recorded knowledge — people, places, facts, stories it has "
        "learned — that bears on the event in front of you. This is local "
        "memory, not world knowledge: report what the records say, not "
        "what you happen to know."
    ),
}


class _ReadOnlyArchive:
    """A query-only view of ArchiveStore.

    The read-only rule for this family is enforced here rather than
    trusted: an instance has `query` and nothing else, so there is no
    write surface on the object to reach for. Anything that wants to
    write long-term memory has to go and get the real ArchiveStore, which
    only Consolidator is handed."""

    __slots__ = ("_archive",)

    def __init__(self, archive):
        self._archive = archive

    def query(self, kind: str, predicate=None, limit: Optional[int] = None):
        if self._archive is None:
            return []
        return self._archive.query(kind, predicate=predicate, limit=limit)

    def __repr__(self) -> str:  # pragma: no cover - debug aid
        return "<_ReadOnlyArchive>"


class _ReadOnlyStructuredArchive:
    """Query-only view of StructuredStore, fixed to one category/topic
    slice (Phase 0.9). Same `query(kind, predicate=None, limit=None)`
    shape as `_ReadOnlyArchive` so `records()` below doesn't need to know
    which kind of store it's talking to; `predicate` is accepted and
    ignored since a fixed category/topic slice has no need for it."""

    __slots__ = ("_store", "_kind", "_category", "_topic")

    def __init__(self, store, kind: str, category: str, topic: str):
        self._store = store
        self._kind = kind
        self._category = category
        self._topic = topic

    def query(self, kind: str, predicate=None, limit: Optional[int] = None):
        if self._store is None:
            return []
        return self._store.query(self._kind, category=self._category,
                                  topic=self._topic, limit=limit)

    def __repr__(self) -> str:  # pragma: no cover - debug aid
        return f"<_ReadOnlyStructuredArchive {self._category}/{self._topic}>"


class ArchiveLookupBase:
    """Bus-facing half of one archive-grounded lookup agent.

    Subclass and implement look(), which turns one event plus a bounded
    slice of one Archive store into Findings."""

    tier = "base"

    def __init__(self, bus: EmbeddedBus, archive, *, role: str,
                 store_kind: Optional[str] = None,
                 topic: Optional[str] = None,
                 brief: str = "",
                 query_limit: int = contract.DEFAULT_QUERY_LIMIT,
                 structured_store=None):
        if role not in ROLE_STORES and store_kind is None:
            raise ValueError(
                f"Unknown archive-lookup role '{role}' and no store_kind given. "
                f"Known roles: {sorted(ROLE_STORES)}.")
        self.bus = bus
        self.role = role
        self.store_kind = store_kind or ROLE_STORES[role]
        self.topic = topic or ROLE_TOPICS.get(role, f"events.{role.lower()}")
        self.brief = brief or DEFAULT_BRIEFS.get(role, "")
        self.query_limit = int(query_limit)

        #: Read-only by construction — see _ReadOnlyArchive /
        #: _ReadOnlyStructuredArchive. A structured-lookup role only gets
        #: the structured view when a store was actually handed in;
        #: otherwise it falls back to the legacy archive like every other
        #: role (keeps this constructible offline, same as before).
        structured_path = STRUCTURED_LOOKUP_PATHS.get(role)
        if structured_path and structured_store is not None:
            self.archive = _ReadOnlyStructuredArchive(
                structured_store, structured_path["kind"],
                structured_path["category"], structured_path["topic"])
        else:
            self.archive = _ReadOnlyArchive(archive)

        self.metrics: Dict[str, int] = {
            "events": 0, "relevant": 0, "silent": 0,
            "llm_calls": 0, "fallbacks": 0,
        }
        self.bus.subscribe(self.topic, self.on_event)

    # ---- Archive access -----------------------------------------------------

    def records(self) -> List[Any]:
        """One bounded read of this agent's own store. Deliberately ONE
        query, not an iterative search: each round trip is a cost the
        flat-cost claim (§1) has to carry, and there is no evidence yet
        that more than one helps."""
        try:
            return self.archive.query(self.store_kind, limit=self.query_limit)
        except Exception:
            # Archive is the only door to memory (§5.8), but a lookup
            # should not take down the event because a read failed.
            return []

    # ---- Business events ----------------------------------------------------

    def on_event(self, envelope: Envelope) -> None:
        self.metrics["events"] += 1
        findings = self.look(envelope, self.records())
        self.emit(envelope, findings)

    def look(self, envelope: Envelope, records: List[Any]) -> Findings:
        raise NotImplementedError

    # ---- Emission -----------------------------------------------------------

    def emit(self, envelope: Envelope, findings: Findings) -> Envelope:
        """Report to Governance, which buffers this alongside the other
        three parallel answers and bundles them for Intent (v0.35c).

        These agents never talk to Intent directly and never to each
        other: they are one slot each in a bundle somebody else
        assembles."""
        if findings.relevant:
            self.metrics["relevant"] += 1
        else:
            self.metrics["silent"] += 1
        if findings.decided_by == "fallback":
            self.metrics["fallbacks"] += 1

        meta: Dict[str, Any] = dict(envelope.meta)
        meta.pop("governance", None)          # not ours to forward
        meta[self.role.lower()] = {"tier": self.tier, **findings.to_meta()}

        out = envelope.reply(
            source=self.role,
            destination="Governance",
            type="Findings",
            content=findings.findings,
            triggered_by=envelope.triggered_by,
            meta=meta,
            # Severity deliberately omitted — inherited untouched (§3). A
            # lookup reports what memory holds; it does not get to raise
            # the alarm level of an event.
        )
        self.bus.publish("events.governance", out)
        return out


__all__ = ["ArchiveLookupBase", "ROLE_STORES", "ROLE_TOPICS",
           "STRUCTURED_LOOKUP_PATHS", "DEFAULT_BRIEFS"]
