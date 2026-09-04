"use client";

import { useEffect, useState } from "react";
import { API_BASE } from "@/lib/api";

/**
 * The persona's own card, as it will reach Intent this turn.
 *
 * Surfaced because it is otherwise the one configured thing with no window
 * onto it: it tints every reply, lives in a JSONL nobody opens, and a seed
 * file that no longer applies looks exactly like one that does. Silent on
 * failure — not knowing the persona is a reason to say nothing, not a reason
 * to show an error where a sentence about a personality should be.
 */
export function usePersona(): string | null {
  const [persona, setPersona] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    fetch(`${API_BASE}/api/persona`, { signal: abort.signal })
      .then((r) => (r.ok ? r.json() : null))
      .then((body) => setPersona(typeof body?.text === "string" ? body.text : null))
      .catch(() => {});
    return () => abort.abort();
  }, []);

  return persona;
}
