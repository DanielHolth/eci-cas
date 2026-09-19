"use client";

import { ResizableAside } from "@/components/ResizableAside";
import type { TurnRecord } from "@/types/events";

const MANUAL = {
  turn: 1,
  title: "Morrow User Manual",
  url: "https://github.com/DanielHolth/eci-cas/blob/main/docs/USER_MANUAL.md",
  summary: "Hotkeys, the conversation window, every panel and setting, toolkits and privacy.",
};

interface Row {
  turn: number;
  title: string;
  url: string;
  summary: string;
}

/** Newest first. Each row is a link a toolkit found, filed under the turn
 * that was told about it -- a search reports on the turn after the question,
 * so the number is the turn that used the link. Turn 1 is always the manual. */
function rowsOf(records: TurnRecord[]): Row[] {
  const found = records.flatMap((r) => (r.references ?? []).map((ref) => ({ turn: r.seq, ...ref })));
  return [...found.reverse(), MANUAL];
}

export function ReferencesPanel({ records, onClose }: { records: TurnRecord[]; onClose: () => void }) {
  const rows = rowsOf(records);

  return (
    <ResizableAside side="left" title="References" onClose={onClose}>
      <div className="flex h-full flex-col bg-white dark:bg-neutral-950">
        <ul className="flex-1 space-y-2 overflow-y-auto p-3">
          {rows.map((row, i) => (
            <li key={`${row.turn}-${i}`} className="rounded-xl border border-neutral-200 p-2 dark:border-neutral-800">
              <div className="flex items-baseline gap-2">
                <span className="rounded-full border border-neutral-300 px-1.5 py-0.5 font-mono text-[10px] text-neutral-600 dark:border-neutral-700 dark:text-neutral-300">
                  Turn {row.turn}
                </span>
                <a
                  href={row.url}
                  target="_blank"
                  rel="noreferrer noopener"
                  className="truncate text-sm font-medium text-blue-700 hover:underline dark:text-blue-400"
                >
                  {row.title}
                </a>
              </div>
              <p className="mt-1 break-all text-[10px] text-neutral-500 dark:text-neutral-500">{row.url}</p>
              <p className="mt-1 text-xs text-neutral-600 dark:text-neutral-400">{row.summary}</p>
            </li>
          ))}
        </ul>
      </div>
    </ResizableAside>
  );
}
