"""
Archive — the only door to memory (ECI-spec-v0-30.md §5.8).

Archive is NOT a bus subscriber in this implementation: per §5.8 its real
interface is two stable HTTP-shaped endpoints, symmetric with Action but
for reads/writes instead of world-effects:

    POST /archive/write   (append)
    GET  /archive/query   (parameterized read)

Here those endpoints are plain Python methods (write / query) that any
agent or the bus itself can call directly. This keeps the *interface*
stable across storage phases (§5.8's phase table: JSON -> SQLite -> Parquet)
while Phase 0 uses the on-disk layout from §13.2:

    /archive/
      /queue/      events_<date>.jsonl     # append-only event log (hot)
      /identity/   intent_epochs.json      # epoch deltas, §7.4 schema
      /knowledge/  knowledge_store.json
      /working/    temp_log.json, drive_vectors.json

Everything is inspectable with cat / grep / jq — debuggability is the point.
"""
from __future__ import annotations

import json
import os
import time
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from bus.envelope import Envelope


class ArchiveStore:
    def __init__(self, root: str = "data/archive"):
        self.root = Path(root)
        self.queue_dir = self.root / "queue"
        self.identity_dir = self.root / "identity"
        self.knowledge_dir = self.root / "knowledge"
        self.working_dir = self.root / "working"
        for d in (self.queue_dir, self.identity_dir, self.knowledge_dir, self.working_dir):
            d.mkdir(parents=True, exist_ok=True)

        self._identity_file = self.identity_dir / "intent_epochs.json"
        self._knowledge_file = self.knowledge_dir / "knowledge_store.json"
        self._temp_log_file = self.working_dir / "temp_log.json"
        self._drive_vectors_file = self.working_dir / "drive_vectors.json"
        for f in (self._identity_file, self._knowledge_file, self._temp_log_file):
            if not f.exists():
                f.write_text("[]")
        if not self._drive_vectors_file.exists():
            self._drive_vectors_file.write_text("{}")

    # ---- POST /archive/write (append) -----------------------------------

    def log_event(self, topic: str, envelope: Envelope) -> None:
        """Append one bus hop to today's hot queue log (JSONL)."""
        path = self.queue_dir / f"events_{time.strftime('%Y-%m-%d', time.gmtime())}.jsonl"
        record = {"topic": topic, **envelope.to_dict()}
        with path.open("a") as f:
            f.write(json.dumps(record) + "\n")

    def write(self, kind: str, record: Dict[str, Any]) -> None:
        """Generic append writer for identity / knowledge / working tiers (§6)."""
        target = {
            "identity": self._identity_file,
            "knowledge": self._knowledge_file,
            "temp_log": self._temp_log_file,
        }.get(kind)
        if target is None:
            raise ValueError(f"Unknown archive kind '{kind}'")
        data = json.loads(target.read_text())
        data.append(record)
        target.write_text(json.dumps(data, indent=2))

    def set_drive_vectors(self, vectors: Dict[str, float]) -> None:
        self._drive_vectors_file.write_text(json.dumps(vectors, indent=2))

    def get_drive_vectors(self) -> Dict[str, float]:
        return json.loads(self._drive_vectors_file.read_text())

    # ---- GET /archive/query (parameterized read) -------------------------

    def query(self, kind: str, predicate: Optional[Callable[[Dict], bool]] = None,
              limit: Optional[int] = None) -> List[Dict[str, Any]]:
        """Read back identity / knowledge / temp_log records, optionally filtered."""
        target = {
            "identity": self._identity_file,
            "knowledge": self._knowledge_file,
            "temp_log": self._temp_log_file,
        }.get(kind)
        if target is None:
            raise ValueError(f"Unknown archive kind '{kind}'")
        data = json.loads(target.read_text())
        if predicate is not None:
            data = [d for d in data if predicate(d)]
        if limit is not None:
            data = data[-limit:]
        return data

    def query_queue(self, date: Optional[str] = None,
                     predicate: Optional[Callable[[Dict], bool]] = None) -> List[Dict[str, Any]]:
        """Read back the hot queue log for a given date (default: today)."""
        date = date or time.strftime("%Y-%m-%d", time.gmtime())
        path = self.queue_dir / f"events_{date}.jsonl"
        if not path.exists():
            return []
        out = []
        with path.open() as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                rec = json.loads(line)
                if predicate is None or predicate(rec):
                    out.append(rec)
        return out
