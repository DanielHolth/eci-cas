"use client";

import { useEffect, useState } from "react";

/** Roughly a spoken syllable rate. Not a measurement — just enough that a
 * one-word reply and a paragraph do not move the mouth for the same length
 * of time. */
const CHARS_PER_SECOND = 18;
const MIN_MS = 700;
const MAX_MS = 12_000;

/**
 * True for about as long as saying that text out loud would take.
 *
 * The mouth used to be driven by "is this the newest turn", so it animated
 * until something else happened — a persona left mid-sentence for as long as
 * nobody typed. Length is the cheapest honest proxy for duration, and it is
 * the thing the surface actually has.
 */
export function useSpeaking(text: string | undefined): boolean {
  // A boolean rather than "which text has finished": two consecutive turns
  // can carry the identical string, and comparing against it left the mouth
  // still marked finished from the previous one.
  const [speaking, setSpeaking] = useState(false);

  useEffect(() => {
    if (!text) {
      setSpeaking(false);
      return;
    }

    setSpeaking(true);
    const ms = Math.min(MAX_MS, Math.max(MIN_MS, (text.length / CHARS_PER_SECOND) * 1000));
    const timer = setTimeout(() => setSpeaking(false), ms);
    return () => clearTimeout(timer);
  }, [text]);

  return !!text && speaking;
}
