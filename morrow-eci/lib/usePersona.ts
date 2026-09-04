"use client";

import { useEffect, useState } from "react";
import { API_BASE } from "@/lib/api";

export type Persona = {
  /** The tone card, as it will reach Intent this turn. */
  text: string | null;
  /** What this profile calls it — the configured default until someone renames it. */
  name: string | null;
};

/**
 * The persona's own card and its name.
 *
 * Surfaced because it is otherwise the one configured thing with no window
 * onto it: it tints every reply, lives in a JSONL nobody opens, and a seed
 * file that no longer applies looks exactly like one that does. Silent on
 * failure — not knowing the persona is a reason to say nothing, not a reason
 * to show an error where a sentence about a personality should be.
 *
 * The name is per profile and can change mid-conversation, so this refetches
 * when the profile changes and whenever `revision` moves. Nothing pushes a
 * rename — Archivist writes it during a turn like any other fact — so the
 * caller bumps `revision` once a turn has settled rather than this polling.
 */
export function usePersona(profileId?: string | null, revision = 0): Persona {
  const [persona, setPersona] = useState<Persona>({ text: null, name: null });

  useEffect(() => {
    const abort = new AbortController();
    const query = profileId ? `?profileId=${encodeURIComponent(profileId)}` : "";
    fetch(`${API_BASE}/api/persona${query}`, { signal: abort.signal })
      .then((r) => (r.ok ? r.json() : null))
      .then((body) =>
        setPersona({
          text: typeof body?.text === "string" ? body.text : null,
          name: typeof body?.name === "string" ? body.name : null,
        }),
      )
      .catch(() => {});
    return () => abort.abort();
  }, [profileId, revision]);

  return persona;
}
