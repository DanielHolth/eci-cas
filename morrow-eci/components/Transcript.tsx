"use client";

import { useEffect, useRef } from "react";
import { SpeechBubble } from "@/components/SpeechBubble";
import { Utterance } from "@/components/Utterance";
import type { TurnEvent } from "@/types/events";

/**
 * The conversation as a conversation: person on the right, persona on the
 * left, oldest at the top, scrolled to the newest.
 *
 * It replaced a single pair of bubbles showing only the latest turn, which
 * had two problems worth naming. Everything said more than one turn ago was
 * simply gone — there was no reading back. And because Reflection pushes its
 * own ideas onto the same feed, the persona thinking to itself counted as a
 * newer turn and wiped the reply the person was still reading. Ideas are not
 * in here at all now; they belong over the avatar's head, where a thought
 * nobody addressed to anyone actually belongs.
 */
export function Transcript({ turns }: { turns: TurnEvent[] }) {
  const bottom = useRef<HTMLDivElement>(null);
  const spoken = turns.filter((t) => t.input || t.output);

  // Follows the newest line. The dependency is the count rather than the
  // array, so a turn filling in its own bundle does not yank the view.
  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [spoken.length]);

  // Not `null`: the composer below is pinned to the bottom by this element's
  // `flex-1`, so an empty transcript that renders nothing drags the input box
  // up under the avatar and leaves the rest of the screen blank.
  if (spoken.length === 0) {
    return <div className="w-full flex-1" />;
  }

  return (
    <div className="flex w-full min-h-0 flex-1 flex-col gap-3 overflow-y-auto">
      {spoken.map((turn) => (
        <div key={turn.turnId} className="flex flex-col gap-3">
          {turn.input && <Utterance text={turn.input} />}
          {turn.output && (
            <div className="flex justify-start">
              <SpeechBubble output={turn.output} />
            </div>
          )}
        </div>
      ))}
      <div ref={bottom} />
    </div>
  );
}
