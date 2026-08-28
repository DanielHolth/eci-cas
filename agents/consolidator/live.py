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
CONSOLIDATION_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"writes": [{"category": "<broad domain>",
               "topic": "<grouping within domain>",
               "subtopic": "<relation or role>",
               "subject": "<the entity's own name, or \\"\\" if unknown>",
               "key": "<attribute name>",
               "value": "<bare datum>"}]}

WRITE RULES — follow exactly:
1. ONLY write facts the user explicitly stated. Questions are NOT facts —
   but a message can state facts AND ask a question in the same breath
   ("Here are the three rules: X, Y, Z. Any questions about them?"). Write
   the stated facts; only the question part itself is not a fact.
2. category and topic are SINGLE words or short phrases, NEVER paths with
   slashes. category = broad domain (person, place, event), topic =
   grouping (family, relationship, biography).
3. subtopic is the RELATION/ROLE — "son", "daughter", "mother", "wife",
   "dog" — never a proper name. It stays the same across everyone who
   shares that role (two children are both subtopic="son" if both are
   sons), so a query like "my kids" can find them without already
   knowing their names.
4. subject is the entity's own proper name once known ("Marcus",
   "Yahnessa") — "" if the user hasn't given one yet. This is what keeps
   two people with the same subtopic from colliding, and what a single
   person's facts all hang off consistently across calls. Never put the
   name in subtopic, and never leave subject blank once you've been told it.
   Multiple keys hang off the same (subtopic, subject) pair:
     category="person", topic="relationship", subtopic="wife", subject="Yahnessa", key="marriage_date", value="07.03.2004"
     category="person", topic="family", subtopic="son", subject="Marcus", key="birth_year", value="2007"
     category="person", topic="family", subtopic="son", subject="Elias", key="birth_year", value="2011"
     category="person", topic="family", subtopic="mother", subject="Maria", key="occupation", value="nurse"
5. value must be the bare datum — a name, a date, a place — never a
   sentence. "Yahnessa" not "Daniel is married to Yahnessa."
6. One fact per write. Do not pack multiple facts into one value.
7. If a key already exists under the same category/topic/subtopic/subject,
   the old value is overwritten — so updating a fact is just writing it
   again.
8. A statement about YOUR OWN identity — your name, a trait or preference
   you've been assigned — is a fact too, and it is never a "person" fact.
   Use category="system", topic="identity", subtopic="persona", subject="":
     category="system", topic="identity", subtopic="persona", subject="", key="name", value="Morrow"
   "person" is reserved for the human and the people they tell you about;
   mixing your own identity into it is what causes it to be confused with
   theirs later.
9. A statement about THIS SYSTEM'S OWN configuration, rules, or mechanics
   (its security rules, its architecture, how its agents route work) is
   also a fact, also never a "person" fact, and also never a question even
   when the user is teaching it to you conversationally. Use
   category="system" with a topic that names what the fact is about —
   e.g. topic="security", subtopic="rule", subject="<the rule's own
   name>":
     category="system", topic="security", subtopic="rule", subject="bypass-this-system", key="verdict", value="red"
     category="system", topic="security", subtopic="rule", subject="self-harm-method", key="verdict", value="red"
   Being told "log this for future reference" is the user stating a fact
   and asking you to remember it — write it, exactly as you would if they
   had stated it about themselves.
   This applies to ANY of this system's own agents/components, not just
   the ones named above — a message that names an agent and describes
   what it does, even in passing ("your knowledge agent, the one that
   reads from your memory"), states a fact about this system just as
   much as a message naming a security rule does:
     category="system", topic="architecture", subtopic="agent", subject="Knowledge", key="function", value="reads from memory"
     category="system", topic="architecture", subtopic="agent", subject="Consolidator", key="function", value="writes new knowledge into memory"
   Do not require the message to be ABOUT that fact for it to count — a
   descriptive aside inside a request to test or check something ("let's
   check if X, the one that does Y, now also does Z") still states that X
   does Y, exactly as plainly as if it were the whole sentence.
10. Reuse existing categories and topics from the EXISTING CATEGORIES list
    when one fits. Create a new category when nothing does — a store that
    only has "person" facts in it must still be able to gain a "system"
    one the first time your own identity comes up. Don't invent a synonym
    for something that already exists, though.
11. writes may be empty if nothing worth storing was said.
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
        just said, nothing else."""
        lines = [f"EVENT: {envelope.event_id}", ""]
        sensory = str(envelope.content)[:400]
        lines.append(f"input: {sensory}")

        lines.append("")
        lines.append("EXISTING CATEGORIES/TOPICS (reuse these when they fit):")
        if self.structured_store is not None:
            index = self.structured_store.schema_index("knowledge")
            if index:
                for entry in index:
                    lines.append(f"  - {entry['category']}/{entry['topic']}")
            else:
                lines.append("  (empty — you may create new categories)")
        else:
            lines.append("  (not available)")

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
                    "subject": str(w.get("subject") or "")[:80],
                    "key": key[:120],
                    "value": value[:1000],
                })

        return writes, dropped


__all__ = ["ConsolidatorAgent", "DEFAULT_SYSTEM_INSTRUCTION",
           "CONSOLIDATION_RESPONSE_CONTRACT"]
