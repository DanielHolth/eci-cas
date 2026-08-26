"use client";

import { useState } from "react";
import type { BundleAgent, BundleFinding } from "@/types/events";

/** Colors for Analytics/Knowledge match tools/console.py's COLORS table
 * (blue / orange) so the two surfaces read as the same system. Personality
 * has no console color yet — fuchsia is a proposal, not settled
 * (see README "open questions"). */
const AGENT_STYLE: Record<BundleAgent, { dot: string; label: string }> = {
  analytics: { dot: "bg-blue-500", label: "Analytics" },
  personality: { dot: "bg-fuchsia-500", label: "Personality" },
  knowledge: { dot: "bg-orange-500", label: "Knowledge" },
};

function KnowledgeDetail({ nodes }: { nodes: NonNullable<BundleFinding["swarmNodes"]> }) {
  return (
    <div className="mt-1 flex flex-col gap-1 rounded-md border border-orange-200 bg-orange-50 p-2 text-xs text-orange-900">
      {nodes.map((n, i) => (
        <div key={i}>
          <span className="font-mono">{n.category}/{n.topic}</span>{" "}
          <span className="text-orange-600">({n.count})</span>
          {n.sample.length > 0 && (
            <span className="text-orange-700"> — {n.sample.join("; ")}</span>
          )}
        </div>
      ))}
    </div>
  );
}

export function ThoughtBubbles({
  findings,
  faded = false,
}: {
  findings: BundleFinding[];
  /** Bubbles persist faded after speaking, per §6's "faded, not gone". */
  faded?: boolean;
}) {
  const [expanded, setExpanded] = useState<BundleAgent | null>(null);

  return (
    <div
      className={`flex flex-col gap-2 transition-opacity duration-500 ${faded ? "opacity-40" : "opacity-100"}`}
    >
      {findings.map((f) => {
        const style = AGENT_STYLE[f.agent];
        const hasDetail = !!f.swarmNodes?.length;
        return (
          <div key={f.agent}>
            <button
              type="button"
              disabled={!hasDetail}
              onClick={() => setExpanded(expanded === f.agent ? null : f.agent)}
              className={`flex w-full items-center gap-2 rounded-full border border-neutral-200 bg-white px-3 py-1.5 text-sm shadow-sm text-left ${hasDetail ? "cursor-pointer hover:bg-neutral-50" : "cursor-default"}`}
            >
              <span className={`h-2.5 w-2.5 rounded-full ${style.dot}`} />
              <span className="font-medium text-neutral-500">{style.label}:</span>
              <span className="text-neutral-700">{f.text}</span>
              {hasDetail && <span className="ml-auto text-neutral-400">{expanded === f.agent ? "−" : "+"}</span>}
            </button>
            {expanded === f.agent && f.swarmNodes && <KnowledgeDetail nodes={f.swarmNodes} />}
          </div>
        );
      })}
    </div>
  );
}
