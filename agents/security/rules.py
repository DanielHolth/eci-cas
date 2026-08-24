"""
Security's rule engine (Phase 0.6, §5.6).

The answer to the open question the Phase 0.6 handover left standing:
what does `security_rules.json` actually look like?

It is a **closed, declarative pattern list** — not a DSL, not a scripting
hook, not a keyword bag. The reasons, in the order they mattered:

  * §5.6 makes Security "is this against the rules", deterministic and
    auditable — every verdict must be justifiable from the rules file and
    that single event, by a human reading both. A DSL with control flow
    stops being readable at exactly the moment it becomes useful.
  * A bare keyword list can't express "unless", and almost every real
    rule needs one ("refuse to give an address, UNLESS it is our own").
    Without it the rules file grows a parallel allowlist that no longer
    lines up with the denials.
  * Regex is the smallest thing that covers the actual cases and still
    has one obvious meaning per rule. Every pattern is compiled at LOAD
    time, so a typo is a bootstrap failure, not a mid-conversation
    exception on the safety path.

Evaluation is total and order-independent: every rule is tested, the
HIGHEST verdict any matching rule asks for wins, and the concern text is
the matching rule's own. Order-independence is deliberate — a rules file
whose meaning depends on line order is a rules file nobody can safely
edit.

There is no model here and there will not be one. A reasoner in this seat
would trade the audit trail for judgment the ecosystem already has in
Intent, and the hard stop only works while it stays mechanical.
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

from bus.envelope import (
    VERDICT_GREEN,
    VERDICT_LEVELS,
    VERDICT_RED,
    VERDICT_YELLOW,
)


class RulesError(ValueError):
    """A rules file that cannot be trusted. Raised at LOAD time only.

    Security is the hard stop: an unreadable rules file must stop the
    bootstrap deterministically, exactly as an unreachable credential
    stops a cognitive role (§9.1 step 6). The one thing it must never do
    is boot with a degraded rule set that still answers green."""


def _rank(verdict: str) -> int:
    return VERDICT_LEVELS.index(verdict)


def verdict_max(a: str, b: str) -> str:
    """Highest-wins, mirroring severity_max's OR-upscale-only discipline
    (bus/envelope.py). A rule may raise a verdict; none may lower one."""
    return a if _rank(a) >= _rank(b) else b


@dataclass(frozen=True)
class Rule:
    """One rule. `any`/`all` are match conditions, `unless` is an escape.

    A rule fires when (any of `any` matches) AND (all of `all` match) AND
    (none of `unless` matches). Omitted lists are vacuous, so a rule with
    only `any` reads exactly as you'd expect."""
    id: str
    verdict: str
    concern: str
    description: str = ""
    any_of: Sequence[re.Pattern] = field(default_factory=tuple)
    all_of: Sequence[re.Pattern] = field(default_factory=tuple)
    unless: Sequence[re.Pattern] = field(default_factory=tuple)

    def matches(self, text: str) -> bool:
        if self.any_of and not any(p.search(text) for p in self.any_of):
            return False
        if self.all_of and not all(p.search(text) for p in self.all_of):
            return False
        if any(p.search(text) for p in self.unless):
            return False
        # A rule with no conditions at all matches nothing. An empty rule
        # is a mistake, and the safe reading of a mistake on this path is
        # "does not fire" — never "fires on everything".
        return bool(self.any_of or self.all_of)


@dataclass(frozen=True)
class Evaluation:
    """What the engine decided, and enough to audit why."""
    verdict: str
    concern: str
    matched: List[str]                       # rule ids, for the audit trail

    def to_meta(self) -> Dict[str, Any]:
        meta: Dict[str, Any] = {"verdict": self.verdict}
        if self.concern:
            meta["security_concern"] = self.concern
        if self.matched:
            meta["security_rules_matched"] = list(self.matched)
        return meta


def _compile(patterns: Any, rule_id: str, field_name: str) -> Sequence[re.Pattern]:
    if patterns is None:
        return ()
    if not isinstance(patterns, list):
        raise RulesError(f"rule '{rule_id}': {field_name} must be a list of "
                         f"patterns, got {type(patterns).__name__}")
    out = []
    for pattern in patterns:
        if not isinstance(pattern, str):
            raise RulesError(f"rule '{rule_id}': {field_name} entries must be "
                             f"strings, got {type(pattern).__name__}")
        try:
            out.append(re.compile(pattern, re.IGNORECASE))
        except re.error as exc:
            raise RulesError(f"rule '{rule_id}': bad pattern "
                             f"{pattern!r} in {field_name} — {exc}") from exc
    return tuple(out)


class RuleSet:
    """A loaded, validated, compiled rules file."""

    def __init__(self, rules: Sequence[Rule], *, version: str = "",
                 source: str = "<memory>"):
        self.rules = tuple(rules)
        self.version = version
        self.source = source

    def __len__(self) -> int:
        return len(self.rules)

    # ---- Loading ---------------------------------------------------------

    @classmethod
    def from_dict(cls, data: Dict[str, Any], *, source: str = "<memory>") -> "RuleSet":
        if not isinstance(data, dict):
            raise RulesError(f"{source}: top level must be an object")
        raw_rules = data.get("rules")
        if not isinstance(raw_rules, list) or not raw_rules:
            raise RulesError(f"{source}: 'rules' must be a non-empty list. "
                             f"An empty rule set clears everything, which is "
                             f"indistinguishable from the Phase 0 mock.")

        seen = set()
        rules: List[Rule] = []
        for index, raw in enumerate(raw_rules):
            if not isinstance(raw, dict):
                raise RulesError(f"{source}: rules[{index}] must be an object")
            rule_id = str(raw.get("id") or "").strip()
            if not rule_id:
                raise RulesError(f"{source}: rules[{index}] has no 'id'. Every "
                                 f"verdict names the rule that produced it; an "
                                 f"unnamed rule is an unauditable verdict.")
            if rule_id in seen:
                raise RulesError(f"{source}: duplicate rule id '{rule_id}'")
            seen.add(rule_id)

            verdict = str(raw.get("verdict") or "").strip().lower()
            if verdict not in VERDICT_LEVELS:
                raise RulesError(f"{source}: rule '{rule_id}' has verdict "
                                 f"{verdict!r}; must be one of {VERDICT_LEVELS}")
            if verdict == VERDICT_GREEN:
                raise RulesError(
                    f"{source}: rule '{rule_id}' declares verdict 'green'. "
                    f"Green is the ABSENCE of a match, not something a rule "
                    f"asserts — a green rule could only ever be an attempt to "
                    f"cancel another rule, and rules here are order-independent "
                    f"by design. Use 'unless' on the rule you mean to narrow.")

            concern = str(raw.get("concern") or "").strip()
            if not concern:
                raise RulesError(f"{source}: rule '{rule_id}' has no 'concern'. "
                                 f"A non-green verdict travels to a reasoner "
                                 f"that has to act on it; 'no' without a reason "
                                 f"is not actionable.")

            rules.append(Rule(
                id=rule_id,
                verdict=verdict,
                concern=concern,
                description=str(raw.get("description") or ""),
                any_of=_compile(raw.get("any"), rule_id, "any"),
                all_of=_compile(raw.get("all"), rule_id, "all"),
                unless=_compile(raw.get("unless"), rule_id, "unless"),
            ))

        return cls(rules, version=str(data.get("version") or ""), source=source)

    @classmethod
    def load(cls, path: str | Path) -> "RuleSet":
        p = Path(path)
        if not p.exists():
            raise RulesError(
                f"security rules file not found: {p}. Security running real "
                f"with no rules cannot be made safe by defaulting — it would "
                f"either clear everything (the mock, silently) or block "
                f"everything. Point roles.security.rules at a real file, or "
                f"set roles.security.mock: true and mean it.")
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            raise RulesError(f"{p}: not valid JSON — {exc}") from exc
        return cls.from_dict(data, source=str(p))

    # ---- Evaluation ------------------------------------------------------

    def evaluate(self, text: str) -> Evaluation:
        """Total: never raises, always returns one of VERDICT_LEVELS.

        Every rule is tested — no short-circuit on the first red. The cost
        is a few dozen regex searches; what it buys is a complete matched
        list, so an audit says which rules an action tripped rather than
        which one happened to be first in the file."""
        text = "" if text is None else str(text)
        verdict = VERDICT_GREEN
        matched: List[Rule] = []
        for rule in self.rules:
            try:
                if rule.matches(text):
                    matched.append(rule)
                    verdict = verdict_max(verdict, rule.verdict)
            except Exception:                              # pragma: no cover
                # A pattern that somehow explodes at match time must not
                # take the whole verdict with it. It is also not allowed
                # to pass silently: doubt on this path is yellow, which
                # routes to the agent that reasons (v0.34's fail-SAFE).
                verdict = verdict_max(verdict, VERDICT_YELLOW)

        if verdict == VERDICT_GREEN:
            return Evaluation(VERDICT_GREEN, "", [])

        # The concern comes from the rules that produced THIS verdict, not
        # from every rule that matched: a red action that also tripped a
        # yellow advisory should be explained by the red.
        decisive = [r for r in matched if r.verdict == verdict]
        concern = " ".join(r.concern for r in decisive)[:300]
        return Evaluation(verdict, concern, [r.id for r in matched])


__all__ = ["Rule", "RuleSet", "RulesError", "Evaluation", "verdict_max",
           "VERDICT_GREEN", "VERDICT_YELLOW", "VERDICT_RED"]
