"use client";

import { useEffect, useRef } from "react";
import type { TurnEvent } from "@/types/events";

/**
 * What the persona thought while nobody was asking, kept above its head.
 *
 * Reflection's ideas used to land in the conversation, which put them in the
 * wrong register twice over: an idea is not addressed to anyone, and it
 * arrived as a newer turn than the reply the person was still reading and
 * replaced it. Here they accumulate in their own scrollable strip, so
 * several are readable at once and none of them displaces anything said.
 *
 * Each one still opens its event in the history drawer — an idea is the one
 * thing on this surface whose provenance a person is most likely to want.
 */
export function IdeaStream({ turns, onOpen }: { turns: TurnEvent[]; onOpen: (turnId: string) => void }) {
  const bottom = useRef<HTMLDivElement>(null);
  const ideas = turns.filter((t) => t.idea);

  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [ideas.length]);

  if (ideas.length === 0) {
    return null;
  }

  return (
    <div className="flex w-full max-w-md max-h-28 flex-col gap-1 overflow-y-auto rounded-xl border border-dashed border-indigo-200 bg-indigo-50/50 p-2 dark:border-indigo-900 dark:bg-indigo-950/30">
      {ideas.map((turn) => (
        <button
          key={turn.turnId}
          type="button"
          onClick={() => onOpen(turn.turnId)}
          className="rounded-lg px-2 py-1 text-left text-xs italic text-indigo-900 hover:bg-indigo-100 dark:text-indigo-100 dark:hover:bg-indigo-900"
        >
          <span className="mr-1 not-italic text-indigo-400">◌</span>
          {turn.idea}
        </button>
      ))}
      <div ref={bottom} />
    </div>
  );
}
