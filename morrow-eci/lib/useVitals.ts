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
  tier: {
    name: string;
    /**
     * The local model is answering. True whether the meter ran dry or the
     * person chose the free tier themselves — the surface reads the tier,
     * never the reason, because the replies are the same either way.
     */
    isLocal: boolean;
  };
}

const IDLE: Vitals = {
  energy: { balanceUsd: 0, maxUsd: 0, fraction: 1, fullAt: null, isEmpty: false },
  level: { level: 1, xp: 0, intoLevel: 0, levelCost: 2, fraction: 0 },
  tier: { name: "", isLocal: false },
};

/**
 * How often vitals are re-read with no turn to prompt it. Slow, because it is
 * a floor and not the mechanism: a settled turn still refetches immediately.
 */
const POLL_MS = 15000;

/**
 * Energy and level, refetched whenever a turn settles -- and, slowly, when
 * one does not.
 *
 * The revision alone was enough while a turn was the only thing that could
 * move these numbers. It is not: the tier dropdown and the fill button both
 * change what this endpoint says without a turn happening, and there are two
 * surfaces here. The overlay is a separate WebView with its own copy of every
 * hook, so a tier switched in the conversation window is invisible to it --
 * the face stayed on the local shell for as long as nobody spoke, which is
 * precisely how it was found. Nothing pushes vitals, so the floor is a poll.
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

    const read = () =>
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

    read();
    const timer = setInterval(read, POLL_MS);

    return () => {
      abort.abort();
      clearInterval(timer);
    };
  }, [revision]);

  return { vitals, levelledUp };
}
