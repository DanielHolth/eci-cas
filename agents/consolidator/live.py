"""
Consolidator — LIVE tier (v0.35f/g).

The moved body of Phase 0.4's `IntentAgent.reconcile()` /
`_consolidation_prompt()` / `_parse_consolidation()`, now its own agent
on its own substrate class. Same discipline as every other live tier in
this codebase: a validated response contract, a deterministic fallback,
budget-mode awareness, and the same attribution fields on every epoch.

Two things are genuinely new here, both from v0.35g:

  Multiple write instructions per pass. One reasoning call over the whole
  accumulated batch may emit N writes, each fully specifying its own
  destination (store + tag), so Archive has nothing left to decide, only
  to execute. Option B taken to its natural conclusion.

  Source determines destination, as a DEFAULT rather than a rule:
  Sensory-sourced content -> knowledge, Intent-sourced content ->
  identity, security events -> knowledge tagged `security`. This lives in
  the prompt, not in code, deliberately: the spec is explicit that
  Consolidator may override the default for an obvious misfit, so the
  judgment stays with the reasoner and the parse boundary only checks
  that the answer is structurally valid.

Fallback posture: fail-OPEN, and that is not an oversight. Consolidator
gates nothing — no action waits on it, nothing reaching the human passes
through it. An unusable answer means one cycle writes an empty templated
epoch and the batch's content is lost to reconciliation, which is
recoverable state loss, not a safety event (§7.2's own framing of the
temp log). The fail-CLOSED asymmetry in this system belongs to the hops
that gate: Analytics' Evaluate-adjacent judgments, and (v0.35e) Intent's
Review/Revise.
"""
from __future__ import annotations

import time
from typing import Any, Dict, List, Optional, Tuple

from bus.pubsub import EmbeddedBus
from substrates.base import (
    CompletionError,
    FailureKind,
    Substrate,
    SubstrateError,
)
from substrates.parsing import extract_json_object

from agents.consolidator.base import (
    ConsolidationResult,
    ConsolidatorBase,
    DEFAULT_BATCH_SIZE,
)

DEFAULT_SYSTEM_INSTRUCTION = (
    "You are CONSOLIDATOR, the memory-writing agent of a multi-agent "
    "system. You extract facts from the user's own words and store them. "
    "ONLY write facts the user explicitly stated. Never infer, embellish, "
    "or guess details that were not in the input. If the user said "
    "'I married Yahnessa' do NOT invent a location or date they did not "
    "mention. You never speak to the human and you never gate anything."
)

#: The response contract. Code-fixed rather than manifest-only, for the
#: same reason Analytics' and Intent's are: it must survive an operator
#: blanking `system_instruction`.
#:
#: The destination rule is stated here, in the prompt, because it is a
#: DEFAULT the reasoner may override for a misfit — not a constraint code
#: should enforce (v0.35g).
CONSOLIDATION_RESPONSE_CONTRACT = """
Reply with a single JSON object and nothing else:

  {"deltas": [{"trait": "<short name>", "rationale": "<one sentence>"}],
   "recalibration": {"<drive vector name>": <small float, -0.2 to 0.2>},
   "evolving_delta": "<one or two sentences: what shifted, in your own words>",
   "writes": [{"category": "<broad domain>",
               "topic": "<grouping within domain>",
               "subtopic": "<the specific who or what>",
               "key": "<attribute name>",
               "value": "<bare datum>"}]}

deltas may be empty. recalibration may be empty or omitted entirely —
only include a vector if this batch gives you a real reason to nudge your
baseline temperament, and keep the number small; this compounds slowly
over many cycles, not all at once.

WRITE RULES — follow exactly:
1. ONLY write facts the user explicitly stated. Questions are NOT facts.
2. category, topic, and subtopic are SINGLE words or short phrases, NEVER
   paths with slashes. The schema is: category = broad domain (person,
   place, event), topic = grouping (family, relationship, biography),
   subtopic = the specific entity (wife, mother, dog). Multiple keys
   hang off the same subtopic:
     category="person", topic="relationship", subtopic="wife", key="name", value="Yahnessa"
     category="person", topic="relationship", subtopic="wife", key="marriage_date", value="07.03.2004"
     category="person", topic="relationship", subtopic="wife", key="marriage_location", value="Tjøme"
     category="person", topic="family", subtopic="mother", key="name", value="Maria"
     category="person", topic="family", subtopic="mother", key="occupation", value="nurse"
3. value must be the bare datum — a name, a date, a place — never a
   sentence. "Yahnessa" not "Daniel is married to Yahnessa."
4. One fact per write. Do not pack multiple facts into one value.
5. If a key already exists under the same path, the old value is
   overwritten — so updating a fact is just writing it again.
6. Reuse existing categories and topics from the EXISTING CATEGORIES list.
   Do not invent synonyms.
7. writes may be empty if nothing worth storing was said.
"""


class ConsolidatorAgent(ConsolidatorBase):
    """Substrate-backed Consolidator. Drop-in replacement for ConsolidatorMock."""

    tier = "live"

    def __init__(self, bus: EmbeddedBus, substrate: Substrate, archive=None, *,
                 batch_size: int = DEFAULT_BATCH_SIZE,
                 impulse=None,
                 synchronous: bool = False,
                 system_instruction: str = "",
                 temperature: float = 0.3,
                 max_tokens: Optional[int] = None,
                 strict: bool = False,
                 budget=None,
                 structured_store=None):
        self.substrate = substrate
        self.budget = budget
        self.structured_store = structured_store
        self.system_instruction = (system_instruction or DEFAULT_SYSTEM_INSTRUCTION).strip()
        self.temperature = float(temperature)
        self.max_tokens = max_tokens or substrate.max_tokens
        self.strict = bool(strict)
        super().__init__(bus, archive, batch_size=batch_size, impulse=impulse,
                         synchronous=synchronous)

    # ---- The pass ----------------------------------------------------------

    def reconcile(self, batch: List[Dict[str, Any]],
                  recent_queue: List[Dict[str, Any]],
                  prior_epochs: List[Dict[str, Any]]) -> ConsolidationResult:
        if self.budget is not None and not self.budget.should_call_substrate():
            return ConsolidationResult(
                deltas=[], decided_by="fallback",
                diagnostics={"degraded": True,
                             "reason": "budget mode",
                             "source_substrate": "none (budget mode)",
                             "source_model": "none (budget mode)"})

        system = (self.system_instruction + "\n" + CONSOLIDATION_RESPONSE_CONTRACT).strip()
        user = self._prompt(batch, recent_queue, prior_epochs)

        try:
            self.metrics["llm_calls"] += 1
            started = time.perf_counter()
            response = self.substrate.complete(
                system=system, user=user, temperature=self.temperature,
                max_tokens=self.max_tokens, prefill="{",
            )
            latency_ms = round((time.perf_counter() - started) * 1000, 1)
            usage = dict(response.usage or {})
            cost = self.substrate.estimate_cost(usage)
            if self.budget is not None:
                self.budget.record_success(usage=usage, cost_usd=cost)

            deltas, recalibration, evolving_delta, writes, dropped = self._parse(
                response.text)
            return ConsolidationResult(
                deltas=deltas, recalibration=recalibration,
                evolving_delta=evolving_delta, writes=writes, decided_by="llm",
                diagnostics={
                    "source_substrate": self.substrate.substrate_class,
                    "source_model": self.substrate.model,
                    "provider": self.substrate.provider_name,
                    "latency_ms": latency_ms, "usage": usage,
                    "est_cost_usd": cost or None,
                    "batch_size": len(batch),
                    "writes_rejected": dropped or None,
                },
            )
        except (SubstrateError, ValueError) as exc:
            if isinstance(exc, CompletionError) and self.budget is not None:
                self.budget.record_failure(
                    getattr(exc, "kind", FailureKind.UNKNOWN), str(exc))
            if self.strict:
                raise
            return ConsolidationResult(
                deltas=[], decided_by="fallback",
                diagnostics={"degraded": True,
                             "reason": f"{type(exc).__name__}: {exc}"[:200],
                             "source_substrate": "none (degraded)",
                             "source_model": "none (degraded)"})

    # ---- Prompt -------------------------------------------------------------

    def _prompt(self, batch: List[Dict[str, Any]],
                recent_queue: List[Dict[str, Any]],
                prior_epochs: List[Dict[str, Any]]) -> str:
        """The batch, rendered.

        Only the original Sensory input and non-green security verdicts.
        Deliberately excluded: Intent's voiced response, Analytics'
        recommendations, Personality/Knowledge findings, and the queue
        log — all are downstream interpretations that the LLM could
        mistake for ground truth, causing hallucinated facts to be
        written back as knowledge."""
        lines = [f"BATCH: {len(batch)} concluded events since the last pass.", ""]

        for entry in batch[-25:]:
            lines.append(f"- event {entry.get('event_id', '?')}")
            sensory = str(entry.get("sensory", ""))[:200]
            if sensory:
                lines.append(f"    input:    {sensory}")
            security = entry.get("security") or {}
            verdict = security.get("verdict")
            if verdict and verdict not in ("green", None):
                lines.append(f"    security: {verdict}"
                             + (f" — {str(security.get('concern'))[:160]}"
                                if security.get("concern") else ""))
        if not batch:
            lines.append("  (none)")

        lines.append("")
        lines.append("PRIOR EPOCHS (most recent identity deltas):")
        for epoch in prior_epochs:
            for delta in epoch.get("deltas", []):
                lines.append(f"  - {delta}")
        if not any(e.get("deltas") for e in prior_epochs):
            lines.append("  (none yet)")

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

    def _parse(self, text: str) -> Tuple[List[Dict[str, Any]], Dict[str, float],
                                         str, List[Dict[str, Any]], int]:
        obj = extract_json_object(text)
        if obj is None:
            raise ValueError(f"no JSON object in consolidation response: {text[:200]!r}")

        raw_deltas = obj.get("deltas")
        deltas: List[Dict[str, Any]] = []
        if isinstance(raw_deltas, list):
            for d in raw_deltas[:10]:
                if not isinstance(d, dict):
                    continue
                trait = d.get("trait")
                if not trait:
                    continue
                deltas.append({"trait": str(trait)[:80],
                               "rationale": str(d.get("rationale") or "")[:300]})

        recalibration: Dict[str, float] = {}
        raw_recal = obj.get("recalibration")
        if isinstance(raw_recal, dict):
            for name, value in raw_recal.items():
                try:
                    f = float(value)
                except (TypeError, ValueError):
                    continue
                # Clamped hard, regardless of what the model asked for —
                # "slow coloring" only stays slow if the ceiling is
                # enforced here, not requested there (same discipline as
                # Impulse's own IMPULSE_SEVERITY_CEILING).
                recalibration[str(name)] = max(-0.2, min(0.2, f))

        evolving_delta = str(obj.get("evolving_delta") or "")[:400]

        # Write instructions. Structural validation only — an unknown
        # store is dropped and counted rather than guessed at, the same
        # clamp-at-the-boundary posture as the recalibration cap above.
        # WHERE something belongs is the reasoner's judgment (see the
        # module docstring); whether the answer is well-formed is ours.
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
                    "key": key[:120],
                    "value": value[:1000],
                })

        return deltas, recalibration, evolving_delta, writes, dropped


__all__ = ["ConsolidatorAgent", "DEFAULT_SYSTEM_INSTRUCTION",
           "CONSOLIDATION_RESPONSE_CONTRACT"]
