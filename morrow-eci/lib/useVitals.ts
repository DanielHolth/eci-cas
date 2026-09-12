"use client";

import { useEffect, useRef, useState } from "react";
import { API_BASE } from "@/lib/api";

export interface Vitals {
  energy: {
    balanceUsd: number;
    maxUsd: number;
    /** 0..1 — what the bar under the face shows. */
    fraction: number;
    /** When the bucket is full again, or null if it already is. */
    fullAt: string | null;
    isEmpty: boolean;
  };
  level: {
    level: number;
    xp: number;
    intoLevel: number;
    levelCost: number;
    /** 0..1 through the current level. */
    fraction: number;
  };
}

const IDLE: Vitals = {
  energy: { balanceUsd: 0, maxUsd: 0, fraction: 1, fullAt: null, isEmpty: false },
  level: { level: 1, xp: 0, intoLevel: 0, levelCost: 2, fraction: 0 },
};

/**
 * Energy and level, refetched whenever a turn settles — the same
 * revision-driven shape as usePersona, and for the same reason: both change
 * only as a consequence of a turn, so polling would be asking a question
 * whose answer cannot have moved.
 *
 * `levelledUp` is derived here rather than announced by the host. A level-up
 * has no meaning on the bus; the ding and the floating +1 are display, and
 * the surface already holds the previous number.
 */
export function useVitals(revision = 0): { vitals: Vitals; levelledUp: number } {
  const [vitals, setVitals] = useState<Vitals>(IDLE);
  const [levelledUp, setLevelledUp] = useState(0);
  const known = useRef<number | null>(null);

  useEffect(() => {
    const abort = new AbortController();

    fetch(`${API_BASE}/api/vitals`, { signal: abort.signal })
      .then((response) => (response.ok ? response.json() : null))
      .then((next: Vitals | null) => {
        if (!next) return;
        setVitals(next);
        // The first read is a starting point, never a level-up: a person
        // reopening the app at level 7 has not just reached it.
        if (known.current !== null && next.level.level > known.current) {
          setLevelledUp(next.level.level);
        }
        known.current = next.level.level;
      })
      .catch(() => {
        // Host down. The meter keeps its last reading rather than dropping
        // to empty, which would read as a consequence rather than an outage.
      });

    return () => abort.abort();
  }, [revision]);

  return { vitals, levelledUp };
}
