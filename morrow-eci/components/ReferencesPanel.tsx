"use client";

import { ResizableAside } from "@/components/ResizableAside";
import type { TurnRecord } from "@/types/events";

const MANUAL = {
  turn: 1,
  title: "Morrow User Manual",
  url: "https://github.com/DanielHolth/eci-cas/blob/main/docs/USER_MANUAL.md",
};

interface Group {
  turn: number;
  links: { title: string; url: string }[];
}

/** Newest first, one card per turn: a search reports on the turn after the
 * question, so the number is the turn that used the links. Titles only --
 * the link is the reference, the page says the rest. Turn 1 is always the manual. */
function groupsOf(records: TurnRecord[]): Group[] {
  const found = records
    .filter((r) => (r.references ?? []).length > 0)
    .map((r) => ({ turn: r.seq, links: (r.references ?? []).map(({ title, url }) => ({ title, url })) }));
  return [...found.reverse(), { turn: MANUAL.turn, links: [{ title: MANUAL.title, url: MANUAL.url }] }];
}

export function ReferencesPanel({ records, onClose }: { records: TurnRecord[]; onClose: () => void }) {
  const groups = groupsOf(records);

  return (
    <ResizableAside side="left" title="References" onClose={onClose}>
      <div className="flex h-full flex-col bg-white dark:bg-neutral-950">
        <ul className="flex-1 space-y-2 overflow-y-auto p-3">
          {groups.map((g, i) => (
            <li key={`${g.turn}-${i}`} className="rounded-xl border border-neutral-200 p-2 dark:border-neutral-800">
              <span className="rounded-full border border-neutral-300 px-1.5 py-0.5 font-mono text-[10px] text-neutral-600 dark:border-neutral-700 dark:text-neutral-300">
                Turn {g.turn}
              </span>
              <ul className="mt-1.5 space-y-0.5">
                {g.links.map((l) => (
                  <li key={l.url}>
                    <a
                      href={l.url}
                      target="_blank"
                      rel="noreferrer noopener"
                      className="block truncate text-sm text-blue-700 hover:underline dark:text-blue-400"
                    >
                      {l.title}
                    </a>
                  </li>
                ))}
              </ul>
            </li>
          ))}
        </ul>
      </div>
    </ResizableAside>
  );
}
