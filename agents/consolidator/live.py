"""
Consolidator — LIVE tier (v0.9).

Per-event fact extraction. Same discipline as every other live tier in
this codebase: a validated response contract, a deterministic fallback,
budget-mode awareness, and attribution fields on the result.

One thing carried over from v0.35g: a reasoning pass may emit MULTIPLE
write instructions, each fully specifying its own destination (store +
tag), so Archive has nothing left to decide, only to execute.

Fallback posture: fail-OPEN, and that is not an oversight. Consolidator
gates nothing — no action waits on it, nothing reaching the human passes
through it. An unusable answer means one event's facts are lost, which
is recoverable state loss, not a safety event. The fail-CLOSED asymmetry
in this system belongs to the hops that gate: Analytics' Evaluate-
adjacent judgments, and Security/Governance's routing.
"""
from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple

from bus.envelope import Envelope
from bus.pubsub import EmbeddedBus
from substrates.base import Substrate, SubstrateError
from substrates.parsing import extract_json_object

from agents.consolidator.base import ConsolidationResult, ConsolidatorBase
from agents.shared.substrate_call import record_budget_failure, timed_complete

DEFAULT_SYSTEM_INSTRUCTION = (
    "You are CONSOLIDATOR, the memory-writing agent of a multi-agent "
    "system. You extract facts from the user's own words and store them. "
    "ONLY write facts the user explicitly stated in THIS event. Never "
    "infer, embellish, or guess details that were not in the input. If "
    "the user said 'I married Yahnessa' do NOT invent a location or date "
    "they did not mention. You never speak to the human and you never "
    "gate anything."
)

#: The response contract. Code-fixed rather than manifest-only, for the
#: same reason Analytics' and Intent's are: it must survive an operator
#: blanking `system_instruction`.
#:
#: Deliberately has no special-cased categories, topics, or named entities
#: (2026-08-29) — an earlier version hand-listed rules for this system's
#: own agents, which fixed consistency for that one closed set and did
#: nothing for the open-ended one (a user's family, job, hobbies — never
#: enumerable in advance). The general mechanism is rule 3: Consolidator
#: now runs on Governance's bundle (agents/governance/agent.py's BUNDLE
#: fork) instead of the raw Sensory event, so ALREADY KNOWN below is the
#: same evidence Intent reasons over — reuse what's already there when
#: this event's fact matches it, invent freely when it doesn't.
CONSOLIDATION_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"writes": [{"category": "<broad domain>",
               "topic": "<grouping within domain>",
               "subtopic": "<relation, role, or type>",
               "subject": "<the entity's own name, or a short description if unnamed>",
               "key": "<attribute name>",
               "value": "<bare datum>"}]}

RULES:
1. Only write facts explicitly stated in THIS event — never infer,
   embellish, or guess. A message can state a fact and ask a question in
   the same breath; write the fact, the question part is not one.
2. category is the one field that stays a small, reused set (person,
   place, event, system...) — reuse one from ALREADY KNOWN below when it
   fits, invent a new one only when nothing does. topic, subtopic,
   subject and key are NOT a fixed list: invent whatever this event
   actually calls for.
3. Check ALREADY KNOWN below first — it's what the knowledge swarm already
   retrieved as relevant to this event, one entry per line, shaped
   "category/topic/subtopic[/subject]: key = value". If this event's fact
   is about something already listed there, reuse that EXACT subtopic and
   subject spelling (copied from the path, not reworded) so its facts
   stay together under one identity instead of fragmenting under a
   near-duplicate. If nothing there matches ALREADY KNOWN either, check
   the OTHER writes you are producing in this SAME response before
   inventing anything: the same rule applies to yourself mid-answer as to
   the swarm — the second write about an entity reuses the first write's
   exact subtopic and subject, it does not reword them. Only once neither
   ALREADY KNOWN nor your own other writes have it, invent freely.
4. subtopic is the entity's stable relation/role/type ("son", "wife",
   "agent", "rule"), never a proper name — it should stay the same across
   everyone who shares it. subject is the entity's own proper name once
   known, and never blank: no name yet doesn't mean nothing to say —
   describe what it IS ("the Friday meeting", "the car", "this rule")
   rather than leaving it empty. Only fall back to "this" when even a
   short description would be a guess. Never put the name in subtopic
   instead.
5. subtopic and subject are labels to reuse, not prose to compose — keep
   each as short as it can be while still identifying the thing.  If an
   entity has both a full name and a short form (an acronym, a first
   name), pick the one form you expect to reuse most and use ONLY that
   one for every write about it in this response — never the full name in
   one write and the short form in another for the same entity.
6. value is the bare datum — a name, a date, a place — never a sentence.
   One fact per write.
7. Writing again under the same category/topic/subtopic/subject/key
   overwrites the old value — updating a fact is just writing it again.
8. writes may be empty if nothing worth storing was said.
"""


class ConsolidatorAgent(ConsolidatorBase):
    """Substrate-backed Consolidator. Drop-in replacement for ConsolidatorMock."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, substrate: Substrate, archive=None, *,
                 system_instruction: str = "",
                 # 0.0, not 0.3 (2026-08-29, Daniel): this is a
                 # fact-extraction judgment call, not creative writing —
                 # the SAME event should reliably produce the SAME
                 # write-or-don't-write decision. A live trace showed two
                 # near-identically-shaped prompts ("X agent, the one that
                 # does Y") land on opposite decisions; temperature was
                 # adding variance to a call that gains nothing from it.
                 temperature: float = 0.0,
                 max_tokens: Optional[int] = None,
                 strict: bool = False,
                 budget=None,
                 structured_store=None):
        self.substrate = substrate
        self.budget = budget
        self.system_instruction = (system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        self.strict = bool(strict)
        super().__init__(bus, archive, structured_store=structured_store)

    # ---- The pass ----------------------------------------------------------

    def write(self, envelope: Envelope) -> ConsolidationResult:
        if self.budget is not None and not self.budget.should_call_substrate():
            return ConsolidationResult(
                decided_by="fallback",
                diagnostics={"degraded": True,
                             "reason": "budget mode",
                             "source_substrate": "none (budget mode)",
                             "source_model": "none (budget mode)"})

        system = (self.system_instruction + "\n" + CONSOLIDATION_RESPONSE_CONTRACT).strip()
        user = self._prompt(envelope)

        try:
            text, latency_ms, usage = timed_complete(
                self.substrate, self.metrics,
                system=system, user=user, temperature=self.temperature,
                max_tokens=self.max_tokens, prefill="{",
            )
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            writes, dropped = self._parse(text)
            return ConsolidationResult(
                writes=writes, decided_by="llm",
                diagnostics={
                    "source_substrate": self.substrate.substrate_class,
                    "source_model": self.substrate.model,
                    "provider": self.substrate.provider_name,
                    "latency_ms": latency_ms, "usage": usage,
                    "est_cost_usd": cost or None,
                    "writes_rejected": dropped or None,
                },
            )
        except (SubstrateError, ValueError) as exc:
            record_budget_failure(exc, self.budget)
            if self.strict:
                raise
            return ConsolidationResult(
                decided_by="fallback",
                diagnostics={"degraded": True,
                             "reason": f"{type(exc).__name__}: {exc}"[:200],
                             "source_substrate": "none (degraded)",
                             "source_model": "none (degraded)"})

    # ---- Prompt -------------------------------------------------------------

    def _prompt(self, envelope: Envelope) -> str:
        """Just this event, rendered. Deliberately ONE event, not a batch —
        each write is a self-contained fact extraction over what the user
        just said, nothing else.

        ALREADY KNOWN comes from meta["knowledge_swarm"] — the same
        per-event, relevance-bounded retrieval Governance already ran for
        Intent (agents/governance/knowledge_swarm.py), forked to this
        envelope rather than re-derived. It costs nothing extra to
        compute and doesn't grow with the size of the knowledge base, only
        with how much is actually relevant to this one event."""
        lines = [f"EVENT: {envelope.event_id}", ""]
        lines.append(f"input: {envelope.content}")

        lines.append("")
        swarm = str(envelope.meta.get("knowledge_swarm") or "").strip()
        lines.append("ALREADY KNOWN, relevant to this event:")
        lines.append(f"  {swarm}" if swarm else "  (nothing on file yet)")

        return "\n".join(lines)

    # ---- Parsing ------------------------------------------------------------

    def _parse(self, text: str) -> Tuple[List[Dict[str, Any]], int]:
        obj = extract_json_object(text)
        if obj is None:
            raise ValueError(f"no JSON object in consolidation response: {text[:200]!r}")

        # Write instructions. Structural validation only — an unknown
        # store is dropped and counted rather than guessed at, the same
        # clamp-at-the-boundary posture used everywhere else in this
        # codebase. WHERE something belongs is the reasoner's judgment;
        # whether the answer is well-formed is ours.
        writes: List[Dict[str, Any]] = []
        dropped = 0
        raw_writes = obj.get("writes")
        if isinstance(raw_writes, list):
            for w in raw_writes[:20]:
                if not isinstance(w, dict):
                    dropped += 1
                    continue
                category = str(w.get("category") or "").strip()
                topic = str(w.get("topic") or "").strip()
                key = str(w.get("key") or "").strip()
                value = str(w.get("value") or "").strip()
                if not category or not topic or not key or not value:
                    dropped += 1
                    continue
                writes.append({
                    "category": category[:80],
                    "topic": topic[:80],
                    "subtopic": str(w.get("subtopic") or "general")[:80],
                    # Forced non-empty (2026-08-29): a blank subject reads
                    # ambiguously downstream (the swarm's own path format
                    # drops it entirely — see knowledge_swarm.py's
                    # format_for_intent), so an entity too generic to name
                    # still gets an explicit placeholder rather than
                    # silence a reader could mistake for "no entity here".
                    "subject": str(w.get("subject") or "").strip()[:80] or "this",
                    "key": key[:120],
                    "value": value[:1000],
                })

        return writes, dropped


__all__ = ["ConsolidatorAgent", "DEFAULT_SYSTEM_INSTRUCTION",
           "CONSOLIDATION_RESPONSE_CONTRACT"]
