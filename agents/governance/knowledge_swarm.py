"""
Knowledge Swarm — parallel structured-archive retrieval (Phase 0.8).

After Analytics reports knowledge_paths, Governance calls this module to
drill into each (category, topic) slice and collect relevant records.
This is deterministic retrieval (Parquet predicate pushdown), not LLM
judgment — the model already did its work in Analytics (choosing paths)
and the old Knowledge agent (relevance over flat records). The swarm
adds BREADTH: multiple slices explored in one pass.

On the synchronous bus this runs inline during Governance's bundle step.
On an async bus it would fan out as parallel agents — the interface is
the same either way (list of paths in, merged findings out).
"""
from __future__ import annotations

import re
from typing import Any, Dict, List, Optional

from agents.archive.structured_store import StructuredStore


SWARM_TIERS: Dict[str, Dict[str, int]] = {
    "minimal": {"agents": 2, "max_results_per_agent": 100},
    "budget":  {"agents": 3, "max_results_per_agent": 200},
    "default": {"agents": 4, "max_results_per_agent": 300},
    "super":   {"agents": 5, "max_results_per_agent": 400},
}

#: Per path, how many of the fetched candidates actually ride to Intent,
#: picked by relevance to the sensory input rather than store order —
#: keeps a path with hundreds of stored facts from overloading Intent
#: with everything it has ever learned about that category/topic.
MAX_RELEVANT_PER_PATH = 10


def _tokenize(text: str) -> set:
    return set(re.findall(r"[a-z0-9]+", text.lower()))


#: Literal keyword overlap alone can't tell "kids" (matches a child's own
#: name/subtopic) from "mother"/"father" (a different relation entirely) —
#: none of those role words share a token with "kids" or "children". Each
#: query term expands to its family-relation cousins so a query that
#: means "children" also credits records whose subtopic IS a role word
#: like "son"/"daughter", without crediting the opposite relation.
ROLE_SYNONYMS: Dict[str, set] = {
    "kids": {"son", "daughter", "child", "children", "kid"},
    "kid": {"son", "daughter", "child", "children", "kids"},
    "child": {"son", "daughter", "children", "kids", "kid"},
    "children": {"son", "daughter", "child", "kids", "kid"},
    "son": {"kids", "children", "child"},
    "daughter": {"kids", "children", "child"},
    "parents": {"mother", "father", "mom", "dad"},
    "mother": {"mom", "parents"},
    "father": {"dad", "parents"},
    "mom": {"mother", "parents"},
    "dad": {"father", "parents"},
    "wife": {"spouse", "married", "marriage"},
    "husband": {"spouse", "married", "marriage"},
    "spouse": {"wife", "husband", "married", "marriage"},
}


def _expand_query_terms(terms: set) -> set:
    expanded = set(terms)
    for term in terms:
        expanded |= ROLE_SYNONYMS.get(term, set())
    return expanded


def _relevance_score(record: Dict[str, Any], query_terms: set) -> int:
    text = (f"{record.get('subtopic', '')} {record.get('subject', '')} "
            f"{record.get('key', '')} {record.get('value', '')}")
    return len(_tokenize(text) & query_terms)


def retrieve_per_path(
    store: StructuredStore,
    paths: List[Dict[str, str]],
    *,
    tier: str = "default",
    query: str = "",
) -> List[tuple]:
    """Query structured store for each (category, topic) path.

    Returns list of (path, records) tuples — one per active path, each
    capped at MAX_RELEVANT_PER_PATH records, ranked by overlap with
    `query` (the sensory input) when given."""
    config = SWARM_TIERS.get(tier, SWARM_TIERS["default"])
    max_paths = config["agents"]
    max_per_path = config["max_results_per_agent"]
    query_terms = _expand_query_terms(_tokenize(query))

    active_paths = paths[:max_paths]
    results: List[tuple] = []
    seen_keys = set()

    for path in active_paths:
        category = path.get("category", "")
        topic = path.get("topic", "")
        # Normalize: LLM sometimes sends "person/family" as category
        if "/" in category and not topic:
            parts = category.split("/", 1)
            category, topic = parts[0], parts[1]
        elif "/" in category:
            category = category.split("/", 1)[0]
        records = store.query(
            "knowledge",
            category=category,
            topic=topic,
            limit=max_per_path,
        )
        deduped = []
        for r in records:
            dedup_key = (r.get("category"), r.get("topic"),
                         r.get("subtopic"), r.get("subject"), r.get("key"))
            if dedup_key not in seen_keys:
                seen_keys.add(dedup_key)
                deduped.append(r)
        if query_terms:
            scored = [(r, _relevance_score(r, query_terms)) for r in deduped]
            # A query that names some relation ("kids") shouldn't surface
            # an unrelated one ("mother") just to fill the round-robin —
            # drop the zero-score records, but only once we know at least
            # one record actually matched (else a broad, untargeted query
            # like "tell me about my family" would wipe out everything).
            if any(score > 0 for _, score in scored):
                scored = [(r, score) for r, score in scored if score > 0]
            scored.sort(key=lambda rs: rs[1], reverse=True)
            deduped = [r for r, _ in scored]
        results.append((path, _diversify_by_subject(deduped, MAX_RELEVANT_PER_PATH)))

    return results


def _diversify_by_subject(records: List[Dict[str, Any]], limit: int) -> List[Dict[str, Any]]:
    """Round-robin across distinct entities before taking a second record
    from any one of them.

    Grouped by subject (the specific entity — "Marcus", "Susana"), falling
    back to subtopic (the relation — "mother") for older or subject-less
    records so a subject-only cut can't split someone's facts before a
    name was ever given. Relevance scoring is literal keyword overlap plus a
    small role-synonym expansion, which still ties multiple people at the
    same score in common cases (three children all matching "kids"
    equally) — round-robin spreads the cut across all of them instead of
    a plain top-N letting whichever one sorted first crowd out the rest."""
    groups: Dict[str, List[Dict[str, Any]]] = {}
    order: List[str] = []
    for r in records:
        key = r.get("subject") or r.get("subtopic", "")
        if key not in groups:
            groups[key] = []
            order.append(key)
        groups[key].append(r)
    picked: List[Dict[str, Any]] = []
    while len(picked) < limit and any(groups[k] for k in order):
        for k in order:
            if groups[k]:
                picked.append(groups[k].pop(0))
                if len(picked) >= limit:
                    break
    return picked


def retrieve(
    store: StructuredStore,
    paths: List[Dict[str, str]],
    *,
    tier: str = "default",
) -> List[Dict[str, Any]]:
    """Query structured store for each (category, topic) path.

    Returns merged, deduplicated results ordered by path priority
    (first path = highest priority from Analytics)."""
    per_path = retrieve_per_path(store, paths, tier=tier)
    all_results: List[Dict[str, Any]] = []
    for _, records in per_path:
        all_results.extend(records)
    return all_results


def format_for_intent(results: List[Dict[str, Any]]) -> str:
    """Format swarm results as a terse string for Intent's bundle."""
    if not results:
        return ""
    lines = []
    for r in results:
        parts = [r.get('category', ''), r.get('topic', ''), r.get('subtopic', '')]
        if r.get('subject'):
            parts.append(r['subject'])
        path = "/".join(parts)
        lines.append(f"{path}: {r.get('key','')} = {r.get('value','')}")
    return "; ".join(lines)
