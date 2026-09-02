"use client";

import { useState } from "react";
import type { ConsolidationEpoch } from "@/types/events";

/**
 * M4/M5 landed, but this stays client-side-only by choice: Archivist has
 * no `source_type: "ui_click"` ingestion path, and building one wasn't asked
 * for by the plan. Acknowledging a doodle is purely a display concern
 * (see README "Assumptions") — it doesn't need to reach the bus.
 */
function sendUiClickStub(epoch: ConsolidationEpoch) {
  console.log(
    `[stub] Perception.ingest(source_type: "ui_click", epoch_id: "${epoch.epochId}") — would fire once M4/M5 land`,
  );
}

export function ConsolidationDoodle({
  epoch,
  onAcknowledge,
}: {
  epoch: ConsolidationEpoch;
  onAcknowledge: (epochId: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const wasAlreadyAcknowledged = epoch.acknowledged;

  function handleClick() {
    setExpanded((v) => !v);
    // First click on this epoch is the real signal; repeats are dropped
    // client-side too, mirroring Archivist's own dedup.
    if (!wasAlreadyAcknowledged) {
      sendUiClickStub(epoch);
      onAcknowledge(epoch.epochId);
    }
  }

  return (
    <div className="flex flex-col items-start gap-1">
      <button
        type="button"
        onClick={handleClick}
        className="flex h-8 w-8 items-center justify-center rounded-full bg-indigo-500 text-white text-lg font-bold shadow hover:bg-indigo-600"
        aria-label="Something was learned — click to see what"
      >
        +
      </button>
      {expanded && (
        <div className="max-w-sm rounded-md border border-indigo-200 bg-indigo-50 p-2 text-xs text-indigo-900 dark:border-indigo-900 dark:bg-indigo-950 dark:text-indigo-100">
          {epoch.summary}
          {wasAlreadyAcknowledged && (
            <span className="block mt-1 text-indigo-400 dark:text-indigo-300">
              (already acknowledged — reopening is view-only, no new event fires)
            </span>
          )}
        </div>
      )}
    </div>
  );
}
