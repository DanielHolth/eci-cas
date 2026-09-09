"use client";

import { useEffect, useState } from "react";
import { fetchKnobs, latestKnobs, subscribeKnobs } from "@/lib/api";

/**
 * The character ceiling the host applies to typed input, so the field can
 * count against it instead of letting the host swallow the overflow.
 *
 * A limit nobody is told about is the bug this replaces: a long paste went
 * in whole, came back truncated, and nothing on screen said so.
 *
 * Subscribed rather than polled. Re-reading once a turn had settled meant a
 * drag on the Debug panel's slider did not reach the field until the next
 * turn finished — so a field still capped at 64 refused the sentence the
 * knob had just made room for. The knob feed publishes on every answer the
 * host gives, whoever asked for it, which makes the drag immediate. `turns`
 * still triggers a re-read, for the changes this surface did not make.
 */
export function usePerceptionLimit(turns: number): number | null {
  const [limit, setLimit] = useState<number | null>(latestKnobs()?.perceptionChars ?? null);

  useEffect(() => subscribeKnobs((knobs) => setLimit(knobs.perceptionChars)), []);

  useEffect(() => {
    fetchKnobs().catch(() => {});
  }, [turns]);

  return limit;
}
