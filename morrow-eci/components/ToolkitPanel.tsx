"use client";

import { useEffect, useMemo, useState } from "react";
import { ResizableAside } from "@/components/ResizableAside";
import { API_BASE } from "@/lib/api";
import type { TurnRecord } from "@/types/events";

type ToolkitStatus = "ready" | "disabled" | "pending" | "unavailable" | "invalid";

interface ToolkitItem {
  file: string;
  pack: string | null;
  name: string;
  description: string | null;
  capability: string | null;
  risk: string | null;
  options: unknown;
  status: ToolkitStatus;
  reason: string | null;
}

interface LastRun {
  result: string;
  at: string;
  turn: number;
}

/** Latest "name: ok" / "name: failed" per toolkit, read off the turn log --
 * a run reports on the turn after it, so that is the turn shown. */
function lastRuns(records: TurnRecord[]): Map<string, LastRun> {
  const runs = new Map<string, LastRun>();
  for (const r of records) {
    for (const line of r.toolkits ?? []) {
      const [name, result] = line.split(": ");
      if (name) runs.set(name, { result: result ?? "", at: r.endedAt, turn: r.seq });
    }
  }
  return runs;
}

export function ToolkitPanel({
  enablePowerShell,
  records,
  onClose,
}: {
  enablePowerShell: boolean;
  records: TurnRecord[];
  onClose: () => void;
}) {
  const [items, setItems] = useState<ToolkitItem[]>([]);
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  const load = () =>
    fetch(`${API_BASE}/api/toolkits`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
      .then((data: ToolkitItem[]) => setItems(data))
      .catch(() => setFailed(true));

  useEffect(() => {
    load();
  }, []);

  /** Approve is the only way a manifest gets turned on -- always a person's click, on the raw verb. */
  const act = (file: string, action: "approve" | "discard") =>
    fetch(`${API_BASE}/api/toolkits/${encodeURIComponent(file)}/${action}`, { method: "POST" }).then(load);

  const runs = useMemo(() => lastRuns(records), [records]);

  const tools = items.map((t) => ({
    ...t,
    status: t.name === "powershell" && !enablePowerShell ? ("disabled" as const) : t.status,
  }));
  const selected = tools.find((t) => t.name === selectedName) ?? tools[0];
  const run = selected ? runs.get(selected.name) : undefined;

  return (
    <ResizableAside side="left" title="Toolkit" onClose={onClose}>
      <div className="flex h-full flex-col bg-white dark:bg-neutral-950">
        <div className="border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
          <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-neutral-500 dark:text-neutral-400">
            Available toolkits
          </p>
        </div>

        <div className="flex flex-1 flex-col gap-3 overflow-y-auto p-3">
          {failed && <p className="text-xs text-red-600 dark:text-red-400">Could not reach the Host.</p>}
          <ul className="space-y-2">
            {tools.map((tool) => (
              <li key={tool.file}>
                <button
                  type="button"
                  onClick={() => setSelectedName(tool.name)}
                  className={`w-full rounded-xl border p-2 text-left transition ${
                    selected?.name === tool.name
                      ? "border-neutral-900 bg-neutral-100 dark:border-neutral-600 dark:bg-neutral-900"
                      : "border-neutral-200 bg-white hover:bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950 dark:hover:bg-neutral-900"
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-sm font-medium capitalize text-neutral-800 dark:text-neutral-100">{tool.name}</span>
                    <span className="rounded-full border border-neutral-300 px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-neutral-700 dark:border-neutral-700 dark:text-neutral-300">
                      {tool.status}
                    </span>
                  </div>
                  <p className="mt-1 text-xs text-neutral-600 dark:text-neutral-400">{tool.description ?? tool.reason}</p>
                </button>
              </li>
            ))}
          </ul>

          {selected && (
            <div className="rounded-xl border border-neutral-200 bg-neutral-50 p-3 dark:border-neutral-800 dark:bg-neutral-900">
              <h3 className="mb-2 text-sm font-semibold capitalize text-neutral-800 dark:text-neutral-100">{selected.name}</h3>
              <p className="text-xs leading-5 text-neutral-600 dark:text-neutral-300">{selected.description}</p>
              <dl className="mt-3 space-y-2 text-xs text-neutral-600 dark:text-neutral-300">
                <div className="flex justify-between gap-4">
                  <dt>Current status</dt>
                  <dd className="font-medium text-neutral-800 dark:text-neutral-100">{selected.status}</dd>
                </div>
                <div className="flex justify-between gap-4">
                  <dt>Runs</dt>
                  <dd className="font-medium text-neutral-800 dark:text-neutral-100">
                    {selected.capability ?? "?"}{selected.risk ? ` (${selected.risk})` : ""}{selected.pack ? ` from ${selected.pack}` : ""}
                  </dd>
                </div>
                {selected.reason && (
                  <div className="flex justify-between gap-4">
                    <dt>Why</dt>
                    <dd className="text-right font-medium text-neutral-800 dark:text-neutral-100">{selected.reason}</dd>
                  </div>
                )}
                <div className="flex justify-between gap-4">
                  <dt>Last run</dt>
                  <dd className="font-medium text-neutral-800 dark:text-neutral-100">
                    {run ? `${run.result} (turn ${run.turn})` : "not run yet"}
                  </dd>
                </div>
                {run && (
                  <div className="flex justify-between gap-4">
                    <dt>Updated</dt>
                    <dd className="font-medium text-neutral-800 dark:text-neutral-100">{new Date(run.at).toLocaleString()}</dd>
                  </div>
                )}
              </dl>
              {selected.status === "pending" && (
                <div className="mt-3 space-y-2">
                  <p className="text-xs text-neutral-600 dark:text-neutral-300">
                    Not running. This is exactly what it would do -- read it, not the description:
                  </p>
                  <pre className="overflow-x-auto rounded-lg bg-neutral-900 p-2 text-[11px] text-neutral-100">
                    {JSON.stringify({ capability: selected.capability, options: selected.options ?? null }, null, 2)}
                  </pre>
                  <div className="flex gap-2">
                    <button type="button" onClick={() => act(selected.file, "approve")}
                      className="rounded-lg bg-neutral-900 px-3 py-1 text-xs text-white dark:bg-neutral-100 dark:text-neutral-900">
                      Approve
                    </button>
                    <button type="button" onClick={() => act(selected.file, "discard")}
                      className="rounded-lg border border-neutral-300 px-3 py-1 text-xs text-neutral-700 dark:border-neutral-700 dark:text-neutral-300">
                      Discard
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </ResizableAside>
  );
}
