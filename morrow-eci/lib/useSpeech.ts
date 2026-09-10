"use client";

import { useEffect, useRef, useState } from "react";
import type { TurnEvent } from "@/types/events";

/**
 * Says every reply aloud, in the order the replies arrived.
 *
 * **Why a queue and not a call per turn.** Reflection pushes its own ideas
 * back onto perception (lib/useEciStream.ts), so the persona can conclude two
 * turns without a person typing between them. Speaking each one as it lands
 * would have the second cut the first off mid-sentence -- browsers cancel the
 * current utterance when a new one is spoken with `cancel`, and queue it only
 * if you let the queue do the queueing. Here the queue is explicit: one
 * utterance is in flight at a time, and the next starts on its `onend`.
 *
 * **What counts as new.** A turn is spoken once, keyed by its turnId. The
 * stream rebuilds the whole turns array on every envelope, so identity has to
 * come from the turn, not from the text -- two consecutive replies can be
 * the same string, and the same reply is re-delivered on every later envelope
 * of the same turn.
 *
 * **The backlog is never spoken.** Whatever is already on screen when this
 * mounts is marked as said. Switching profiles remounts Conversation, and a
 * remount that read twenty turns aloud would be a worse bug than silence.
 *
 * The returned boolean is what drives the avatar's mouth: true while an
 * utterance is actually in flight, which is real timing now rather than the
 * characters-per-second estimate this replaces.
 */
export function useSpeech(turns: TurnEvent[], enabled = true): boolean {
  const [speaking, setSpeaking] = useState(false);

  const said = useRef<Set<string>>(new Set());
  const queue = useRef<string[]>([]);
  const busy = useRef(false);
  const primed = useRef(false);

  // Marking the backlog has to happen before the first drain, and it has to
  // happen once -- doing it in the queueing effect would also swallow the
  // first real reply when it arrives in the same commit.
  if (!primed.current) {
    primed.current = true;
    for (const turn of turns) {
      if (turn.output?.text) {
        said.current.add(turn.turnId);
      }
    }
  }

  useEffect(() => {
    const synth = typeof window === "undefined" ? undefined : window.speechSynthesis;
    if (!synth) return;

    function drain() {
      const next = queue.current.shift();
      if (next === undefined) {
        busy.current = false;
        setSpeaking(false);
        return;
      }

      busy.current = true;
      const utterance = new SpeechSynthesisUtterance(next);
      utterance.onstart = () => setSpeaking(true);
      // Both hands go to the same place: an utterance that errors (no voice
      // installed, autoplay refused) must not wedge the queue shut.
      utterance.onend = drain;
      utterance.onerror = drain;
      synth!.speak(utterance);
    }

    for (const turn of turns) {
      const text = turn.output?.text;
      if (!text || said.current.has(turn.turnId)) {
        continue;
      }
      said.current.add(turn.turnId);
      if (enabled) {
        queue.current.push(text);
      }
    }

    if (enabled && !busy.current && queue.current.length > 0) {
      drain();
    }
  }, [turns, enabled]);

  // Leaving an utterance in flight would go on talking over the next profile,
  // since speechSynthesis is a window-wide singleton and outlives this mount.
  useEffect(() => {
    return () => {
      window.speechSynthesis?.cancel();
    };
  }, []);

  return speaking;
}
