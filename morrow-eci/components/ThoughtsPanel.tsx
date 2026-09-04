"use client";

import { ResizableAside } from "@/components/ResizableAside";
import { useState } from "react";
import type { TurnRecord } from "@/types/events";

const KIND_STYLE = {
  recalled: { dot: "bg-orange-500", label: "Recalled" },
  learned: { dot: "bg-emerald-500", label: "Learned" },
  reflection: { dot: "bg-indigo-500", label: "Reflection" },
} as const;

type Kind = keyof typeof KIND_STYLE;

interface Thought {
  id: string;
  kind: Kind;
  text: string;
  correlationId: string;
}

/** Newest first: what Recall read, Archivist wrote ("Learned"), and
 * Reflection noticed, across the whole session — the same terse pill the
 * center column used to show for one turn's bundle, now a running list so a
 * fact recalled three turns ago is still readable. */
function thoughtsOf(records: TurnRecord[]): Thought[] {
  const out: Thought[] = [];
  for (const r of [...records].reverse()) {
    r.reads.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-recalled-${i}`, kind: "recalled", text: t, correlationId: r.correlationId }),
    );
    r.writes.forEach((t, i) =>
      out.push({ id: `${r.correlationId}-learned-${i}`, kind: "learned", text: t, correlationId: r.correlationId }),
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
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  function toggle(id: string) {
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  return (
    <ResizableAside side="left" title="Thoughts" onClose={onClose}>
      <ul className="flex flex-col gap-1.5 p-2">
        {thoughts.length === 0 && (
          <li className="px-2 py-4 text-xs text-neutral-400 dark:text-neutral-500">
            Nothing recalled, learned, or reflected on yet.
          </li>
        )}
        {thoughts.map((t) => {
          const style = KIND_STYLE[t.kind];
          const isOpen = expanded.has(t.id);
          return (
            <li key={t.id} className="rounded-2xl border border-neutral-200 bg-white shadow-sm dark:border-neutral-700 dark:bg-neutral-900">
              <button
                type="button"
                onClick={() => toggle(t.id)}
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
                    open event
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
