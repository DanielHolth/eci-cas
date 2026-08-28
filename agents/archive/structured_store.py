"""
Structured Archive Store — Parquet-backed category/topic/subtopic/subject/
key/value model.

Phase 0.8: replaces flat JSON arrays for knowledge retrieval at scale.
The original ArchiveStore (store.py) remains the interface for identity,
temp_log, budget, queue, and drive vectors. This module handles the new
structured knowledge tier only.

subtopic vs subject (Phase 0.9): subtopic is the stable RELATION/ROLE
("son", "daughter", "mother") — what makes "tell me about my kids"
answerable without already knowing anyone's name. subject is the specific
ENTITY ("Marcus", "Susana") — what keeps two sons from colliding and a
single child's facts from fragmenting. Consolidator always writes both;
neither is asked to carry the other's job.

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
    ("subject", pa.string()),
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
                "subject": str(r.get("subject", "")),
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
              subject: Optional[str] = None,
              key: Optional[str] = None,
              limit: Optional[int] = None) -> List[Dict[str, Any]]:
        """Query with predicate pushdown on category/topic/subtopic/subject/key."""
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
        if subject:
            filters.append(("subject", "==", subject))
        if key:
            filters.append(("key", "==", key))

        table = pq.read_table(path, filters=filters if filters else None)
        results = table.to_pylist()
        if limit is not None:
            results = results[-limit:]
        return results

    def schema_index(self, kind: str) -> List[Dict[str, Any]]:
        """Return distinct (category, topic) pairs — the routing map
        Analytics reads to know what paths exist to choose from."""
        path = self._file_for(kind)
        if not path.exists():
            return []
        table = pq.read_table(path, columns=["category", "topic"])
        pairs = {(row["category"], row["topic"]) for row in table.to_pylist()}
        return [{"category": c, "topic": t} for c, t in sorted(pairs)]

    def upsert(self, kind: str, records: List[Dict[str, Any]]) -> Dict[str, int]:
        """Write records, overwriting any existing row with the same
        (category, topic, subtopic, subject, key)."""
        path = self._file_for(kind)
        rows = []
        for r in records:
            rows.append({
                "category": str(r.get("category", "")),
                "topic": str(r.get("topic", "")),
                "subtopic": str(r.get("subtopic", "")),
                "subject": str(r.get("subject", "")),
                "key": str(r.get("key", "")),
                "value": str(r.get("value", "")),
                "written_at": r.get("written_at") or time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "source": str(r.get("source", "unknown")),
            })

        new_table = pa.Table.from_pylist(rows, schema=SCHEMA)
        updated = 0
        if path.exists():
            existing = pq.read_table(path)
            new_keys = {(r["category"], r["topic"], r["subtopic"], r["subject"], r["key"]) for r in rows}
            keep_mask = [
                (row["category"], row["topic"], row["subtopic"], row["subject"], row["key"]) not in new_keys
                for row in existing.to_pylist()
            ]
            updated = sum(1 for k in keep_mask if not k)
            filtered = existing.filter(keep_mask) if updated > 0 else existing
            combined = pa.concat_tables([filtered, new_table])
        else:
            combined = new_table
        pq.write_table(combined, path)
        return {"written": len(rows), "updated": updated}

    def count(self, kind: str) -> int:
        path = self._file_for(kind)
        if not path.exists():
            return 0
        return pq.read_metadata(path).num_rows
