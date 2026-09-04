"use client";

import { EventLogEntry } from "@/components/EventLogEntry";
import type { TurnRecord } from "@/types/events";

/**
 * Everything the console prints about a turn, on the surface. Newest first,
 * scrolling independently of the avatar column — a person watching the face
 * should not have to lose it to read what the faculties did. Below `lg` it
 * overlays instead of splitting: a fixed-width pane taking half a narrow
 * window leaves neither column readable.
 */
export function EventLog({
  records,
  openCorrelationId,
  openSignal,
  onClose,
}: {
  records: TurnRecord[];
  openCorrelationId?: string;
  openSignal?: number;
  onClose: () => void;
}) {
  return (
    <aside className="fixed inset-y-0 right-0 z-20 flex h-full w-80 shrink-0 flex-col border-l border-neutral-200 bg-white shadow-xl lg:static lg:z-auto lg:shadow-none dark:border-neutral-800 dark:bg-neutral-950">
      <div className="flex items-center justify-between border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
        <h2 className="text-sm font-semibold text-neutral-800 dark:text-neutral-100">History</h2>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close the history log"
          className="rounded px-2 text-neutral-400 hover:text-neutral-800 dark:hover:text-neutral-100"
        >
          ×
        </button>
      </div>

      <ol className="flex-1 overflow-y-auto text-xs">
        {records.length === 0 && (
          <li className="px-3 py-4 text-neutral-400 dark:text-neutral-500">Nothing has happened yet.</li>
        )}
        {[...records].reverse().map((record) => (
          <EventLogEntry
            key={record.correlationId}
            record={record}
            openSignal={record.correlationId === openCorrelationId ? openSignal : undefined}
          />
        ))}
      </ol>
    </aside>
  );
}
