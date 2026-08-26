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

from typing import Any, Dict, List, Optional

from agents.archive.structured_store import StructuredStore


SWARM_TIERS: Dict[str, Dict[str, int]] = {
    "minimal": {"agents": 2, "max_results_per_agent": 100},
    "budget":  {"agents": 3, "max_results_per_agent": 200},
    "default": {"agents": 4, "max_results_per_agent": 300},
    "super":   {"agents": 5, "max_results_per_agent": 400},
}


def retrieve_per_path(
    store: StructuredStore,
    paths: List[Dict[str, str]],
    *,
    tier: str = "default",
) -> List[tuple]:
    """Query structured store for each (category, topic) path.

    Returns list of (path, records) tuples — one per active path,
    so callers can publish each node's findings individually."""
    config = SWARM_TIERS.get(tier, SWARM_TIERS["default"])
    max_paths = config["agents"]
    max_per_path = config["max_results_per_agent"]

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
                         r.get("subtopic"), r.get("key"))
            if dedup_key not in seen_keys:
                seen_keys.add(dedup_key)
                deduped.append(r)
        results.append((path, deduped))

    return results


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
        path = f"{r.get('category','')}/{r.get('topic','')}/{r.get('subtopic','')}"
        lines.append(f"{path}: {r.get('key','')} = {r.get('value','')}")
    return "; ".join(lines)
