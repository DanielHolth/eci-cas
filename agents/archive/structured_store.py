"""
Structured Archive Store — Parquet-backed category/topic/subtopic/key/value model.

Phase 0.8: replaces flat JSON arrays for knowledge retrieval at scale.
The original ArchiveStore (store.py) remains the interface for identity,
temp_log, budget, queue, and drive vectors. This module handles the new
structured knowledge tier only.

Storage layout:
    /archive/
      /structured/   knowledge.parquet
                     identity.parquet
"""
from __future__ import annotations

import time
from pathlib import Path
from typing import Any, Dict, List, Optional

import pyarrow as pa
import pyarrow.parquet as pq


SCHEMA = pa.schema([
    ("category", pa.string()),
    ("topic", pa.string()),
    ("subtopic", pa.string()),
    ("key", pa.string()),
    ("value", pa.string()),
    ("written_at", pa.string()),
    ("source", pa.string()),
])


class StructuredStore:
    def __init__(self, root: str = "data/archive"):
        self.root = Path(root)
        self.structured_dir = self.root / "structured"
        self.structured_dir.mkdir(parents=True, exist_ok=True)
        self._knowledge_file = self.structured_dir / "knowledge.parquet"
        self._identity_file = self.structured_dir / "identity.parquet"

    def _file_for(self, kind: str) -> Path:
        if kind == "knowledge":
            return self._knowledge_file
        elif kind == "identity":
            return self._identity_file
        raise ValueError(f"Unknown structured kind '{kind}'")

    def write(self, kind: str, records: List[Dict[str, Any]]) -> int:
        """Append records to the structured store. Returns count written."""
        path = self._file_for(kind)
        rows = []
        for r in records:
            rows.append({
                "category": str(r.get("category", "")),
                "topic": str(r.get("topic", "")),
                "subtopic": str(r.get("subtopic", "")),
                "key": str(r.get("key", "")),
                "value": str(r.get("value", "")),
                "written_at": r.get("written_at") or time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "source": str(r.get("source", "unknown")),
            })
        new_table = pa.Table.from_pylist(rows, schema=SCHEMA)
        if path.exists():
            existing = pq.read_table(path)
            combined = pa.concat_tables([existing, new_table])
        else:
            combined = new_table
        pq.write_table(combined, path)
        return len(rows)

    def query(self, kind: str, *,
              category: Optional[str] = None,
              topic: Optional[str] = None,
              subtopic: Optional[str] = None,
              key: Optional[str] = None,
              limit: Optional[int] = None) -> List[Dict[str, Any]]:
        """Query with predicate pushdown on category/topic/subtopic/key."""
        path = self._file_for(kind)
        if not path.exists():
            return []

        filters = []
        if category:
            filters.append(("category", "==", category))
        if topic:
            filters.append(("topic", "==", topic))
        if subtopic:
            filters.append(("subtopic", "==", subtopic))
        if key:
            filters.append(("key", "==", key))

        table = pq.read_table(path, filters=filters if filters else None)
        results = table.to_pylist()
        if limit is not None:
            results = results[-limit:]
        return results

    def schema_index(self, kind: str) -> List[Dict[str, str]]:
        """Return distinct (category, topic) pairs — the routing map for Analytics."""
        path = self._file_for(kind)
        if not path.exists():
            return []
        table = pq.read_table(path, columns=["category", "topic"])
        pairs = set()
        for row in table.to_pylist():
            pairs.add((row["category"], row["topic"]))
        return [{"category": c, "topic": t} for c, t in sorted(pairs)]

    def count(self, kind: str) -> int:
        path = self._file_for(kind)
        if not path.exists():
            return 0
        return pq.read_metadata(path).num_rows
