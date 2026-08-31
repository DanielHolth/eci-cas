# Archive Tool

`EciCas.ArchiveTool` is a console REPL for inspecting and manually editing
the Parquet archive directly — for testing and prototyping, when a record
needs to be corrected or removed without running the full agent swarm.
It reuses `ParquetArchiveStore`'s static read/write helpers rather than
duplicating Parquet I/O, so its notion of a record's shape never drifts
from `IArchiveStore`'s.

## Running it

```bash
dotnet run --project src/EciCas.ArchiveTool -- <archive-directory>
```

Directory defaults to `archive` (relative to cwd) if omitted. On Windows,
prefer PowerShell or forward slashes for the path — Git Bash/MSYS mangles
a backslash-prefixed argument (`\D`, `\E`, ... read as escape sequences).

## Commands

| Command | Effect |
|---|---|
| `list` | Category names (one per `.parquet` file, minus `index`) |
| `show <category> [topic] [subtopic]` | `[i] Topic/Subtopic/Subject/Key = Value` — same shape RecallAgent logs for its picked facts |
| `showall <category> [topic] [subtopic]` | Full field dump per row, including Importance/Domain/Timestamp |
| `del <category> <index[,index...]>` | Delete specific rows by the index `show`/`showall` printed |
| `del <category> <topic> [subtopic]` | Delete every row whose Topic (and Subtopic, if given) contains the text, case-insensitive substring match |
| `rebuild-index` | Rescans every category file and rewrites `index.parquet` from scratch |
| `help` / `exit` | — |

`del`'s second form is picked automatically when its third token isn't a
comma-separated list of integers — no separate flag needed.

## Caveats

- No quote-awareness: arguments are split on plain whitespace, so a
  filter value containing a space (e.g. a malformed `Topic` field) can't
  be quoted — fall back to index-based `del` in that case.
- Filter delete is a substring match, not exact — double-check `show`'s
  output before deleting on a short or common topic string.
- Only one running instance should point at a given archive directory at
  a time; the underlying files aren't safe for concurrent writers (same
  constraint `ParquetArchiveStore` has for the live Host).
