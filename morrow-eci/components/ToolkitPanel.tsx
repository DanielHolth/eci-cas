"use client";

import { useMemo, useState } from "react";
import { ResizableAside } from "@/components/ResizableAside";

type ToolkitStatus = "running" | "finished" | "ready" | "disabled" | "idle";

type ToolkitItem = {
  id: string;
  name: string;
  description: string;
  status: ToolkitStatus;
  lastStatus: string;
  updatedAt: string;
};

function sortToolkits(items: ToolkitItem[]) {
  return [...items].sort((a, b) => {
    const rank = (item: ToolkitItem) => {
      if (item.status === "running") return 0;
      if (item.status === "finished") return 1;
      return 2;
    };

    const byRank = rank(a) - rank(b);
    if (byRank !== 0) return byRank;

    if (rank(a) === 1 && rank(b) === 1) {
      return b.updatedAt.localeCompare(a.updatedAt);
    }

    return b.updatedAt.localeCompare(a.updatedAt);
  });
}

export function ToolkitPanel({ enablePowerShell, onClose }: { enablePowerShell: boolean; onClose: () => void }) {
  const [selectedId, setSelectedId] = useState<string>("toolkit-guide");

  const tools = useMemo<ToolkitItem[]>(() => {
    const items: ToolkitItem[] = [
      {
        id: "toolkit-guide",
        name: "Toolkit Guide",
        description: "Explains what toolkits are available and how Morrow can use them.",
        status: "ready",
        lastStatus: "ready",
        updatedAt: "2026-09-15T09:00:00Z",
      },
      {
        id: "powershell-toolkit",
        name: "PowerShell Toolkit",
        description: "Runs PowerShell commands for the preview environment and reports the result back as a perception event.",
        status: enablePowerShell ? "ready" : "disabled",
        lastStatus: enablePowerShell ? "ready" : "disabled",
        updatedAt: enablePowerShell ? "2026-09-15T09:05:00Z" : "2026-09-15T08:45:00Z",
      },
    ];
    return sortToolkits(items);
  }, [enablePowerShell]);

  const selected = tools.find((tool) => tool.id === selectedId) ?? tools[0];

  return (
    <ResizableAside side="left" title="Toolkit" onClose={onClose}>
      <div className="flex h-full flex-col bg-white dark:bg-neutral-950">
        <div className="border-b border-neutral-200 px-3 py-2 dark:border-neutral-800">
          <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-neutral-500 dark:text-neutral-400">
            Available toolkits
          </p>
        </div>

        <div className="flex flex-1 flex-col gap-3 overflow-y-auto p-3">
          <ul className="space-y-2">
            {tools.map((tool) => (
              <li key={tool.id}>
                <button
                  type="button"
                  onClick={() => setSelectedId(tool.id)}
                  className={`w-full rounded-xl border p-2 text-left transition ${
                    selected.id === tool.id
                      ? "border-neutral-900 bg-neutral-100 dark:border-neutral-600 dark:bg-neutral-900"
                      : "border-neutral-200 bg-white hover:bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950 dark:hover:bg-neutral-900"
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-sm font-medium text-neutral-800 dark:text-neutral-100">{tool.name}</span>
                    <span className="rounded-full border border-neutral-300 px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-neutral-700 dark:border-neutral-700 dark:text-neutral-300">
                      {tool.status}
                    </span>
                  </div>
                  <p className="mt-1 text-xs text-neutral-600 dark:text-neutral-400">{tool.description}</p>
                </button>
              </li>
            ))}
          </ul>

          <div className="rounded-xl border border-neutral-200 bg-neutral-50 p-3 dark:border-neutral-800 dark:bg-neutral-900">
            <div className="mb-2 flex items-center justify-between">
              <h3 className="text-sm font-semibold text-neutral-800 dark:text-neutral-100">{selected.name}</h3>
              <span className="rounded-full border border-neutral-300 px-2 py-0.5 text-[10px] uppercase tracking-wide text-neutral-700 dark:border-neutral-700 dark:text-neutral-300">
                {selected.status}
              </span>
            </div>

            <p className="text-xs leading-5 text-neutral-600 dark:text-neutral-300">{selected.description}</p>

            <dl className="mt-3 space-y-2 text-xs text-neutral-600 dark:text-neutral-300">
              <div className="flex justify-between gap-4">
                <dt>Current status</dt>
                <dd className="font-medium text-neutral-800 dark:text-neutral-100">{selected.status}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt>Last status</dt>
                <dd className="font-medium text-neutral-800 dark:text-neutral-100">{selected.lastStatus}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt>Updated</dt>
                <dd className="font-medium text-neutral-800 dark:text-neutral-100">{new Date(selected.updatedAt).toLocaleString()}</dd>
              </div>
            </dl>
          </div>
        </div>
      </div>
    </ResizableAside>
  );
}
