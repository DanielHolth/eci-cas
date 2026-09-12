"use client";

import type { Vitals } from "@/lib/useVitals";

/** The line shown when the meter is dry — Morrow saying what changed, in its own voice. */
export const TIRED_LINE = "[im tired and dumber now, using your local hardware to answer questions]";

function whenFull(fullAt: string | null): string {
  if (!fullAt) return "full";
  const hours = (new Date(fullAt).getTime() - Date.now()) / 3_600_000;
  if (hours <= 0) return "full";
  if (hours < 1) return `full in ${Math.max(1, Math.round(hours * 60))} min`;
  if (hours < 48) return `full in ${Math.round(hours)} h`;
  return `full in ${Math.round(hours / 24)} d`;
}

/**
 * Energy, directly under the face, with the level to its left.
 *
 * Two numbers and no units: the bar is a fuel gauge, not an accounting
 * screen, and a person who wants dollars has the cost readout in the drawer.
 * The tooltip carries the detail so the resting state stays one bar.
 */
export function EnergyMeter({ vitals }: { vitals: Vitals }) {
  const { energy, level } = vitals;
  const pct = Math.round(energy.fraction * 100);

  return (
    <div className="flex w-56 flex-col items-center gap-1">
      <div
        className="flex w-full items-center gap-2"
        title={`Energy ${pct}% — ${whenFull(energy.fullAt)}\nLevel ${level.level} · ${level.intoLevel}/${level.levelCost} to the next`}
      >
        <span className="w-6 shrink-0 text-right text-[11px] font-medium tabular-nums text-neutral-500 dark:text-neutral-400">
          {level.level}
        </span>

        <div
          role="meter"
          aria-label="Energy"
          aria-valuenow={pct}
          aria-valuemin={0}
          aria-valuemax={100}
          className="h-1.5 flex-1 overflow-hidden rounded-full bg-neutral-200 dark:bg-neutral-800"
        >
          <div
            className={`h-full rounded-full transition-[width,background-color] duration-700 ${
              energy.isEmpty
                ? "bg-neutral-400 dark:bg-neutral-600"
                : energy.fraction < 0.2
                  ? "bg-amber-500"
                  : "bg-emerald-500"
            }`}
            style={{ width: `${Math.max(energy.isEmpty ? 0 : 2, pct)}%` }}
          />
        </div>
      </div>

      {/* The level bar, thinner and quieter: it only ever fills, so it needs
          less of the eye than the one that can run out. */}
      <div className="h-0.5 w-full overflow-hidden rounded-full bg-neutral-200/60 dark:bg-neutral-800/60">
        <div
          className="h-full rounded-full bg-sky-400/70 transition-[width] duration-700"
          style={{ width: `${Math.round(level.fraction * 100)}%` }}
        />
      </div>
    </div>
  );
}
