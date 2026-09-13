"use client";

import { useEffect, useState } from "react";
import { fetchKnobs, latestKnobs, subscribeKnobs } from "@/lib/api";
import type { Expression } from "@/types/events";

// The Mood knob pins the face. Neutral is absent on purpose: an untouched
// dial leaves the face to Impulse, the same way it leaves the prompt alone.
export const MOOD_FACE: Record<string, Expression> = {
  Maleficent: "scared",
  Sarcastic: "angry",
  Sad: "sad",
  Helpful: "warm",
  Ecstatic: "alert",
};

/**
 * How often the knobs are re-read. Faster than vitals, because this one is
 * answering a person who just turned a dial and is looking at the other
 * window to see whether it took.
 */
const POLL_MS = 5000;

/**
 * The expression the Mood knob pins the face to, or undefined when the dial
 * is untouched and Impulse still owns it.
 *
 * A hook rather than a constant read once, and a poll rather than the module
 * fan-out alone, for the same reason useVitals polls. `subscribeKnobs` is a
 * set of listeners inside one JavaScript context, and the overlay is a
 * different WebView from the conversation window -- so a mood chosen in the
 * Debug panel published to every listener that existed, all of which were in
 * the window that published it. The watermark stayed slate grey next to a
 * gold face and looked like a different persona.
 *
 * Mood is the whole of the visual half of the knobs. Tier reaches the face
 * too, as the shell it wears, but that arrives through vitals; the rest --
 * sentences, context, recall depth -- change what Morrow says, not what she
 * looks like.
 */
export function useMood(): Expression | undefined {
  const [mood, setMood] = useState(() => latestKnobs()?.mood ?? "");

  useEffect(() => {
    const off = subscribeKnobs((knobs) => setMood(knobs.mood));

    // fetchKnobs publishes, so the subscription above is what applies it --
    // here and in every other component watching, which is the point of the
    // fan-out staying in place underneath the poll.
    const read = () => {
      fetchKnobs().catch(() => {
        // Host down. The face keeps the mood it has rather than snapping back
        // to neutral, which would read as Morrow's mood changing.
      });
    };

    read();
    const timer = setInterval(read, POLL_MS);

    return () => {
      off();
      clearInterval(timer);
    };
  }, []);

  return MOOD_FACE[mood];
}
