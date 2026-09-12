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
  const total = `${Math.round(wallClockMs)} ms`;
  return addends ? `${addends} → ${total}` : total;
}

/** Token usage rolled up per model, not per call: what a person wants off
 * this panel is "which model did the work and what did it read", and on a
 * mixed tier the same model backs several agents. Sorted by spend so the
 * expensive one is never below the fold. A call whose provider never
 * reported a model is grouped under "unreported" rather than dropped. */
function byModel(calls: SubstrateCall[]) {
  const rows = new Map<string, { provider: string; model: string; calls: number; in: number; out: number; total: number; cost: number | null }>();
  for (const c of calls) {
    const provider = c.provider ?? "?";
    const model = c.model ?? "unreported";
    const key = `${provider}/${model}`;
    const row = rows.get(key) ?? { provider, model, calls: 0, in: 0, out: 0, total: 0, cost: null };
    row.calls += 1;
    row.in += c.promptTokens ?? 0;
    row.out += c.completionTokens ?? 0;
    row.total += c.tokens ?? 0;
    if (c.cost !== null) row.cost = (row.cost ?? 0) + c.cost;
    rows.set(key, row);
  }
  return [...rows.values()].sort((a, b) => (b.cost ?? 0) - (a.cost ?? 0) || b.total - a.total);
}

/** Local hardware costs nothing and is worth seeing at a glance — the whole
 * question on a mixed tier is which half of the fan-out left the machine. */
const LOCAL = new Set(["local", "ollama", "llamacpp", "mock"]);

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
          Turn {String(record.seq).padStart(3, "0")} · {date} · {time}
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
          {/* Learned-N: noise here, identical to what Archivist/Scribe wrote —
              see it in the Thoughts panel instead. */}

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
              {/* Cost: noise here — Totals below already gives the number that matters. */}
              <Line agent="Totals">
                <span className="text-neutral-800 dark:text-neutral-200">event {money(record.cost)}</span>
                <span className="text-neutral-400 dark:text-neutral-500">
                  {" · "}session {money(record.sessionCost)}
                  {" · "}total {money(record.totalCost)}
                </span>
              </Line>
              <Line agent="Latency">{latency(record.calls, record.wallClockMs)}</Line>
              <Line agent="Models">
                <span className="flex flex-col gap-0.5">
                  {byModel(record.calls).map((r) => (
                    <span key={`${r.provider}/${r.model}`} className="flex flex-wrap gap-x-2 font-mono text-[11px]">
                      <span className={LOCAL.has(r.provider) ? "text-emerald-600 dark:text-emerald-400" : "text-sky-600 dark:text-sky-400"}>
                        {LOCAL.has(r.provider) ? "local" : "remote"}
                      </span>
                      <span className="text-neutral-800 dark:text-neutral-200">{r.provider}/{r.model}</span>
                      <span className="text-neutral-400 dark:text-neutral-500">
                        ×{r.calls}
                        {" · "}
                        {r.in || r.out ? `${r.in} in / ${r.out} out` : `${r.total} tok`}
                        {" · "}
                        {money(r.cost)}
                      </span>
                    </span>
                  ))}
                </span>
              </Line>
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
