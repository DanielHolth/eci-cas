"""One-time migration: add the `domain` column (dispatch #4, 2026-08-29) to
a StructuredStore Parquet file written before that field existed.

Every pre-migration row is user-taught fact — Consolidator's only output —
so every row becomes domain="external" (structured_store.DEFAULT_DOMAIN).
A file that already has the column is left untouched (idempotent, safe to
rerun against a deployment that's already migrated).

Usage:
    python -m tools.migrate_domain data/archive/structured/knowledge.parquet
    python -m tools.migrate_domain data/archive/structured/identity.parquet
"""
from __future__ import annotations

import sys
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

from agents.archive.structured_store import DEFAULT_DOMAIN, SCHEMA


def migrate(path: Path) -> int:
    table = pq.read_table(path)
    if "domain" in table.schema.names:
        print(f"{path}: already has 'domain', skipping")
        return 0
    rows = table.to_pylist()
    for row in rows:
        row["domain"] = DEFAULT_DOMAIN
    pq.write_table(pa.Table.from_pylist(rows, schema=SCHEMA), path)
    print(f"{path}: migrated {len(rows)} rows to domain={DEFAULT_DOMAIN!r}")
    return len(rows)


def main(argv=None) -> int:
    argv = argv if argv is not None else sys.argv[1:]
    if not argv:
        print("usage: python -m tools.migrate_domain <parquet-file> [...]", file=sys.stderr)
        return 1
    for arg in argv:
        migrate(Path(arg))
    return 0


if __name__ == "__main__":
    sys.exit(main())
