"use client";

import { ResizableAside } from "@/components/ResizableAside";
import { useState } from "react";
import type { TurnRecord } from "@/types/events";

const KIND_STYLE = {
  learned: { dot: "bg-emerald-500", label: "Learned" },
  // Its own row rather than a shade of Learned. Hindsight is the persona
  // second-guessing a turn it has already had, which is a thought about the
  // conversation -- not a fact taken out of it and kept.
  hindsight: { dot: "bg-amber-500", label: "Hindsight" },
  reflection: { dot: "bg-indigo-500", label: "Reflection" },
} as const;

type Kind = keyof typeof KIND_STYLE;

interface Thought {
  id: string;
  kind: Kind;
  text: string;
  correlationId: string;
}

/** Newest first: what Archivist wrote ("Learned") and what Reflection
 * noticed and what Hindsight went back over, across the whole session.
 *
 * What Recall read is deliberately absent. Recall fires on every turn and
 * returns its depth whether or not the turn needed anything, so the reads
 * were most of the panel and most of them were beside the point -- the
 * thoughts a person wants to see are the ones the system arrived at, not
 * the rows it happened to touch getting there. The reads are still whole in
 * the event drawer, which is where you go when you want to know why a turn
 * answered the way it did. */
function thoughtsOf(records: TurnRecord[]): Thought[] {
  const out: Thought[] = [];
  for (const r of [...records].reverse()) {
    r.writes.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-learned-${i}`, kind: "learned", text: t, correlationId: r.correlationId }),
    );
    r.hindsight.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-hindsight-${i}`, kind: "hindsight", text: t, correlationId: r.correlationId }),
    );
    if (r.idea) {
      out.push({ id: `${r.correlationId}-reflection-idea`, kind: "reflection", text: r.idea, correlationId: r.correlationId });
    }
    r.passages.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-reflection-${i}`, kind: "reflection", text: t, correlationId: r.correlationId }),
    );
  }
  return out;
}

/** How many ideas Reflection has pushed — the count the collapsed toggle's
 * red badge shows, so the panel doesn't have to be open to notice one. */
export function reflectionCount(records: TurnRecord[]): number {
  return records.filter((r) => r.idea).length;
}

export function ThoughtsPanel({
  records,
  onClose,
  onOpen,
}: {
  records: TurnRecord[];
  onClose: () => void;
  onOpen: (correlationId: string) => void;
}) {
  const thoughts = thoughtsOf(records);

  // Overrides only — anything not yet clicked falls back to its kind's
  // default. Learned is practically the utterance restated, one per turn,
  // and arriving open turned the panel into a wall of near-duplicate prose;
  // Hindsight and Reflection are rarer and worth seeing at a glance.
  const [overrides, setOverrides] = useState<Map<string, boolean>>(new Map());

  function defaultOpen(kind: Kind): boolean {
    return kind !== "learned";
  }

  function toggle(id: string, kind: Kind) {
    setOverrides((current) => {
      const next = new Map(current);
      const open = next.has(id) ? next.get(id)! : defaultOpen(kind);
      next.set(id, !open);
      return next;
    });
  }

  return (
    <ResizableAside side="left" title="Thoughts" onClose={onClose}>
      <ul className="flex flex-col gap-1.5 p-2">
        {thoughts.length === 0 && (
          <li className="px-2 py-4 text-xs text-neutral-400 dark:text-neutral-500">
            Nothing learned or reflected on yet.
          </li>
        )}
        {thoughts.map((t) => {
          const style = KIND_STYLE[t.kind];
          const isOpen = overrides.has(t.id) ? overrides.get(t.id)! : defaultOpen(t.kind);
          return (
            <li key={t.id} className="rounded-2xl border border-neutral-200 bg-white shadow-sm dark:border-neutral-700 dark:bg-neutral-900">
              <button
                type="button"
                onClick={() => toggle(t.id, t.kind)}
                className="flex w-full items-start gap-2 px-3 py-1.5 text-left text-sm"
              >
                <span className={`mt-1 h-2.5 w-2.5 shrink-0 rounded-full ${style.dot}`} />
                <span className="shrink-0 font-medium text-neutral-500 dark:text-neutral-400">{style.label}:</span>
                <span className={`min-w-0 flex-1 text-neutral-700 dark:text-neutral-200 ${isOpen ? "whitespace-pre-wrap break-words" : "truncate"}`}>
                  {t.text}
                </span>
              </button>
              {isOpen && (
                <div className="flex justify-end px-3 pb-1.5">
                  <button
                    type="button"
                    onClick={() => onOpen(t.correlationId)}
                    className="text-xs text-neutral-400 underline decoration-dotted hover:text-neutral-700 dark:hover:text-neutral-200"
                  >
                    open turn
                  </button>
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </ResizableAside>
  );
}
