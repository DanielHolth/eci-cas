"use client";

import { useEffect, useState } from "react";
import { fetchKnobs } from "@/lib/api";

/**
 * The character ceiling the host applies to typed input, so the field can
 * count against it instead of letting the host swallow the overflow.
 *
 * A limit nobody is told about is the bug this replaces: a long paste went
 * in whole, came back truncated, and nothing on screen said so. Fetched
 * rather than hardcoded because it is a tier knob — the Debug panel can move
 * it mid-session, and `turns` re-reads it once a turn has settled so a drag
 * shows up on the field without a reload.
 */
export function usePerceptionLimit(turns: number): number | null {
  const [limit, setLimit] = useState<number | null>(null);

  useEffect(() => {
    let live = true;
    fetchKnobs()
      .then((k) => live && setLimit(k.perceptionChars))
      .catch(() => {});
    return () => {
      live = false;
    };
  }, [turns]);

  return limit;
}
