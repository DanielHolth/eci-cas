"use client";

import { useState } from "react";
import type { SecurityOutcome } from "@/types/events";

const VERDICT_STYLE: Record<SecurityOutcome["verdict"], string> = {
  green: "bg-green-500",
  yellow: "bg-yellow-400",
  red: "bg-red-500",
};

/** Only renders anything for yellow/red passes — green is non-blocking
 * and, per the spec, doesn't need its own icon. Click reveals what
 * Intent tried and why it was stopped (Intent's Revise/refusal text). */
export function SecurityIcon({ outcomes }: { outcomes: SecurityOutcome[] }) {
  const [open, setOpen] = useState<number | null>(null);
  const flagged = outcomes.filter((o) => o.verdict !== "green");
  if (flagged.length === 0) return null;

  return (
    <div className="flex flex-col gap-2 items-start">
      {flagged.map((o, i) => (
        <div key={i} className="flex flex-col gap-1">
          <button
            type="button"
            onClick={() => setOpen(open === i ? null : i)}
            className={`flex items-center gap-2 rounded-full px-3 py-1 text-xs font-medium text-white ${VERDICT_STYLE[o.verdict]}`}
          >
            <span aria-hidden>⚠</span>
            Security: {o.verdict} — pass {i + 1}
          </button>
          {open === i && o.detail && (
            <div className="max-w-sm rounded-md border border-neutral-200 bg-neutral-50 p-2 text-xs text-neutral-600">
              {o.detail}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
