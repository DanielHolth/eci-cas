"use client";

import { useCallback, useEffect, useState } from "react";

/** Default, min and max for the fade slider. Default is twice the number the
 * three bubbles (heard, idea, reply) used to be hardcoded to before this was
 * a setting -- a person who never touches the slider gets a slower fade than
 * before, on the theory that the old number was tuned for reading speed
 * alone and not for "is she about to say something else". Min/max are
 * generous on purpose: this is a comfort setting, not a tuned constant, and
 * the person asking for it is the one who gets to find their own number. */
export const FADE_MS_DEFAULT = 12000;
export const FADE_MS_MIN = 2000;
export const FADE_MS_MAX = 30000;

const STORAGE_KEY = "morrow.fadeMs";

function read(): number {
  try {
    const saved = window.localStorage.getItem(STORAGE_KEY);
    const parsed = saved === null ? NaN : Number(saved);
    return Number.isFinite(parsed) ? Math.min(FADE_MS_MAX, Math.max(FADE_MS_MIN, parsed)) : FADE_MS_DEFAULT;
  } catch {
    return FADE_MS_DEFAULT;
  }
}

/**
 * How long the three bubbles around the avatar (heard, idea, reply) stay up
 * once they have something to show, shared between whichever surface has the
 * slider (Conversation.tsx) and the one that actually renders the bubbles
 * (overlay/page.tsx) -- two different windows over the same localStorage, so
 * a drag in one is picked up by the other via the "storage" event rather
 * than a prop, since neither owns the other.
 */
export function useFadeMs(): [number, (ms: number) => void] {
  const [fadeMs, setFadeMsState] = useState(FADE_MS_DEFAULT);

  useEffect(() => {
    setFadeMsState(read());
    const onStorage = (e: StorageEvent) => {
      if (e.key === STORAGE_KEY) setFadeMsState(read());
    };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, []);

  const setFadeMs = useCallback((ms: number) => {
    const clamped = Math.min(FADE_MS_MAX, Math.max(FADE_MS_MIN, ms));
    setFadeMsState(clamped);
    try {
      window.localStorage.setItem(STORAGE_KEY, String(clamped));
    } catch {
      // Nothing to persist to — the choice still holds for this window.
    }
  }, []);

  return [fadeMs, setFadeMs];
}
