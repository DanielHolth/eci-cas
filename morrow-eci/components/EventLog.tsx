"use client";

import { ResizableAside } from "@/components/ResizableAside";
import { EventLogEntry } from "@/components/EventLogEntry";
import { KnobsPanel } from "@/components/KnobsPanel";
import type { TurnRecord } from "@/types/events";

/**
 * Everything the console prints about a turn, on the surface. Newest first,
 * scrolling independently of the avatar column — a person watching the face
 * should not have to lose it to read what the faculties did.
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
    <ResizableAside side="right" title="Debug" onClose={onClose}>
      <KnobsPanel />
      <ol className="text-xs">
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
    </ResizableAside>
  );
}
