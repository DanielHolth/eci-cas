"use client";

import { useState } from "react";
import type { SubstrateCall, TurnRecord } from "@/types/events";

function stamp(iso: string): { date: string; time: string } {
  const at = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return {
    date: `${at.getFullYear()}.${pad(at.getMonth() + 1)}.${pad(at.getDate())}`,
    time: `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}`,
  };
}

/** Four decimals is enough for a turn and not enough for a fraction of a
 * tenth of a cent, which is what a cheap model on a short turn actually
 * costs. A rendered $0.0000 read as free, so anything that rounds away
 * says so instead. Null — nothing priced the turn at all — is an em dash. */
function money(total: number | null): string {
  if (total === null) return "—";
  if (total === 0) return "$0";
  if (total < 0.0001) return "<$0.0001";
  return `$${total.toFixed(4)}`;
}

/** The addends are the individual calls; the total is the turn's wall-clock.
 * The fan-out is concurrent, so a summed total would claim more time than the
 * turn actually took. */
function latency(calls: SubstrateCall[], wallClockMs: number): string {
  const addends = calls.map((c) => Math.round(c.latencyMs)).join(" + ");
  const total = `${Math.round(wallClockMs)} ms wall-clock`;
  return addends ? `${addends} → ${total}` : total;
}

function Line({ agent, children }: { agent: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-2 py-0.5">
      <span className="shrink-0 font-medium text-neutral-500 dark:text-neutral-400">{agent}:</span>
      <span className="min-w-0 break-words text-neutral-800 dark:text-neutral-200">{children}</span>
    </div>
  );
}

/** Only an event that started from something perceived is waiting on a
 * reply. A Reflection flush never gets an Action envelope, so a permanent
 * "still arriving" marker on it would be a lie. */
function stillArriving(record: TurnRecord): boolean {
  return !!record.perception && !record.concluded;
}

/** What the row says when collapsed. An event with no perception is the
 * persona thinking on its own time — a Reflection flush that surfaced no
 * idea still spent a call, so it is named by the faculties that ran rather
 * than left as a blank line. */
function headlineOf(record: TurnRecord): string {
  const said = record.perception ?? record.idea ?? record.intent;
  if (said) return said;

  const ran = [...new Set(record.calls.map((c) => c.agent))];
  return ran.length > 0 ? `${ran.join(", ")} — thinking on its own` : "…";
}

/**
 * One event, in a fixed slot order rather than arrival order — the fan-out is
 * concurrent by design, so what arrived first says nothing about what a
 * person should read first. A slot with nothing in it is not drawn.
 */
export function EventLogEntry({ record, openSignal }: { record: TurnRecord; openSignal?: number }) {
  // Open by default. The drawer exists to be read, and a wall of collapsed
  // one-line summaries made seeing a turn a click per event.
  const [expanded, setExpanded] = useState(true);
  const [showReflection, setShowReflection] = useState(false);
  const [lastSignal, setLastSignal] = useState(openSignal);

  // A click on the persona's thought bubble opens its own entry.
  if (openSignal !== lastSignal) {
    setLastSignal(openSignal);
    setExpanded(true);
  }

  const { date, time } = stamp(record.startedAt);
  const headline = headlineOf(record);

  return (
    <li className="border-b border-neutral-200 dark:border-neutral-800">
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        className="flex w-full flex-col items-start gap-0.5 px-3 py-2 text-left hover:bg-neutral-100 dark:hover:bg-neutral-900"
      >
        <span className="font-mono text-[11px] text-neutral-400 dark:text-neutral-500">
          Event {String(record.seq).padStart(3, "0")} · {date} · {time}
          {record.selfTriggered && " · self"}
          {stillArriving(record) && " · …"}
        </span>
        <span className="line-clamp-1 text-neutral-700 dark:text-neutral-300">{headline}</span>
      </button>

      {expanded && (
        <div className="px-3 pb-3 font-mono text-[11px] leading-relaxed">
          {record.perception && (
            <Line agent={record.selfTriggered ? "Idea" : "Perception"}>{record.perception}</Line>
          )}
          {record.impulse && <Line agent="Impulse">{record.impulse}</Line>}
          {record.pairs.map((pair, i) => (
            <Line key={`pair-${i}`} agent={`Librarian-${i + 1}`}>
              {pair}
            </Line>
          ))}
          {record.reads.map((read, i) => (
            <Line key={`read-${i}`} agent={`Recall-${i + 1}`}>
              {read}
            </Line>
          ))}
          {record.hindsight.map((note, i) => (
            <Line key={`note-${i}`} agent={`Hindsight-${i + 1}`}>
              {note}
            </Line>
          ))}
          {record.intent && <Line agent="Intent">{record.intent}</Line>}
          {record.verdict && (
            <Line agent="Security">
              <span className={record.verdict === "red" ? "text-red-600 dark:text-red-400" : "text-amber-600 dark:text-amber-400"}>
                {record.verdict}
                {record.concern && ` — ${record.concern}`}
              </span>
            </Line>
          )}
          {record.writes.map((write, i) => (
            <Line key={`write-${i}`} agent={`Archivist-${i + 1}`}>
              {write}
            </Line>
          ))}

          {(record.passages.length > 0 || record.idea) && (
            <div className="py-0.5">
              <button
                type="button"
                onClick={() => setShowReflection((v) => !v)}
                className="text-neutral-500 underline decoration-dotted hover:text-neutral-800 dark:text-neutral-400 dark:hover:text-neutral-200"
              >
                Reflection ({record.passages.length + (record.idea ? 1 : 0)})
              </button>
              {showReflection && (
                <div className="mt-1 border-l border-neutral-300 pl-2 dark:border-neutral-700">
                  {record.passages.map((passage, i) => (
                    <Line key={`passage-${i}`} agent={`Passage-${i + 1}`}>
                      {passage}
                    </Line>
                  ))}
                  {record.idea && <Line agent="Idea">{record.idea}</Line>}
                </div>
              )}
            </div>
          )}

          {record.calls.length > 0 && (
            <>
              <Line agent="Cost">
                <span className="text-neutral-800 dark:text-neutral-200">event {money(record.cost)}</span>
                <span className="text-neutral-400 dark:text-neutral-500">
                  {" · "}session {money(record.sessionCost)}
                  {" · "}total {money(record.totalCost)}
                </span>
                <span className="ml-1 text-neutral-400 dark:text-neutral-500">
                  ({record.calls.map((c) => c.label ?? c.agent).join(", ")})
                </span>
              </Line>
              <Line agent="Latency">{latency(record.calls, record.wallClockMs)}</Line>
            </>
          )}
          {record.calls.some((c) => c.degraded) && (
            <Line agent="Degraded">
              <span className="text-amber-600 dark:text-amber-400">
                {record.calls
                  .filter((c) => c.degraded)
                  .map((c) => `${c.agent} ${c.degraded}`)
                  .join(", ")}
              </span>
            </Line>
          )}
        </div>
      )}
    </li>
  );
}
