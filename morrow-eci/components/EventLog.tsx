"use client";

import { ResizableAside } from "@/components/ResizableAside";
import { EventLogEntry } from "@/components/EventLogEntry";
import type { TurnRecord } from "@/types/events";

/**
 * Everything the console prints about a turn, on the surface. Newest first,
 * scrolling independently of the avatar column — a person watching the face
 * should not have to lose it to read what the faculties did.
 *
 * Event log only — the knobs that used to live here moved to the Settings
 * tab on the left, see SettingsPanel.tsx, which leaves this drawer the one
 * thing its title says it is.
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
