"""
Reading structured answers back out of a model (§10.2).

Every cognitive role in the ecosystem asks its substrate for JSON and has
to cope with the fact that models, and the endpoints in front of them,
wrap that JSON in different things: a prefilled opening brace, a ```json
fence, a sentence of preamble, a cheerful sign-off. Rather than forbid
all of that per-vendor in a system prompt and hope, this scans for the
first balanced object and ignores everything around it.

Deliberately tolerant about the packaging and deliberately strict about
the contents: getting an object back is this module's job, and deciding
whether the object says anything legal is the caller's.

This lived inside the Phase 0.1 Governance agent until that role turned
out not to need a model at all. It moves here because Analytics needs it
now and Intent will need it next — one implementation, one set of tests.
"""
from __future__ import annotations

import json
from typing import Any, Dict, Optional


def extract_json_object(text: str) -> Optional[Dict[str, Any]]:
    """Find the first balanced {...} in `text` and parse it.

    Returns None when there is no parseable object — which callers treat
    as "the substrate did not answer" and degrade from, rather than as an
    error to propagate.

    String-aware, so a brace inside a quoted payload doesn't end the scan
    early, and a malformed first candidate doesn't stop a later valid one
    from being found."""
    if not text:
        return None

    start = text.find("{")
    while start != -1:
        depth = 0
        in_string = False
        escaped = False
        for index in range(start, len(text)):
            char = text[index]
            if in_string:
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == '"':
                    in_string = False
                continue
            if char == '"':
                in_string = True
            elif char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
                if depth == 0:
                    candidate = text[start:index + 1]
                    try:
                        parsed = json.loads(candidate)
                    except json.JSONDecodeError:
                        break
                    return parsed if isinstance(parsed, dict) else None
        start = text.find("{", start + 1)
    return None


def coerce_bool(value: Any, default: bool) -> bool:
    """Read a boolean a model might have spelled several ways.

    Models return `false`, `"false"`, `"no"`, and occasionally `0` for the
    same intent. Anything genuinely unrecognisable returns `default` —
    and every caller in this codebase passes the SAFE value as the
    default, so an unreadable answer never accidentally means 'go ahead'.
    """
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return bool(value)
    if isinstance(value, str):
        lowered = value.strip().lower()
        if lowered in ("true", "yes", "y", "proceed", "1", "ok", "allow"):
            return True
        if lowered in ("false", "no", "n", "decline", "0", "stop", "block"):
            return False
    return default


__all__ = ["extract_json_object", "coerce_bool"]
